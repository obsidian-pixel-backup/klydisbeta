using System;
using System.Collections.Generic;

namespace Klydis.Core.Chat;

/// <summary>
/// Compact projection of the agent's current state for model context injection.
/// This is the ONLY thing the model receives by default — full state stays
/// in the persistent store and is retrieved on demand.
/// </summary>
public sealed record AgentContextSnapshot
{
    public string Objective { get; init; } = string.Empty;
    public PlanSummary? Plan { get; init; }
    public TodoSummary? Todos { get; init; }
    public WorkspaceSummary? Workspace { get; init; }
    public CurrentTaskSummary? CurrentTask { get; init; }
    public IReadOnlyList<RecentEvidence> RecentEvidence { get; init; } = Array.Empty<RecentEvidence>();
    public IReadOnlyList<string> OpenIssues { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Formats the snapshot as a compact text block for injection into the model prompt.
    /// </summary>
    public string Format()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[AGENT STATE]");

        if (!string.IsNullOrWhiteSpace(Objective))
            sb.AppendLine($"  OBJECTIVE: {Objective}");

        if (CurrentTask != null)
            sb.AppendLine($"  CURRENT TASK: {CurrentTask.Id} \"{CurrentTask.Title}\" ({CurrentTask.Status})");

        if (Plan != null)
            sb.AppendLine($"  PLAN: {Plan.CompletedTasks}/{Plan.TotalTasks} tasks completed"
                + (Plan.CurrentTaskTitle != null ? $" - current: '{Plan.CurrentTaskTitle}'" : ""));

        if (Todos != null)
            sb.AppendLine($"  TODO: {Todos.Completed} completed, {Todos.Remaining} remaining"
                + (Todos.Blocked > 0 ? $", {Todos.Blocked} blocked" : ""));

        if (Workspace != null)
        {
            sb.AppendLine($"  WORKSPACE: {Workspace.Root}");
            if (Workspace.ModifiedFileCount > 0)
                sb.AppendLine($"  MODIFIED FILES: {Workspace.ModifiedFileCount}");
        }

        if (RecentEvidence.Count > 0)
        {
            sb.AppendLine($"  RECENT EVIDENCE:");
            foreach (var e in RecentEvidence)
                sb.AppendLine($"    {(e.Passed ? "[OK]" : "[FAIL]")} {e.Kind}: {e.Subject}");
        }

        if (OpenIssues.Count > 0)
        {
            sb.AppendLine($"  OPEN ISSUES:");
            foreach (var issue in OpenIssues)
                sb.AppendLine($"    - {issue}");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>Compact plan state for model context.</summary>
public sealed record PlanSummary(
    string Objective,
    int TotalTasks,
    int CompletedTasks,
    string? CurrentTaskId,
    string? CurrentTaskTitle,
    IReadOnlyList<string> NextTaskIds);

/// <summary>Compact TODO state for model context.</summary>
public sealed record TodoSummary(
    int Completed,
    int Remaining,
    int Blocked,
    string? CurrentTodoTitle);

/// <summary>Compact current task state for model context.</summary>
public sealed record CurrentTaskSummary(
    string Id,
    string Title,
    string Status,
    IReadOnlyList<string> RecentToolCalls);

/// <summary>Compact workspace state for model context.</summary>
public sealed record WorkspaceSummary(
    string Root,
    string? Scratch,
    string? Artifacts,
    int ModifiedFileCount);

/// <summary>Compact evidence entry for model context.</summary>
public sealed record RecentEvidence(
    string Kind,
    string Subject,
    bool Passed,
    string? Detail);
