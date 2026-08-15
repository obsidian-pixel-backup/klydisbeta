using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Learning;

/// <summary>
/// Cross-session adaptive learning. The system's self-correction machinery (loop detection,
/// tool-call parse escalation, rescue mode, guardrails) currently forgets everything the moment
/// a turn ends — the same model repeats the same mistakes in every session. This service
/// persists those correction events as <b>lessons</b> and feeds them back:
///  - auto-correction events are recorded with a dedup key (model + type + normalized content),
///    so recurrence signals survive (use_count grows) without flooding the store;
///  - the most relevant lessons are injected into the system prompt as a bounded
///    LESSONS LEARNED section, so a fresh session starts with the accumulated experience;
///  - per-model behavior counters drive adaptive decisions (e.g. switching a model that keeps
///    failing the native tool-call format over to the JSON format automatically);
///  - the model itself can persist explicit lessons via the learn_lesson / recall_lessons tools,
///    making the model a first-class participant in the ecosystem's growth.
/// </summary>
public sealed class AdaptiveLearningService
{
    public const string TypeAutoCorrection = "auto_correction";
    public const string TypeModelBehavior = "model_behavior";
    public const string TypeExplicit = "explicit";
    public const string TypeToolFailure = "tool_failure";

    private readonly MessageStore _store;
    private readonly ILogger<AdaptiveLearningService>? _logger;

    // In-memory throttle: identical lessons recorded within a short window are suppressed to
    // prevent a loop-heavy session from hammering the DB with the same correction.
    private readonly ConcurrentDictionary<string, DateTime> _recentRecordings = new();
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(10);

    public AdaptiveLearningService(MessageStore store, ILogger<AdaptiveLearningService>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Derives a stable model identity from a model file path ("qwen3.6-14b-a3b-fablevibes-q4_k_m").
    /// Falls back to the full path when no file name is available.
    /// </summary>
    public static string DeriveModelName(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return "unknown-model";
        var name = Path.GetFileNameWithoutExtension(modelPath);
        return string.IsNullOrWhiteSpace(name) ? modelPath : name.ToLowerInvariant();
    }

    /// <summary>
    /// Records a lesson. Deduplicated in the store; identical content recorded again within the
    /// throttle window is skipped entirely (counted only once per 10 minutes per model).
    /// </summary>
    public async Task RecordLessonAsync(
        string? modelPath,
        string type,
        string content,
        string? source = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        string model = DeriveModelName(modelPath);

        // Behavior counters (model_behavior) intentionally bypass the throttle: each occurrence
        // must increment the stored use_count so the adaptive threshold is reached promptly.
        // Narrative lessons (auto_correction/explicit) are throttled to avoid loop-spam.
        if (type != TypeModelBehavior)
        {
            string throttleKey = $"{model}|{type}|{content.Trim().ToLowerInvariant()}";
            if (_recentRecordings.TryGetValue(throttleKey, out var last) &&
                DateTime.UtcNow - last < ThrottleWindow)
            {
                return;
            }
            _recentRecordings[throttleKey] = DateTime.UtcNow;
        }

        try
        {
            await _store.AddLessonAsync(model, type, content, source).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist lesson (type {Type}) for model {Model}.", type, model);
        }
    }

    /// <summary>
    /// Records a self-correction event (loop detector, empty-response correction, rescue mode,
    /// parse-failure escalation, guardrail block) as an auto_correction lesson.
    /// </summary>
    public Task RecordCorrectionAsync(string? modelPath, string source, string detail, CancellationToken ct = default)
        => RecordLessonAsync(modelPath, TypeAutoCorrection, detail, source, ct);

    /// <summary>
    /// Records that a model failed the native &lt;function=&gt; tool-call format. Two or more
    /// occurrences (across any sessions) flip <see cref="HasNativeToolFormatIssuesAsync"/>.
    /// </summary>
    public Task RecordNativeToolFormatFailureAsync(string? modelPath, CancellationToken ct = default)
        => RecordLessonAsync(modelPath, TypeModelBehavior,
            "Model fails to complete native <tool_call><function=...><parameter=...> tool calls; JSON format instructions produce better results.",
            "native_tool_format_failure", ct);

    /// <summary>
    /// True when a model has demonstrated repeated trouble with the native qwen tool-call
    /// format. ChatEngine uses this to automatically fall back to the JSON format for that model.
    /// </summary>
    public async Task<bool> HasNativeToolFormatIssuesAsync(string? modelPath, CancellationToken ct = default)
    {
        try
        {
            string model = DeriveModelName(modelPath);
            int count = await _store.GetLessonCountAsync(model, TypeModelBehavior).ConfigureAwait(false);
            return count >= 2;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read model behavior lessons; assuming no native-format issues.");
            return false;
        }
    }

    /// <summary>
    /// Builds a compact, bounded LESSONS LEARNED section for prompt injection. Empty when there
    /// is nothing relevant. Lessons are ordered by recurrence (use_count) then recency, and the
    /// output is capped at <paramref name="maxChars"/>.
    /// </summary>
    public async Task<string> BuildLessonsSectionAsync(string? modelPath, int maxChars = 700, CancellationToken ct = default)
    {
        try
        {
            string model = DeriveModelName(modelPath);
            var lessons = await _store.GetRecentLessonsAsync(modelName: model, limit: 8).ConfigureAwait(false);
            if (lessons.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("### LESSONS LEARNED (persistent, from previous sessions with this model)");
            foreach (var lesson in lessons)
            {
                if (sb.Length >= maxChars) break;
                string sourceTag = string.IsNullOrWhiteSpace(lesson.Source)
                    ? lesson.Type
                    : lesson.Source;
                string recurrence = lesson.UseCount > 1 ? $" (recurred {lesson.UseCount}x)" : "";
                sb.AppendLine($"- [{sourceTag}{recurrence}] {lesson.Content.Trim()}");
            }
            return sb.Length > maxChars ? sb.ToString()[..maxChars] : sb.ToString();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to build lessons section; omitting it from the prompt.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns lessons as plain text for the recall_lessons tool — the model's own way to
    /// query accumulated knowledge mid-task.
    /// </summary>
    public async Task<string> RecallLessonsTextAsync(string? modelPath, int limit = 8, CancellationToken ct = default)
    {
        try
        {
            string model = DeriveModelName(modelPath);
            var lessons = await _store.GetRecentLessonsAsync(modelName: model, limit: limit).ConfigureAwait(false);
            if (lessons.Count == 0) return "No lessons recorded for this model yet.";

            var sb = new StringBuilder();
            foreach (var lesson in lessons)
            {
                string sourceTag = string.IsNullOrWhiteSpace(lesson.Source) ? lesson.Type : lesson.Source;
                sb.AppendLine($"- [{sourceTag}] {lesson.Content.Trim()}");
            }
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to recall lessons.");
            return $"Failed to recall lessons: {ex.Message}";
        }
    }
}
