using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Chat;

/// <summary>
/// Task lifecycle status owned by the HARNESS. A model turn ending (CompletedGeneration)
/// has zero authority over this enum — only the supervisor's checks (queue drained, plan
/// checklist verified) may move a task to <see cref="TaskStatus.Completed"/>.
/// </summary>
public enum TaskStatus
{
    Pending,
    Running,
    Waiting,
    Blocked,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// The continuation contract: structured execution state derived from DURABLE sources
/// (the plan checklist, the message queue), never from a model-written narrative summary.
/// Rolling compaction preserves the conversation; this contract preserves the execution.
/// A compacted window that omits this block loses "D = REQUIRED, D = NOT COMPLETE" —
/// the exact failure mode that makes long-horizon agents unreliable.
/// </summary>
public sealed record ExecutionStateContract(
    string Objective,
    TaskStatus Status,
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> InProgress,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Blocked,
    int PendingQueueItems,
    string? CurrentStep,
    string? NextRequiredAction,
    IReadOnlyList<string> CompletionCriteria);

/// <summary>
/// Builds and formats <see cref="ExecutionStateContract"/> deterministically from the
/// durable plan checklist and message queue. The first open item is the current step;
/// the remaining open items are pending. The completion criteria ARE the full checklist —
/// so "done" is externally verifiable, not a model opinion.
/// </summary>
public static class ContinuationContractBuilder
{
    public static ExecutionStateContract Build(
        string objective,
        IReadOnlyList<ToolExecutor.PlanEntry> planEntries,
        int pendingQueueItems)
    {
        var done = planEntries.Where(e => e.Done).Select(e => e.Text).ToList();
        var open = planEntries.Where(e => !e.Done).Select(e => e.Text).ToList();
        string? current = open.FirstOrDefault();

        var status = planEntries.Count > 0 && open.Count == 0
            ? TaskStatus.Completed
            : TaskStatus.Running;

        string? nextAction = current != null
            ? $"finish \"{current}\" and check it off with the 'plan' tool"
            : pendingQueueItems > 0
                ? "incorporate the queued message(s) with 'incorporate_queued_message'"
                : "call 'task_complete' to seal completion (all acceptance criteria are met)";

        return new ExecutionStateContract(
            Objective: objective ?? string.Empty,
            Status: status,
            Completed: done,
            InProgress: current != null ? new[] { current } : Array.Empty<string>(),
            Pending: open.Skip(1).ToList(),
            Blocked: Array.Empty<string>(),
            PendingQueueItems: pendingQueueItems,
            CurrentStep: current,
            NextRequiredAction: nextAction,
            CompletionCriteria: planEntries.Select(e => e.Text).ToList());
    }

    /// <summary>
    /// Renders the contract as a labeled text block for injection into the model window.
    /// The objective line is omitted when empty (ChatEngine injects this per-iteration
    /// without a goal string; the GoalOrchestrator supplies it in continuation prompts).
    /// </summary>
    public static string Format(ExecutionStateContract c)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[EXECUTION STATE — authoritative continuation contract]");
        sb.AppendLine($"  Status: {c.Status}");
        if (!string.IsNullOrWhiteSpace(c.Objective))
            sb.AppendLine($"  Objective: {c.Objective}");
        sb.AppendLine($"  Completed ({c.Completed.Count}): {(c.Completed.Count > 0 ? string.Join("; ", c.Completed) : "—")}");
        sb.AppendLine($"  In Progress: {(c.InProgress.Count > 0 ? string.Join("; ", c.InProgress) : "—")}");
        sb.AppendLine($"  Pending ({c.Pending.Count}): {(c.Pending.Count > 0 ? string.Join("; ", c.Pending) : "—")}");
        sb.AppendLine($"  Blocked ({c.Blocked.Count}): {(c.Blocked.Count > 0 ? string.Join("; ", c.Blocked) : "—")}");
        sb.AppendLine($"  Queued messages pending: {c.PendingQueueItems}");
        sb.AppendLine($"  Current step: {c.CurrentStep ?? "—"}");
        sb.AppendLine($"  Next required action: {c.NextRequiredAction ?? "—"}");
        sb.AppendLine($"  Completion criteria ({c.CompletionCriteria.Count}): {(c.CompletionCriteria.Count > 0 ? string.Join("; ", c.CompletionCriteria) : "—")}");
        sb.AppendLine("  The task is NOT complete until every completion criterion is verified by the harness.");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The continuation supervisor: after every model turn it checks durable state (queue,
/// plan checklist) and decides whether the task continues — regardless of what the model
/// said. A model's "I'm done" claim has no authority over task completion; the harness
/// determines it.
/// </summary>
public readonly record struct TaskContinuationVerdict(TaskStatus Status, string Reason, bool Continue);

public static class GoalSupervisor
{
    /// <summary>
    /// Evaluates continuation after a turn. <paramref name="modelClaimedComplete"/> is true
    /// only when the model called task_complete AND the deterministic gate accepted it.
    /// The verdict drives the loop: Continue=true keeps the run alive.
    /// </summary>
    public static TaskContinuationVerdict EvaluateContinuation(
        bool modelClaimedComplete,
        (int Total, int Completed) plan,
        int pendingQueueItems)
    {
        int openCount = plan.Total > 0 ? plan.Total - plan.Completed : 0;

        if (modelClaimedComplete)
        {
            return openCount == 0
                ? new TaskContinuationVerdict(TaskStatus.Completed,
                    "Acceptance criteria verified: every plan item is checked off.", false)
                : new TaskContinuationVerdict(TaskStatus.Running,
                    $"task_complete was claimed but {openCount} plan item(s) still open — the claim has no authority over task completion.", true);
        }

        if (openCount > 0)
            return new TaskContinuationVerdict(TaskStatus.Running,
                $"{openCount} plan item(s) still open — task remains ACTIVE.", true);

        if (pendingQueueItems > 0)
            return new TaskContinuationVerdict(TaskStatus.Running,
                $"{pendingQueueItems} queued message(s) still pending — task remains ACTIVE.", true);

        if (plan.Total > 0)
            return new TaskContinuationVerdict(TaskStatus.Waiting,
                "All plan items are complete but the model stopped without sealing completion — direct it to call task_complete.", true);

        return new TaskContinuationVerdict(TaskStatus.Running, "Task in progress.", true);
    }
}
