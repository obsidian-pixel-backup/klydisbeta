using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klydis.Core.Tasks;

public enum ObjectiveStatus
{
    Pending,
    InProgress,
    Completed,
    Blocked,
    Failed
}

public sealed record ObjectiveEvidenceItem(
    string ToolExecutionId,
    string Source,
    string Value,
    DateTime TimestampUtc);

public sealed class TaskObjective
{
    public string Id { get; init; }
    public string Description { get; init; }
    public ObjectiveStatus Status { get; set; } = ObjectiveStatus.Pending;
    public int Attempts { get; set; } = 0;
    public int ExplorationActionsUsed { get; set; } = 0;
    public int MaxExplorationBudget { get; set; } = 3;
    public string? BlockedReason { get; set; }
    public List<ObjectiveEvidenceItem> Evidence { get; } = new();
    public IReadOnlyList<string> PreferredCapabilities { get; init; }

    public TaskObjective(
        string id,
        string description,
        IReadOnlyList<string>? preferredCapabilities = null,
        int maxExplorationBudget = 3)
    {
        Id = id;
        Description = description;
        PreferredCapabilities = preferredCapabilities ?? Array.Empty<string>();
        MaxExplorationBudget = maxExplorationBudget;
    }

    public void AddEvidence(string source, string value, string? executionId = null)
    {
        Evidence.Add(new ObjectiveEvidenceItem(
            ToolExecutionId: executionId ?? $"E-{Guid.NewGuid():N}"[..8],
            Source: source,
            Value: value,
            TimestampUtc: DateTime.UtcNow));
        Status = ObjectiveStatus.Completed;
    }

    public void MarkBlocked(string reason)
    {
        Status = ObjectiveStatus.Blocked;
        BlockedReason = reason;
    }
}

/// <summary>
/// Structured Task Graph managing discrete sub-objectives, evidence collection,
/// per-objective exploration budgets, and independent objective progression.
/// </summary>
public sealed class TaskObjectiveGraph
{
    public string GoalId { get; }
    public string GoalObjective { get; }
    public List<TaskObjective> Objectives { get; } = new();

    public TaskObjectiveGraph(string goalId, string goalObjective, IEnumerable<TaskObjective>? objectives = null)
    {
        GoalId = goalId;
        GoalObjective = goalObjective;
        if (objectives != null)
        {
            Objectives.AddRange(objectives);
        }
    }

    public TaskObjective? GetActiveObjective()
    {
        return Objectives.FirstOrDefault(o => o.Status == ObjectiveStatus.InProgress)
            ?? Objectives.FirstOrDefault(o => o.Status == ObjectiveStatus.Pending);
    }

    public TaskObjective? FindById(string id)
    {
        return Objectives.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public void RecordAction(string toolName, bool success, string? output = null, string? executionId = null)
    {
        var active = GetActiveObjective();
        if (active == null) return;

        active.Attempts++;
        if (active.Status == ObjectiveStatus.Pending)
        {
            active.Status = ObjectiveStatus.InProgress;
        }

        bool isExploratory = toolName is "list_directory" or "search_files";
        if (isExploratory)
        {
            active.ExplorationActionsUsed++;
            if (active.ExplorationActionsUsed > active.MaxExplorationBudget)
            {
                active.MarkBlocked($"Exploration budget exhausted ({active.ExplorationActionsUsed} actions without evidence).");
                return;
            }
        }

        // Automatic objective contribution matching
        if (success && !string.IsNullOrWhiteSpace(output))
        {
            if (EvaluateObjectiveContribution(active, toolName, output) is { } evidence)
            {
                active.AddEvidence(toolName, evidence, executionId);
            }
        }
    }

    private static string? EvaluateObjectiveContribution(TaskObjective objective, string toolName, string output)
    {
        string desc = objective.Description.ToLowerInvariant();
        string lowerOut = output.ToLowerInvariant();

        if (desc.Contains("cpu") && (toolName.Contains("cpu") || toolName == "system_report" || lowerOut.Contains("cpu")))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }
        if (desc.Contains("gpu") && (toolName.Contains("gpu") || toolName == "system_report" || lowerOut.Contains("gpu") || lowerOut.Contains("nvidia")))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }
        if ((desc.Contains("ram") || desc.Contains("memory")) && (toolName.Contains("memory") || toolName == "system_report" || lowerOut.Contains("memory") || lowerOut.Contains("ram")))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }
        if ((desc.Contains("disk") || desc.Contains("storage")) && (toolName.Contains("disk") || toolName == "system_report" || lowerOut.Contains("drive")))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }
        if (desc.Contains("os") || desc.Contains("operating system") && (toolName.Contains("os") || toolName == "system_report" || lowerOut.Contains("windows")))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }
        if (desc.Contains("process") && (toolName.Contains("process") || toolName == "system_report"))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }
        if (desc.Contains("uptime") && (toolName.Contains("uptime") || toolName == "system_report"))
        {
            return output.Length > 300 ? output[..300] + "..." : output;
        }

        return null;
    }

    public bool IsGoalComplete()
    {
        if (Objectives.Count == 0) return true;
        return Objectives.All(o => o.Status is ObjectiveStatus.Completed or ObjectiveStatus.Blocked or ObjectiveStatus.Failed);
    }

    public int CompletedCount => Objectives.Count(o => o.Status == ObjectiveStatus.Completed);
    public int BlockedCount => Objectives.Count(o => o.Status == ObjectiveStatus.Blocked);
    public int InProgressCount => Objectives.Count(o => o.Status == ObjectiveStatus.InProgress);
    public int PendingCount => Objectives.Count(o => o.Status == ObjectiveStatus.Pending);

    public string FormatProgressSummary()
    {
        return $"Progress: {CompletedCount}/{Objectives.Count} completed, {BlockedCount} blocked, {InProgressCount + PendingCount} remaining.";
    }

    /// <summary>
    /// Decomposes a multi-part prompt into a TaskObjectiveGraph.
    /// </summary>
    public static TaskObjectiveGraph CreateFromPrompt(string goalId, string prompt)
    {
        var rawItems = TaskDecomposer.Decompose(prompt);
        var objectives = new List<TaskObjective>();

        if (rawItems.Count >= 2)
        {
            for (int i = 0; i < rawItems.Count; i++)
            {
                string id = $"T{(i + 1):D2}";
                objectives.Add(new TaskObjective(id, rawItems[i]));
            }
        }
        else
        {
            objectives.Add(new TaskObjective("T01", prompt));
        }

        return new TaskObjectiveGraph(goalId, prompt, objectives);
    }
}
