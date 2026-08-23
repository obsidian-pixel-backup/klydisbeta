using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The completion gate's second dimension (P0): an empty plan checklist is necessary but not
/// sufficient — the run's evidence must back it. Produced by the runtime from the plan and
/// the run evidence ledger; consumed by <see cref="AgentSupervisor.EvaluateCompletion"/>.
/// </summary>
public sealed record CompletionEligibility(
    bool AllRequiredStepsComplete,
    bool AllVerificationPredicatesSatisfied,
    bool NoUnresolvedFailures,
    IReadOnlyList<string> UnsatisfiedVerification,
    IReadOnlyList<string>? UnsatisfiedCompletionCriteria = null)
{
    /// <summary>True when all completion eligibility conditions hold.</summary>
    public bool IsEligible => AllRequiredStepsComplete &&
                             AllVerificationPredicatesSatisfied &&
                             NoUnresolvedFailures &&
                             (UnsatisfiedCompletionCriteria == null || UnsatisfiedCompletionCriteria.Count == 0);
}

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
    /// The completion gate: "done" requires the plan checklist to be empty AND the run's
    /// completion eligibility to hold (all verification predicates satisfied, no unresolved
    /// verification failures, all model-generated completion criteria verified). A claim while
    /// items remain open — or while the evidence does not back the checklist — is rejected.
    /// </summary>
    public static GoalCompletionVerdict EvaluateCompletion(
        IReadOnlyList<string>? openPlanItems,
        CompletionEligibility? eligibility = null)
    {
        // FAIL CLOSED (P0.6): a completion claim is accepted only when the authoritative
        // plan state was actually READ and shows zero open items. If the plan could not be
        // read at all, verification is UNAVAILABLE — the claim is rejected, never accepted.
        if (openPlanItems == null)
        {
            return new GoalCompletionVerdict(false,
                "Completion verification unavailable: the authoritative plan state could not be " +
                "read, so this completion claim cannot be verified and is REJECTED. The goal is " +
                "not verified complete.");
        }

        if (openPlanItems.Count > 0)
        {
            var listed = openPlanItems.Count <= 3
                ? string.Join("; ", openPlanItems)
                : string.Join("; ", openPlanItems.Take(3)) + $"; (+{openPlanItems.Count - 3} more)";

            return new GoalCompletionVerdict(false,
                $"{openPlanItems.Count} plan item(s) still open: {listed}");
        }

        // P0 (review §4/§36): an EMPTY checklist is necessary but not sufficient. The
        // evidence must back it — a task where every box is checked but the build never ran,
        // or whose verification evidence was invalidated by later edits, is NOT complete.
        if (eligibility != null)
        {
            if (!eligibility.AllRequiredStepsComplete)
            {
                return new GoalCompletionVerdict(false,
                    "Completion rejected: not all required steps are complete.");
            }
            if (eligibility.UnsatisfiedCompletionCriteria != null && eligibility.UnsatisfiedCompletionCriteria.Count > 0)
            {
                var missingCriteria = string.Join("; ", eligibility.UnsatisfiedCompletionCriteria);
                return new GoalCompletionVerdict(false,
                    $"Completion rejected: {eligibility.UnsatisfiedCompletionCriteria.Count} completion criterion/criteria unsatisfied: {missingCriteria}");
            }
            if (!eligibility.AllVerificationPredicatesSatisfied)
            {
                var missing = eligibility.UnsatisfiedVerification.Count <= 3
                    ? string.Join("; ", eligibility.UnsatisfiedVerification)
                    : string.Join("; ", eligibility.UnsatisfiedVerification.Take(3)) +
                      $"; (+{eligibility.UnsatisfiedVerification.Count - 3} more)";
                return new GoalCompletionVerdict(false,
                    $"Completion rejected: {eligibility.UnsatisfiedVerification.Count} " +
                    $"verification predicate(s) unsatisfied. Required but missing/stale: {missing}. " +
                    "Re-run the verification (build/tests/preview) against the CURRENT files " +
                    "and only then claim completion.");
            }
            if (!eligibility.NoUnresolvedFailures)
            {
                return new GoalCompletionVerdict(false,
                    "Completion rejected: the run has unresolved verification FAILURES " +
                    "(failed build/test/preview/command) against the current files. Fix them " +
                    "and re-verify before claiming completion.");
            }
        }

        return new GoalCompletionVerdict(true, null);
    }

    /// <summary>
    /// Computes the completion eligibility from the plan and the run's CURRENT (non-stale)
    /// evidence (P0): every step complete, every verification step's predicate satisfied,
    /// no unresolved verification failures.
    /// </summary>
    public static CompletionEligibility EvaluateEligibility(
        IReadOnlyList<ToolExecutor.PlanEntry> plan,
        string? taskId,
        IReadOnlyList<EvidenceLedgerEntry> currentLedgerEvidence,
        CompletionCriteria? completionCriteria = null)
    {
        var steps = TaskStepBuilder.Build(plan, taskId);
        bool allComplete = steps.All(s => !s.IsOpen);

        var evidence = currentLedgerEvidence.Select(e => e.Evidence).ToList();
        var unsatisfied = new List<string>();

        foreach (var step in steps.Where(s => s.ExpectedActionKind == StepActionKind.Verification))
        {
            var criteria = StepClassifier.ClassifyCriteria(step.Title);
            bool satisfied = criteria.Count > 0
                ? evidence.Any(ev => criteria.Any(c => c.Satisfies(ev)))
                : evidence.Any(ev => ev.IsVerificationCapable);
            if (!satisfied) unsatisfied.Add(step.Title);
        }

        var unsatisfiedCriteria = new List<string>();
        if (completionCriteria != null && completionCriteria.Conditions.Count > 0)
        {
            foreach (var cond in completionCriteria.Conditions)
            {
                if (string.IsNullOrWhiteSpace(cond)) continue;
                bool satisfied = evidence.Any(ev =>
                    ev.Description.Contains(cond, StringComparison.OrdinalIgnoreCase) ||
                    (ev.Subject != null && cond.Contains(ev.Subject, StringComparison.OrdinalIgnoreCase)));
                if (!satisfied && allComplete && steps.Count > 0)
                {
                    // If all steps are complete and we have verified evidence, allow completion unless explicitly contradicted
                    if (!evidence.Any(ev => ev.IsVerificationCapable))
                    {
                        unsatisfiedCriteria.Add(cond);
                    }
                }
            }
        }

        // P0-Fix: a failed COMMAND is a completion blocker only when the run actually has
        // verification obligations (build/test/preview steps). On a pure diagnostics run a
        // probe that returns an error (e.g. no temperature sensors, permissions denied) is a
        // FINDING, not an unresolved failure — the old rule rejected task_complete forever
        // on read-only goals (the qwen run died on exactly this: 10× COMPLETION_NOT_ELIGIBLE
        // then the app crashed mid-loop). Hard build/test/preview/assertion failures always
        // block regardless.
        var verificationStepsExist = steps.Any(s => s.ExpectedActionKind == StepActionKind.Verification);
        bool noFailures = !currentLedgerEvidence.Any(e =>
            e.IsUnresolvedFailure &&
            (e.Evidence.Kind is EvidenceKind.BuildFailed or EvidenceKind.TestFailed or
                 EvidenceKind.PreviewFailed or EvidenceKind.AssertionFailed ||
             (e.Evidence.Kind == EvidenceKind.CommandFailed && verificationStepsExist)));
        return new CompletionEligibility(allComplete, unsatisfied.Count == 0, noFailures, unsatisfied, unsatisfiedCriteria);
    }

    /// <summary>
    /// The next step to work: the first open TaskStep (in plan order). Returns its StepId.
    /// </summary>
    public static string? SelectNextStep(IReadOnlyList<TaskStep> steps)
        => steps.FirstOrDefault(s => s.IsOpen)?.StepId;

    /// <summary>
    /// P1.8: the snapshot-based decision. The <see cref="TaskExecutionSnapshot"/> is the ONLY
    /// input — the supervisor derives the plan, current step, queue, outcome and state delta
    /// from it and decides directly. This is the ONLY decision path; the legacy plan-
    /// checklist overload was deleted — every caller goes through the snapshot.
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
        //    call, no completion claim, no replan) while work remains open. Steps whose
        //    deliverable IS text (Reason/Summary/UserInput) are exempt — their contract demands
        //    text, so a text-only generation is the deliverable, not a protocol failure. The
        //    live loop's no-action guard applies the same exemption.
        if (snapshot.Outcome == GenerationOutcome.NoActionProduced && openWork &&
            (currentStep == null || RequiresExecution(currentStep.ExpectedActionKind)))
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

        // 8. A verification step is satisfied only by evidence MATCHING ITS PREDICATE
        //    (P1.10/P1.14): the criteria the step actually requires (BuildPassed for "run the
        //    build", PreviewLoaded for "run a local preview"). Evidence of a kind the step
        //    does not require — incl. CommandSucceeded ("echo hello ran") and weak inspection
        //    (FileExists/FileChanged) — does NOT verify. Criteria may also carry a subject
        //    pattern, so kind matches against the wrong subject fail too. "Build a tool ran"
        //    is not "the thing was verified". When the step derives no predicate, any
        //    verification-capable evidence qualifies (legacy fallback).
        if (openWork && currentStep?.ExpectedActionKind == StepActionKind.Verification)
        {
            var criteria = StepClassifier.ClassifyCriteria(currentStep.Title);
            bool satisfied = criteria.Count > 0
                ? delta.EvidenceEntries.Any(ev => criteria.Any(c => c.Satisfies(ev)))
                : delta.HasVerificationEvidence();
            if (!satisfied)
            {
                return new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.VerificationFailed, nextStepId);
            }
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
    /// Shared by the supervisor decision AND the live loop's no-action guard, so both agree
    /// on which steps treat a text-only response as a deliverable rather than a protocol
    /// failure.
    /// </summary>
    public static bool RequiresExecution(StepActionKind kind)
        => kind is StepActionKind.Inspect or StepActionKind.Research or
            StepActionKind.FileMutation or StepActionKind.CommandExecution or
            StepActionKind.TerminalInteraction or StepActionKind.Verification;
}
