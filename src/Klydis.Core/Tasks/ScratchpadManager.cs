using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// A named section within structured scratchpad reasoning.
/// </summary>
public sealed record ScratchpadSection(
    string SectionName,
    string Content,
    DateTime UpdatedAtUtc);

/// <summary>
/// Persistent structured internal reasoning state for a task.
/// Segregated from user deliverables and tool arguments to prevent hallucinated tool calls
/// and retain working cognitive state across multi-turn OODA-VR cycles.
/// </summary>
public sealed record ScratchpadState(
    string SessionId,
    string TaskId,
    string WorkingHypothesis,
    IReadOnlyList<string> KeyObservations,
    IReadOnlyList<string> NextActions,
    IReadOnlyList<string> VerificationChecklist,
    IReadOnlyList<string> LessonsLearned,
    string RawThinkingContent,
    DateTime LastUpdatedUtc)
{
    public static ScratchpadState CreateEmpty(string sessionId, string taskId) => new(
        sessionId ?? string.Empty,
        taskId ?? string.Empty,
        string.Empty,
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        string.Empty,
        DateTime.UtcNow);
}

/// <summary>
/// Manages extraction, segregation, persistence, and prompt formatting of scratchpad reasoning.
/// </summary>
public sealed class ScratchpadManager
{
    private static readonly Regex ScratchpadCustomBlockRegex = new(
        @"(?:\[SCRATCHPAD\]|<scratchpad>)([\s\S]*?)(?:\[/SCRATCHPAD\]|</scratchpad>|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThinkingBlockRegex = new(
        @"<\|?(?:think|thought)\|?>([\s\S]*?)(?:</\|?(?:think|thought)\|?>|<\|/(?:think|thought)\|?>|$)|\[(?:THINK|THOUGHT)\]([\s\S]*?)(?:\[/(?:THINK|THOUGHT)\]|$)|<antml:thinking>([\s\S]*?)</antml:thinking>|\{antml:thinking\}([\s\S]*?)\{/antml:thinking\}",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, ScratchpadState> _stateCache = new();

    private static string GetCacheKey(string? sessionId, string? taskId) => $"{sessionId ?? ""}:{taskId ?? ""}";

    /// <summary>
    /// Extracts thinking/scratchpad content from raw output, returning sanitized public text
    /// and the extracted scratchpad state.
    /// </summary>
    public (string PublicText, ScratchpadState UpdatedState) ProcessModelOutput(
        string rawOutput,
        string? sessionId,
        string? taskId)
    {
        return ExtractAndApplyThinking(rawOutput, sessionId, taskId);
    }

    /// <summary>
    /// Core extraction logic: strips thinking and custom scratchpad blocks, updating persistent state.
    /// </summary>
    public (string PublicText, ScratchpadState UpdatedState) ExtractAndApplyThinking(
        string rawOutput,
        string? sessionId,
        string? taskId)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return (string.Empty, GetState(sessionId, taskId));
        }

        var thinkingBuilder = new StringBuilder();

        // 1. Extract standard thinking tags (<think>...</think>, <antml:thinking>, etc.)
        var thinkMatches = ThinkingBlockRegex.Matches(rawOutput);
        foreach (Match match in thinkMatches)
        {
            for (int g = 1; g < match.Groups.Count; g++)
            {
                if (match.Groups[g].Success && !string.IsNullOrWhiteSpace(match.Groups[g].Value))
                {
                    thinkingBuilder.AppendLine(match.Groups[g].Value.Trim());
                    break;
                }
            }
        }

        // 2. Extract structured scratchpad custom blocks
        var customMatches = ScratchpadCustomBlockRegex.Matches(rawOutput);
        foreach (Match match in customMatches)
        {
            if (match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                thinkingBuilder.AppendLine(match.Groups[1].Value.Trim());
            }
        }

        // Strip custom and thinking blocks from public text
        string publicText = ScratchpadCustomBlockRegex.Replace(rawOutput, "");
        publicText = OutputSanitizer.StripThinkingBlocks(publicText);
        publicText = OutputSanitizer.SanitizeText(publicText);

        string rawThinking = thinkingBuilder.ToString().Trim();
        var currentState = GetState(sessionId, taskId);

        if (string.IsNullOrWhiteSpace(rawThinking))
        {
            return (publicText, currentState);
        }

        // Parse structured sections from extracted thinking
        var parsed = ParseSectionsFromThinking(rawThinking);

        var updated = new ScratchpadState(
            SessionId: sessionId ?? string.Empty,
            TaskId: taskId ?? string.Empty,
            WorkingHypothesis: !string.IsNullOrWhiteSpace(parsed.WorkingHypothesis)
                ? parsed.WorkingHypothesis
                : currentState.WorkingHypothesis,
            KeyObservations: parsed.KeyObservations.Count > 0
                ? MergeDistinct(currentState.KeyObservations, parsed.KeyObservations)
                : currentState.KeyObservations,
            NextActions: parsed.NextActions.Count > 0
                ? parsed.NextActions
                : currentState.NextActions,
            VerificationChecklist: parsed.VerificationChecklist.Count > 0
                ? MergeDistinct(currentState.VerificationChecklist, parsed.VerificationChecklist)
                : currentState.VerificationChecklist,
            LessonsLearned: parsed.LessonsLearned.Count > 0
                ? MergeDistinct(currentState.LessonsLearned, parsed.LessonsLearned)
                : currentState.LessonsLearned,
            RawThinkingContent: rawThinking,
            LastUpdatedUtc: DateTime.UtcNow);

        UpdateState(sessionId, taskId, updated);
        return (publicText, updated);
    }

    /// <summary>
    /// Gets the current scratchpad state for a task.
    /// </summary>
    public ScratchpadState GetState(string? sessionId, string? taskId)
    {
        string key = GetCacheKey(sessionId, taskId);
        return _stateCache.GetOrAdd(key, _ => ScratchpadState.CreateEmpty(sessionId ?? string.Empty, taskId ?? string.Empty));
    }

    /// <summary>
    /// Updates the scratchpad with explicit state delta and reflections.
    /// </summary>
    public void UpdateState(string? sessionId, string? taskId, ScratchpadState newState)
    {
        string key = GetCacheKey(sessionId, taskId);
        _stateCache[key] = newState;
    }

    /// <summary>
    /// Formats the current scratchpad for injection into the prompt during Orient/Decide phases,
    /// bounded by the specified max character budget.
    /// </summary>
    public string FormatForPrompt(string? sessionId, string? taskId, AgentLoopPhase phase, int maxChars = 2000)
    {
        var state = GetState(sessionId, taskId);
        bool hasData = !string.IsNullOrWhiteSpace(state.WorkingHypothesis) ||
                       state.KeyObservations.Count > 0 ||
                       state.NextActions.Count > 0 ||
                       state.VerificationChecklist.Count > 0 ||
                       state.LessonsLearned.Count > 0 ||
                       !string.IsNullOrWhiteSpace(state.RawThinkingContent);

        if (!hasData) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"[CURRENT SCRATCHPAD — Phase: {phase}]");

        if (!string.IsNullOrWhiteSpace(state.WorkingHypothesis))
        {
            sb.AppendLine($"Working Hypothesis: {state.WorkingHypothesis}");
        }

        if (state.KeyObservations.Count > 0)
        {
            sb.AppendLine("Key Observations:");
            foreach (var obs in state.KeyObservations.Take(5))
            {
                sb.AppendLine($"  - {obs}");
            }
        }

        if (state.NextActions.Count > 0)
        {
            sb.AppendLine("Next Actions:");
            foreach (var act in state.NextActions.Take(5))
            {
                sb.AppendLine($"  - {act}");
            }
        }

        if (state.VerificationChecklist.Count > 0)
        {
            sb.AppendLine("Verification Checklist:");
            foreach (var item in state.VerificationChecklist.Take(5))
            {
                sb.AppendLine($"  - [ ] {item}");
            }
        }

        if (state.LessonsLearned.Count > 0)
        {
            sb.AppendLine("Lessons Learned:");
            foreach (var lesson in state.LessonsLearned.Take(5))
            {
                sb.AppendLine($"  - {lesson}");
            }
        }

        if (sb.Length < 100 && !string.IsNullOrWhiteSpace(state.RawThinkingContent))
        {
            string summary = state.RawThinkingContent.Length > 500
                ? state.RawThinkingContent.Substring(0, 500) + "..."
                : state.RawThinkingContent;
            sb.AppendLine($"Recent Reasoning: {summary}");
        }

        string result = sb.ToString().Trim();
        if (result.Length > maxChars)
        {
            result = result.Substring(0, maxChars) + "\n...[scratchpad context truncated]";
        }

        return result;
    }

    /// <summary>
    /// Clears scratchpad state on task completion.
    /// </summary>
    public void Clear(string sessionId, string taskId)
    {
        string key = GetCacheKey(sessionId, taskId);
        _stateCache.TryRemove(key, out _);
    }

    private static (string WorkingHypothesis, List<string> KeyObservations, List<string> NextActions, List<string> VerificationChecklist, List<string> LessonsLearned)
        ParseSectionsFromThinking(string thinking)
    {
        string hypothesis = string.Empty;
        var observations = new List<string>();
        var actions = new List<string>();
        var checklist = new List<string>();
        var lessons = new List<string>();

        var lines = thinking.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        string currentSection = string.Empty;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (Regex.IsMatch(line, @"^#+\s*(?:Working\s+)?Hypothes(?:is|es)", RegexOptions.IgnoreCase))
            {
                currentSection = "hypothesis";
                continue;
            }
            if (Regex.IsMatch(line, @"^#+\s*(?:Key\s+)?Observations?", RegexOptions.IgnoreCase))
            {
                currentSection = "observations";
                continue;
            }
            if (Regex.IsMatch(line, @"^#+\s*(?:Next\s+)?Actions?|^#+\s*Plan", RegexOptions.IgnoreCase))
            {
                currentSection = "actions";
                continue;
            }
            if (Regex.IsMatch(line, @"^#+\s*Verification|^#+\s*Invariants?", RegexOptions.IgnoreCase))
            {
                currentSection = "checklist";
                continue;
            }
            if (Regex.IsMatch(line, @"^#+\s*Lessons?(?:\s+Learned)?|^#+\s*Reflections?", RegexOptions.IgnoreCase))
            {
                currentSection = "lessons";
                continue;
            }

            string cleanItem = Regex.Replace(line, @"^[-*•\d\.\s\[\]x]+", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanItem)) continue;

            switch (currentSection)
            {
                case "hypothesis":
                    if (string.IsNullOrEmpty(hypothesis)) hypothesis = cleanItem;
                    else hypothesis += " " + cleanItem;
                    break;
                case "observations":
                    observations.Add(cleanItem);
                    break;
                case "actions":
                    actions.Add(cleanItem);
                    break;
                case "checklist":
                    checklist.Add(cleanItem);
                    break;
                case "lessons":
                    lessons.Add(cleanItem);
                    break;
                default:
                    if (line.StartsWith("Hypothesis:", StringComparison.OrdinalIgnoreCase))
                    {
                        hypothesis = line.Substring("Hypothesis:".Length).Trim();
                    }
                    else if (line.StartsWith("Observation:", StringComparison.OrdinalIgnoreCase))
                    {
                        observations.Add(line.Substring("Observation:".Length).Trim());
                    }
                    else if (line.StartsWith("Action:", StringComparison.OrdinalIgnoreCase))
                    {
                        actions.Add(line.Substring("Action:".Length).Trim());
                    }
                    break;
            }
        }

        return (hypothesis, observations, actions, checklist, lessons);
    }

    private static IReadOnlyList<string> MergeDistinct(IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(existing);
        foreach (var item in incoming)
        {
            if (set.Add(item))
            {
                result.Add(item);
            }
        }
        return result;
    }
}
