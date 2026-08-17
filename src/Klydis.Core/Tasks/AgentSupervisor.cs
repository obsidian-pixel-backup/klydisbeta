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
    /// P1.8: the snapshot-based decision. The <see cref="TaskExecutionSnapshot"/> is the ONLY
    /// input — the supervisor derives the plan, current step, queue, outcome and state delta
    /// from it and decides directly. This is the authoritative decision path; the legacy
    /// plan-checklist overload above is kept only for pre-snapshot callers.
    ///
    /// STATE DELTA IS THE PRIMARY PROGRESS SIGNAL (text ≠ progress):
    ///   - NoAction + no state delta        → protocol repair
    ///   - ToolCall + no state delta        → suspicious / repeated / failed action (repair)
    ///   - ToolCall + meaningful state delta → continue
    ///   - Verification + evidence          → advance (fall through to continue)
    ///   - Verification + no evidence       → reject (keep the step open, VerificationFailed)
    ///   - Text-only turn on an execution step with no state change → protocol repair
    ///
    /// Completion claims (CompletionClaimAccepted) and the rejection/stall counters are all
    /// INPUTS here; none of them can terminate a task by themselves.
    /// </summary>
    public static SupervisorDecision DecideAfterTurn(
        TaskExecutionSnapshot snapshot,
        int maxCompletionRejections = 3,
        int maxStalledTurns = 6)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        var steps = TaskStepBuilder.Build(snapshot.Plan, snapshot.TaskId);
        string? nextStepId = SelectNextStep(steps);
        var currentStep = snapshot.CurrentStep ?? TaskStepBuilder.CurrentStep(steps);
        bool openWork = snapshot.OpenPlanItems > 0;
        var delta = snapshot.StateDelta ?? StateDelta.Empty;
        bool madeProgress = !delta.IsEmpty;

        // 1. A harness-accepted completion claim seals the task — the ONLY path to CompleteTask.
        if (snapshot.CompletionClaimAccepted)
        {
            return new SupervisorDecision(ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted);
        }

        // 2. Hard interrupts: the user cancelled, or the generation failed.
        if (snapshot.Outcome == GenerationOutcome.Cancelled)
        {
            return new SupervisorDecision(ExecutionDecision.AwaitUser, ContinuationReason.UserCancelled);
        }
        if (snapshot.Outcome == GenerationOutcome.Error)
        {
            return new SupervisorDecision(ExecutionDecision.FailTask, ContinuationReason.Error);
        }

        // 3. A model that keeps claiming completion while work stays open is the "same action
        //    without progress" failure — halt before cycling forever.
        if (snapshot.CompletionRejections >= maxCompletionRejections)
        {
            return new SupervisorDecision(ExecutionDecision.Pause, ContinuationReason.VerificationFailed, nextStepId);
        }

        // 4. Repeated tool turns with no plan progress = silent failure; reassess the approach.
        if (snapshot.ConsecutiveStalledTurns >= maxStalledTurns && openWork)
        {
            return new SupervisorDecision(ExecutionDecision.Replan, ContinuationReason.StagnationDetected, nextStepId);
        }

        // 5. Autonomous-mode protocol failure: the model produced text but NO action (no tool
        //    call, no completion claim, no replan) while work remains open.
        if (snapshot.Outcome == GenerationOutcome.NoActionProduced && openWork)
        {
            return new SupervisorDecision(ExecutionDecision.RepairProtocol, ContinuationReason.NoActionProduced, nextStepId);
        }

        // 6. A parsed tool call that changed ZERO task state is a suspicious/repeated/failed
        //    action — the model asked for a tool but nothing executed (gate rejection), the
        //    plan did not move, and no file changed. Repair the protocol rather than treating
        //    the un-executed call as progress.
        if (snapshot.Outcome == GenerationOutcome.ToolCallProduced && !madeProgress && openWork)
        {
            return new SupervisorDecision(ExecutionDecision.RepairProtocol, ContinuationReason.FailedActionNoProgress, nextStepId);
        }

        // 7. NO-FACTUAL-PROGRESS RULE: the model's text volume is never progress. When the
        //    current step's contract requires an EXECUTABLE action (inspection, mutation,
        //    verification, research, commands) and this generation changed ZERO task state, a
        //    text-only "completed turn" is exactly the 2,500-token essay failure — route it to
        //    RepairProtocol. Reason/Summary/UserInput steps are exempt (their deliverable IS
        //    text). Only genuinely COMPLETED text-only generations are "talking": a cancelled
        //    turn, a truncation, a degenerate loop, or an already-classified NoActionProduced
        //    keep their own recovery paths (AwaitUser / auto-continue / loop correction).
        if (openWork && !madeProgress &&
            snapshot.Outcome is GenerationOutcome.CompletedTurn or GenerationOutcome.ModelEndedEarly &&
            currentStep != null && RequiresExecution(currentStep.ExpectedActionKind))
        {
            return new SupervisorDecision(ExecutionDecision.RepairProtocol, ContinuationReason.NoActionProduced, currentStep.StepId);
        }

        // 8. A verification step whose turn produced NO evidence is not verified: "done" is
        //    rejected — the step stays open and the model must produce evidence (build, tests,
        //    inspection) rather than narrating success.
        if (openWork && currentStep?.ExpectedActionKind == StepActionKind.Verification &&
            !delta.Contains(StateDeltaKind.EvidenceAdded))
        {
            return new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.VerificationFailed, nextStepId);
        }

        // 9. Open work remains → continue the current step.
        if (openWork)
        {
            var reason = snapshot.Outcome switch
            {
                GenerationOutcome.OutputBudgetExhausted or GenerationOutcome.GenerationCutShort => ContinuationReason.GenerationTruncated,
                GenerationOutcome.ModelEndedEarly => ContinuationReason.ModelEndedEarly,
                GenerationOutcome.ContextExhausted => ContinuationReason.ContextCompacted,
                GenerationOutcome.DegenerateLoop => ContinuationReason.ModelEndedEarly,
                _ => ContinuationReason.StepIncomplete
            };
            return new SupervisorDecision(ExecutionDecision.ContinueStep, reason, nextStepId);
        }

        // 10. Queued work pending → continue.
        if (snapshot.PendingQueueItems > 0)
        {
            return new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.StepIncomplete, nextStepId);
        }

        // 11. All items are checked off but the model didn't seal completion — direct it to.
        if (snapshot.Plan.Count > 0)
        {
            return new SupervisorDecision(ExecutionDecision.Verify, ContinuationReason.StepIncomplete, nextStepId);
        }

        return new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.StepIncomplete, nextStepId);
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
