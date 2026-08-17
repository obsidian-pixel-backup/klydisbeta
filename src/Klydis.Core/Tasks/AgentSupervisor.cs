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
    public static GoalCompletionVerdict EvaluateCompletion(IReadOnlyList<string>? openPlanItems)
    {
        // FAIL CLOSED (P0.6): a completion claim is accepted only when the authoritative
        // plan state was actually READ and shows zero open items. If the plan could not be
        // read at all, verification is UNAVAILABLE — the claim is rejected, never accepted.
        // The old behavior degraded a read failure to "no open items" (claim accepted),
        // which let a database fault complete a task that still had open work.
        if (openPlanItems == null)
        {
            return new GoalCompletionVerdict(false,
                "Completion verification unavailable: the authoritative plan state could not be " +
                "read, so this completion claim cannot be verified and is REJECTED. The goal is " +
                "not verified complete.");
        }

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
    /// The next step to work: the first open TaskStep (in plan order). Returns its StepId.
    /// </summary>
    public static string? SelectNextStep(IReadOnlyList<TaskStep> steps)
        => steps.FirstOrDefault(s => s.IsOpen)?.StepId;

    /// <summary>
    /// Legacy projection: the first open plan item's text. Kept for callers that still work
    /// on raw plan entries; new code goes through TaskStep records.
    /// </summary>
    public static string? SelectNextStepText(IReadOnlyList<ToolExecutor.PlanEntry> plan)
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

        // Autonomous-mode protocol failure: the model produced text but NO action (no tool
        // call, no completion claim, no replan). This is the dominant failure from the live
        // export — the model understood the request but answered with a greeting/permission
        // ask instead of entering the tool protocol. Repair the protocol with a compact
        // action-required instruction rather than accepting the text as a completed turn.
        if (outcome == GenerationOutcome.NoActionProduced && openCount > 0)
        {
            return new SupervisorDecision(ExecutionDecision.RepairProtocol, ContinuationReason.NoActionProduced,
                SelectNextStepText(plan));
        }

        // A model that keeps claiming completion while work stays open is the "same action
        // without progress" failure — halt before cycling forever.
        if (completionRejections >= maxCompletionRejections)
        {
            return new SupervisorDecision(ExecutionDecision.Pause, ContinuationReason.VerificationFailed,
                SelectNextStepText(plan));
        }

        // Repeated tool turns with no plan progress = silent failure; reassess the approach.
        if (consecutiveStalledTurns >= maxStalledTurns && openCount > 0)
        {
            return new SupervisorDecision(ExecutionDecision.Replan, ContinuationReason.StagnationDetected,
                SelectNextStepText(plan));
        }

        string? nextStep = SelectNextStepText(plan);

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

    /// <summary>
    /// P1.8: the snapshot-based decision. The TaskExecutionSnapshot is the ONLY input — the
    /// supervisor derives the plan, current step, queue, outcome and state delta from it and
    /// delegates to the plan-checklist decision. Keeping one verdict owner means the live
    /// loop and any caller agree on what happens next.
    /// </summary>
    public static SupervisorDecision DecideAfterTurn(
        TaskExecutionSnapshot snapshot,
        int maxCompletionRejections = 3,
        int maxStalledTurns = 6)
    {
        var steps = TaskStepBuilder.Build(snapshot.Plan, snapshot.TaskId);
        string? nextStepId = SelectNextStep(steps);

        // NO-FACTUAL-PROGRESS RULE: the model's text volume is never progress. When the
        // current step's contract requires an EXECUTABLE action (inspection, mutation,
        // verification, research, commands) and this generation changed ZERO task state
        // (no tool executed, no plan move, no file change), a text-only "completed turn" is
        // exactly the 2,500-token essay failure — route it to RepairProtocol like
        // NoActionProduced. Reason/Summary/UserInput steps are exempt (their deliverable IS
        // text). ToolCallProduced outcomes are exempt (a failed tool is still execution the
        // next repair can learn from).
        var currentStep = snapshot.CurrentStep ?? TaskStepBuilder.CurrentStep(steps);
        // Only genuinely COMPLETED text-only generations are "talking": a cancelled turn, a
        // truncation, a degenerate loop, or an already-classified NoActionProduced must keep
        // their own recovery paths (AwaitUser / auto-continue / loop correction).
        bool completedTextOnly = snapshot.Outcome is GenerationOutcome.CompletedTurn
            or GenerationOutcome.ModelEndedEarly;
        if (completedTextOnly &&
            currentStep != null &&
            RequiresExecution(currentStep.ExpectedActionKind) &&
            !snapshot.MadeFactualProgress &&
            snapshot.OpenPlanItems > 0)
        {
            return new SupervisorDecision(
                ExecutionDecision.RepairProtocol,
                ContinuationReason.NoActionProduced,
                currentStep.StepId);
        }

        var decision = DecideAfterTurn(
            claimAccepted: false,
            snapshot.Outcome,
            snapshot.Plan,
            snapshot.PendingQueueItems,
            snapshot.CompletionRejections,
            maxCompletionRejections,
            snapshot.ConsecutiveStalledTurns,
            maxStalledTurns);

        // The snapshot knows the step records; surface the step id rather than raw text.
        return decision with { NextStepId = nextStepId ?? decision.NextStepId };
    }

    /// <summary>
    /// True when the step's contract requires an executable action (a tool call), as opposed
    /// to a step whose deliverable is text (reasoning/requirements, summaries, user input).
    /// </summary>
    private static bool RequiresExecution(StepActionKind kind)
        => kind is StepActionKind.Inspect or StepActionKind.Research or
            StepActionKind.FileMutation or StepActionKind.CommandExecution or
            StepActionKind.TerminalInteraction or StepActionKind.Verification;
}
