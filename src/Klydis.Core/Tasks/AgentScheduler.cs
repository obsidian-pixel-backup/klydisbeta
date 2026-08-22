using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// States for the agent scheduler state machine.
/// </summary>
public enum SchedulerState
{
    NoWork,
    WorkReady,
    ModelDecisionRequired,
    ToolExecutionRequired,
    WaitingTool,
    WaitingUser,
    ReflectionRequired,
    VerificationRequired,
    GoalCompletionCheck,
    BudgetWarning,
    BudgetExhausted,
    Blocked,
    Complete
}

/// <summary>
/// Authoritative token emitted when a tool call finishes execution, forcing a subsequent model decision cycle.
/// The model cannot terminate or drop the goal without evaluating the observation.
/// </summary>
public sealed record ContinuationToken(
    string GoalId,
    string TurnId,
    string? ToolCallId,
    string? ResultId,
    bool RequiredNextDecision = true,
    DateTimeOffset Timestamp = default)
{
    public DateTimeOffset Timestamp { get; init; } = Timestamp == default ? DateTimeOffset.UtcNow : Timestamp;
}

/// <summary>
/// Decision output by the scheduler indicating what work or action is required next.
/// </summary>
public sealed record SchedulerDecision(
    SchedulerState State,
    WorkItem? NextWorkItem,
    ContinuationToken? Token,
    string? Reason,
    bool ShouldContinue);

/// <summary>
/// Interface for the agent spine scheduler.
/// </summary>
public interface IAgentScheduler
{
    SchedulerDecision SelectNext(
        GoalEntity goal,
        BudgetSnapshot budget,
        IReadOnlyList<Evidence>? evidence = null,
        ContinuationToken? activeToken = null,
        int consecutiveStalledTurns = 0);
}

/// <summary>
/// Central agent scheduler implementing deterministic work selection, dependency resolution,
/// continuation enforcement, budget limits, and reflection triggers.
/// </summary>
public class AgentScheduler : IAgentScheduler
{
    public SchedulerDecision SelectNext(
        GoalEntity goal,
        BudgetSnapshot budget,
        IReadOnlyList<Evidence>? evidence = null,
        ContinuationToken? activeToken = null,
        int consecutiveStalledTurns = 0)
    {
        if (goal == null) throw new ArgumentNullException(nameof(goal));
        if (budget == null) throw new ArgumentNullException(nameof(budget));

        // 1. Check budget exhaustion (hard stop)
        if (budget.IsExhausted)
        {
            return new SchedulerDecision(
                SchedulerState.BudgetExhausted,
                null,
                null,
                budget.GuidanceMessage ?? "Budget limits exhausted.",
                ShouldContinue: false);
        }

        // 2. If already in terminal states
        if (goal.State is GoalLifecycleState.Completed or GoalLifecycleState.Failed or GoalLifecycleState.Cancelled)
        {
            return new SchedulerDecision(
                SchedulerState.Complete,
                null,
                null,
                $"Goal is in terminal state: {goal.State}.",
                ShouldContinue: false);
        }

        if (goal.State == GoalLifecycleState.WaitingUser)
        {
            return new SchedulerDecision(
                SchedulerState.WaitingUser,
                null,
                null,
                "Goal is waiting for user input or approval.",
                ShouldContinue: false);
        }

        // 3. Stagnation / Stuck-loop detection -> Reflection
        if (consecutiveStalledTurns >= 2)
        {
            return new SchedulerDecision(
                SchedulerState.ReflectionRequired,
                null,
                null,
                $"Consecutive turns with zero progress ({consecutiveStalledTurns}). Strategy reflection required.",
                ShouldContinue: true);
        }

        // 4. If continuation token is active from a tool result, force model decision cycle
        if (activeToken != null && activeToken.RequiredNextDecision)
        {
            return new SchedulerDecision(
                SchedulerState.ModelDecisionRequired,
                null,
                activeToken,
                "Tool execution completed; observation received. Next decision cycle required.",
                ShouldContinue: true);
        }

        // 5. Work item scheduling
        if (goal.WorkItems.Count > 0)
        {
            var itemMap = goal.WorkItems.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
            var runnable = goal.WorkItems.FirstOrDefault(w => w.IsRunnable(itemMap));

            if (runnable != null)
            {
                return new SchedulerDecision(
                    SchedulerState.WorkReady,
                    runnable,
                    null,
                    $"Work item '{runnable.Id}' ({runnable.Objective}) is ready for execution.",
                    ShouldContinue: true);
            }

            bool anyPending = goal.WorkItems.Any(w => w.State == WorkItemState.Pending || w.State == WorkItemState.Running);
            if (anyPending)
            {
                // All pending items have unmet dependencies or are blocked
                return new SchedulerDecision(
                    SchedulerState.Blocked,
                    null,
                    null,
                    "Pending work items are blocked by unsatisfied dependencies.",
                    ShouldContinue: false);
            }

            // All items completed -> check verification / completion
            return new SchedulerDecision(
                SchedulerState.GoalCompletionCheck,
                null,
                null,
                "All work items completed; goal completion verification ready.",
                ShouldContinue: true);
        }

        // If no structured work items are defined yet, model decision / planning is required
        return new SchedulerDecision(
            SchedulerState.ModelDecisionRequired,
            null,
            null,
            "Autonomous goal running; model decision required.",
            ShouldContinue: true);
    }
}
