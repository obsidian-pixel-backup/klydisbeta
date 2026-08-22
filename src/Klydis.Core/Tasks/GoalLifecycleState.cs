using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Authoritative state machine for a goal's lifecycle.
/// </summary>
public enum GoalLifecycleState
{
    Created,
    Planning,
    Ready,
    Running,
    WaitingTool,
    WaitingUser,
    Verifying,
    Blocked,
    Paused,
    Completing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// First-class goal runtime entity owning work items and execution status.
/// </summary>
public sealed class GoalEntity
{
    public required string Id { get; init; }
    public required string Objective { get; init; }
    public GoalLifecycleState State { get; set; } = GoalLifecycleState.Created;
    public GoalBudgetConfig BudgetConfig { get; init; } = GoalBudgetConfig.Default;
    public List<WorkItem> WorkItems { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public string? FinalSummary { get; set; }

    public bool IsActive => State switch
    {
        GoalLifecycleState.Created or
        GoalLifecycleState.Planning or
        GoalLifecycleState.Ready or
        GoalLifecycleState.Running or
        GoalLifecycleState.WaitingTool or
        GoalLifecycleState.Verifying or
        GoalLifecycleState.Completing => true,
        _ => false
    };

    public (int Total, int Completed, int Pending, int Failed, int Blocked) GetWorkItemCounts()
    {
        int total = WorkItems.Count;
        int completed = WorkItems.Count(w => w.State == WorkItemState.Completed);
        int failed = WorkItems.Count(w => w.State == WorkItemState.Failed);
        int blocked = WorkItems.Count(w => w.State == WorkItemState.Blocked);
        int pending = total - (completed + failed + blocked);
        return (total, completed, Math.Max(0, pending), failed, blocked);
    }
}

/// <summary>
/// Evaluator for verifying whether a goal satisfies all completion requirements.
/// Assertion alone ("Done", "I finished") is rejected unless backed by verified evidence.
/// </summary>
public interface IGoalCompletionEvaluator
{
    GoalCompletionVerdict Evaluate(GoalEntity goal, IReadOnlyList<Evidence>? currentEvidence = null);
}

/// <summary>
/// Deterministic two-stage goal completion evaluator.
/// </summary>
public class GoalCompletionEvaluator : IGoalCompletionEvaluator
{
    public GoalCompletionVerdict Evaluate(GoalEntity goal, IReadOnlyList<Evidence>? currentEvidence = null)
    {
        if (goal == null) throw new ArgumentNullException(nameof(goal));

        // 1. If goal has work items, every item MUST be in Completed state.
        if (goal.WorkItems.Count > 0)
        {
            var openItems = goal.WorkItems.Where(w => w.State != WorkItemState.Completed).ToList();
            if (openItems.Count > 0)
            {
                var summary = string.Join("; ", openItems.Take(5).Select(w => $"[{w.Id}] {w.Objective} ({w.State})"));
                return new GoalCompletionVerdict(
                    false,
                    $"Goal completion rejected: {openItems.Count} work item(s) are not completed: {summary}");
            }
        }

        // 2. Any failed work item blocks completion
        if (goal.WorkItems.Any(w => w.State == WorkItemState.Failed))
        {
            return new GoalCompletionVerdict(
                false,
                "Goal completion rejected: one or more work items failed without recovery.");
        }

        // 3. Evidence-backed completion verification: if evidence list provided, check for unresolved failures
        if (currentEvidence != null)
        {
            var failures = currentEvidence.Where(e => e.Kind is EvidenceKind.BuildFailed or EvidenceKind.TestFailed or EvidenceKind.CommandFailed or EvidenceKind.PreviewFailed or EvidenceKind.AssertionFailed).ToList();
            if (failures.Count > 0)
            {
                return new GoalCompletionVerdict(
                    false,
                    $"Goal completion rejected: {failures.Count} unresolved execution/verification failures detected in evidence ledger.");
            }
        }

        return new GoalCompletionVerdict(true, null);
    }
}
