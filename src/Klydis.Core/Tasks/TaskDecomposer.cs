using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Decomposes explicit numbered requirements, bulleted actions, and multi-part user requests
/// into discrete work items and <see cref="TaskStep"/> instances.
/// Eliminates the need for local models to rediscover or plan obvious user-provided task lists.
/// </summary>
public static class TaskDecomposer
{
    private static readonly Regex NumberedItemRegex = new(
        @"^\s*(?:(?:(?:\d{1,3}|[a-zA-Z])[\.\)\:-])|\#\d{1,3}|\b(?:step|task|item)\s*\d{1,3}[\.\)\:-]?)\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BulletItemRegex = new(
        @"^\s*[\-\*\•\–\—]\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Checks if a user message contains explicit multi-part or numbered action tasks.
    /// </summary>
    public static bool ContainsDecomposableTasks(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var items = Decompose(message);
        return items.Count >= 2;
    }

    /// <summary>
    /// Decomposes a user message into a list of concrete task descriptions.
    /// </summary>
    public static IReadOnlyList<string> Decompose(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return Array.Empty<string>();

        var results = new List<string>();
        string[] lines = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            var match = NumberedItemRegex.Match(trimmed);
            if (match.Success)
            {
                string content = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    results.Add(NormalizeTaskItem(content));
                    continue;
                }
            }

            var bulletMatch = BulletItemRegex.Match(trimmed);
            if (bulletMatch.Success)
            {
                string content = bulletMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    results.Add(NormalizeTaskItem(content));
                }
            }
        }

        // If line-by-line didn't match multiple items, check for inline numbered lists: "1. CPU 2. GPU 3. RAM"
        if (results.Count < 2)
        {
            var inlineMatches = Regex.Matches(message, @"(?:^|\s)(?:\d{1,2}[\.\)])\s*([^\d\.\)]+?)(?=(?:\s\d{1,2}[\.\)])|$)", RegexOptions.IgnoreCase);
            if (inlineMatches.Count >= 2)
            {
                results.Clear();
                foreach (Match m in inlineMatches)
                {
                    string content = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        results.Add(NormalizeTaskItem(content));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Decomposes a user message directly into durable <see cref="TaskStep"/> records.
    /// </summary>
    public static IReadOnlyList<TaskStep> DecomposeToSteps(string message, string? taskId = null)
    {
        var items = Decompose(message);
        if (items.Count == 0) return Array.Empty<TaskStep>();

        var steps = new List<TaskStep>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var entry = new ToolExecutor.PlanEntry(items[i], false);
            steps.Add(TaskStepBuilder.FromPlanEntry(entry, i, taskId));
        }
        return steps;
    }

    /// <summary>
    /// Normalizes a brief task description (e.g. "CPU" -> "Determine CPU utilization", "1. GPU" -> "Determine GPU utilization")
    /// to provide clear, actionable direction to both the scheduler and the model.
    /// </summary>
    public static string NormalizeTaskItem(string rawItem)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return string.Empty;
        string item = rawItem.Trim().TrimEnd('.', ';', ',');

        // Normalize common diagnostic short-hands into clear actionable statements
        string lower = item.ToLowerInvariant();
        if (lower is "cpu" or "cpu usage" or "cpu utilization" or "cpu load")
            return "Determine current CPU utilization";
        if (lower is "gpu" or "gpu usage" or "gpu utilization" or "gpu load" or "vram")
            return "Determine current GPU utilization and VRAM";
        if (lower is "ram" or "memory" or "ram usage" or "memory usage" or "ram load")
            return "Determine current RAM and memory utilization";
        if (lower is "disk" or "disk space" or "storage" or "hard drive" or "disk usage")
            return "Check disk space and drive utilization";
        if (lower is "os" or "operating system" or "os version" or "windows version")
            return "Determine operating system details and version";
        if (lower is "temperature" or "temperatures" or "temps" or "thermal")
            return "Check hardware temperatures and thermal status";
        if (lower is "process count" or "processes" or "process list")
            return "Inspect running processes and process count";
        if (lower is "top cpu" or "top cpu processes")
            return "Identify top processes consuming CPU";
        if (lower is "top memory" or "top ram" or "top memory processes")
            return "Identify top processes consuming memory";
        if (lower is "gpu processes")
            return "Identify processes running on GPU";
        if (lower is "uptime" or "system uptime")
            return "Determine system uptime and boot time";
        if (lower is "hardware report" or "hardware info")
            return "Generate comprehensive hardware report";
        if (lower is "software report" or "software info" or "installed software")
            return "Generate comprehensive software and environment report";

        return item;
    }
}
