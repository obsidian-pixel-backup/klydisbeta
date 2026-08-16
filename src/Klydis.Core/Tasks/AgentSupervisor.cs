using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The harness's decision layer — pure and deterministic (no I/O). The model produces a
/// generation; the supervisor evaluates it against durable task state and decides what
/// happens next. Completion claims, generation outcomes, and stagnation signals are all
/// INPUTS here; none of them can terminate a task by themselves. This is the single place
/// that answers "continue?", "complete?", and "what step next?" — the live loop consults it
/// instead of letting the model (or scattered loop branches) own those answers.
/// </summary>
public static class AgentSupervisor
{
    /// <summary>
    /// The completion gate: "done" requires the plan checklist to be empty. A claim of
    /// completion while items remain open is rejected. (Owned here; GoalOrchestrator's copy
    /// delegates to this so the live loop and the legacy orchestrator agree.)
    /// </summary>
    public static GoalCompletionVerdict EvaluateCompletion(IReadOnlyList<string> openPlanItems)
    {
        if (openPlanItems.Count == 0)
        {
            return new GoalCompletionVerdict(true, null);
        }

        var listed = openPlanItems.Count <= 3
            ? string.Join("; ", openPlanItems)
            : string.Join("; ", openPlanItems.Take(3)) + $"; (+{openPlanItems.Count - 3} more)";

        return new GoalCompletionVerdict(false,
            $"{openPlanItems.Count} plan item(s) still open: {listed}");
    }

    /// <summary>
    /// The next step to work: the first open plan item. Returns its text (a stable step id
    /// arrives with first-class TaskStep records).
    /// </summary>
    public static string? SelectNextStep(IReadOnlyList<ToolExecutor.PlanEntry> plan)
        => plan.FirstOrDefault(e => !e.Done)?.Text;

    /// <summary>
    /// Decides what happens after a generation, given the generation outcome and the durable
    /// task state (plan checklist, queue, rejection/stall counters). The decision is
    /// independent of anything the model said about being done or hitting limits.
    /// </summary>
    public static SupervisorDecision DecideAfterTurn(
        bool claimAccepted,
        GenerationOutcome outcome,
        IReadOnlyList<ToolExecutor.PlanEntry> plan,
        int pendingQueueItems,
        int completionRejections,
        int maxCompletionRejections,
        int consecutiveStalledTurns,
        int maxStalledTurns)
    {
        int openCount = plan.Count(e => !e.Done);
        int total = plan.Count;

        // A harness-accepted completion claim seals the task — the ONLY path to CompleteTask.
        if (claimAccepted)
        {
            return new SupervisorDecision(ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted);
        }

        // Hard interrupts: the user cancelled, or the generation failed.
        if (outcome == GenerationOutcome.Cancelled)
        {
            return new SupervisorDecision(ExecutionDecision.AwaitUser, ContinuationReason.UserCancelled);
        }
        if (outcome == GenerationOutcome.Error)
        {
            return new SupervisorDecision(ExecutionDecision.FailTask, ContinuationReason.Error);
        }

        // A model that keeps claiming completion while work stays open is the "same action
        // without progress" failure — halt before cycling forever.
        if (completionRejections >= maxCompletionRejections)
        {
            return new SupervisorDecision(ExecutionDecision.Pause, ContinuationReason.VerificationFailed,
                SelectNextStep(plan));
        }

        // Repeated tool turns with no plan progress = silent failure; reassess the approach.
        if (consecutiveStalledTurns >= maxStalledTurns && openCount > 0)
        {
            return new SupervisorDecision(ExecutionDecision.Replan, ContinuationReason.StagnationDetected,
                SelectNextStep(plan));
        }

        string? nextStep = SelectNextStep(plan);

        if (openCount > 0)
        {
            var reason = outcome switch
            {
                GenerationOutcome.OutputBudgetExhausted or GenerationOutcome.GenerationCutShort => ContinuationReason.GenerationTruncated,
                GenerationOutcome.ModelEndedEarly => ContinuationReason.ModelEndedEarly,
                GenerationOutcome.ContextExhausted => ContinuationReason.ContextCompacted,
                GenerationOutcome.DegenerateLoop => ContinuationReason.ModelEndedEarly,
                _ => ContinuationReason.StepIncomplete
            };
            return new SupervisorDecision(ExecutionDecision.ContinueStep, reason, nextStep);
        }

        if (pendingQueueItems > 0)
        {
            return new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.StepIncomplete, nextStep);
        }

        if (total > 0)
        {
            // All items are checked off but the model didn't seal completion — direct it to.
            return new SupervisorDecision(ExecutionDecision.Verify, ContinuationReason.StepIncomplete, nextStep);
        }

        return new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.StepIncomplete, nextStep);
    }
}
