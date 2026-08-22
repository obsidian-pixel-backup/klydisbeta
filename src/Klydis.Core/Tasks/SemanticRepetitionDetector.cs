using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Klydis.Core.Tasks;

/// <summary>
/// A semantic fingerprint of an action that normalizes equivalent command variations.
/// </summary>
public sealed record ActionFingerprintKey(
    string ToolName,
    string NormalizedTarget,
    string NormalizedIntent);

/// <summary>
/// Progress events proving real factual advancement.
/// </summary>
public enum ProgressEventType
{
    WorkItemCompleted,
    EvidenceAdded,
    ArtifactCreated,
    StateChanged,
    GoalProgressChanged
}

public sealed record ProgressEvent(
    ProgressEventType Type,
    string Detail,
    DateTimeOffset Timestamp);

/// <summary>
/// Detects semantic repetition and stalled loops across turns.
/// </summary>
public class SemanticRepetitionDetector
{
    private readonly List<(ActionFingerprintKey Key, bool Success)> _actionHistory = new();
    private readonly List<ProgressEvent> _progressEvents = new();
    private readonly object _lock = new();

    public void RecordAction(string toolName, IDictionary<string, object>? args, bool success)
    {
        var fp = GenerateFingerprint(toolName, args);
        lock (_lock)
        {
            _actionHistory.Add((fp, success));
        }
    }

    public void RecordProgress(ProgressEventType type, string detail)
    {
        lock (_lock)
        {
            _progressEvents.Add(new ProgressEvent(type, detail, DateTimeOffset.UtcNow));
        }
    }

    public bool IsStuckInLoop(out string reflectionGuidance)
    {
        lock (_lock)
        {
            if (_actionHistory.Count < 2)
            {
                reflectionGuidance = string.Empty;
                return false;
            }

            // Check if the last 2 or 3 actions are semantically identical and failed
            var tail = _actionHistory.TakeLast(3).ToList();
            if (tail.Count >= 2 && tail.All(t => !t.Success))
            {
                var firstKey = tail[0].Key;
                if (tail.All(t => t.Key == firstKey))
                {
                    reflectionGuidance = FormatReflectionGuidance(firstKey.ToolName, "The exact/equivalent action failed multiple times consecutively.");
                    return true;
                }
            }

            // Check if the last 3 actions produced zero progress events
            if (_actionHistory.Count >= 3)
            {
                var recentProgress = _progressEvents.Where(p => p.Timestamp >= DateTimeOffset.UtcNow.AddMinutes(-5)).ToList();
                if (recentProgress.Count == 0 && tail.Count >= 3)
                {
                    reflectionGuidance = FormatReflectionGuidance(tail.Last().Key.ToolName, "No progress events recorded across recent actions.");
                    return true;
                }
            }

            reflectionGuidance = string.Empty;
            return false;
        }
    }

    public static ActionFingerprintKey GenerateFingerprint(string toolName, IDictionary<string, object>? args)
    {
        string canonicalTool = (toolName ?? "").Trim().ToLowerInvariant();
        string target = "";
        string intent = "";

        if (args != null)
        {
            if (args.TryGetValue("command", out var cmdObj))
            {
                string cmd = cmdObj?.ToString() ?? "";
                (target, intent) = NormalizeCommand(cmd);
            }
            else if (args.TryGetValue("path", out var pathObj))
            {
                target = (pathObj?.ToString() ?? "").Trim().ToLowerInvariant().Replace('\\', '/');
                intent = canonicalTool;
            }
            else if (args.TryGetValue("query", out var queryObj))
            {
                target = (queryObj?.ToString() ?? "").Trim().ToLowerInvariant();
                intent = "search";
            }
            else if (args.TryGetValue("url", out var urlObj))
            {
                target = (urlObj?.ToString() ?? "").Trim().ToLowerInvariant();
                intent = "crawl";
            }
        }

        return new ActionFingerprintKey(canonicalTool, target, intent);
    }

    private static (string Target, string Intent) NormalizeCommand(string cmd)
    {
        string trimmed = cmd.Trim();
        string lower = trimmed.ToLowerInvariant();

        // Strip powershell / cmd wrappers
        lower = Regex.Replace(lower, @"^(?:powershell(?:\.exe)?|pwsh(?:\.exe)?|cmd(?:\.exe)?)\s+(?:-command\s+|-c\s+|/c\s+)?", "");
        lower = lower.Trim('"', '\'', ' ');

        // Normalize directory listing commands
        if (lower.StartsWith("dir") || lower.StartsWith("get-childitem") || lower.StartsWith("gci") || lower.StartsWith("ls"))
        {
            string path = Regex.Replace(lower, @"^(?:dir|get-childitem|gci|ls)\s*", "").Trim();
            return (path.Replace('\\', '/'), "list_directory");
        }

        // Normalize process queries
        if (lower.StartsWith("get-process") || lower.StartsWith("ps") || lower.StartsWith("tasklist"))
        {
            return ("processes", "get_processes");
        }

        // Normalize system diagnostics
        if (lower.Contains("nvidia-smi"))
        {
            return ("gpu", "get_gpu_metrics");
        }
        if (lower.Contains("wmic cpu") || lower.Contains("get-wmiobject win32_processor"))
        {
            return ("cpu", "get_cpu_metrics");
        }

        return (lower, "execute");
    }

    private static string FormatReflectionGuidance(string toolName, string reason)
    {
        return $"[SYSTEM — STUCK LOOP DETECTED]\n" +
               $"Reason: {reason} (Tool: {toolName})\n" +
               $"You must change strategy and choose one of the following alternatives:\n" +
               $"1. Change command syntax or arguments.\n" +
               $"2. Use a different tool (e.g. system_* tools instead of raw shell, or read_file instead of run_command).\n" +
               $"3. Inspect environment or directory contents first.\n" +
               $"4. Decompose the task into smaller verifiable steps.\n" +
               $"5. If blocked by missing prerequisites, mark the step blocked and proceed with remaining work.";
    }

    public void Reset()
    {
        lock (_lock)
        {
            _actionHistory.Clear();
            _progressEvents.Clear();
        }
    }
}
