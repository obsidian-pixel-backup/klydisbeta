using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The concrete action the loop must take to execute a supervisor decision (P1.15). Every
/// <see cref="ExecutionDecision"/> maps to exactly one directive; ChatEngine renders the
/// directive (inject messages, yield events, continue/break) but never re-decides what
/// happens next. The runtime executes the durable half of the decision (task state
/// transitions, completion seal) in <see cref="AgentRuntime.DispatchAsync"/>; the directive
/// returned there is the loop's instruction. The legacy loop branches survive only for
/// sessions without the task layer (no supervisor), never as a competing authority.
/// </summary>
public enum DispatchDirectiveKind
{
    /// <summary>Continue the loop for the current step. When <see cref="DispatchDirective.IncludeContinuationInstruction"/>
    /// is set, the loop injects the truncation continuation instruction before regenerating.</summary>
    ContinueLoop,

    /// <summary>Inject the step-aware protocol-repair instruction and regenerate (bounded by
    /// the repair budget). Executes the supervisor's RepairProtocol decision.</summary>
    InjectRepair,

    /// <summary>Inject the plan-revision directive and regenerate. Executes Replan.</summary>
    InjectReplan,

    /// <summary>Inject the verification-required instruction and regenerate. Executes Verify
    /// when the completion gate is NOT yet satisfied.</summary>
    InjectVerificationInstruction,

    /// <summary>End the turn with a structured notice. Executes Pause / AwaitUser.</summary>
    EndTurnNotice,

    /// <summary>Seal the task as Completed (harness-verified) and end the turn with the
    /// completion event. Executes CompleteTask (and Verify when the gate holds).</summary>
    SealCompletion,

    /// <summary>Mark the task Failed and end the turn with a diagnostic. Executes FailTask.</summary>
    MarkFailed
}

/// <summary>
/// What the dispatcher ordered the loop to do: the decision, the reason, the concrete action,
/// the exact message to inject/emit, and whether a truncation continuation instruction is
/// warranted. The loop executes this — it does not invent its own decision tree.
/// </summary>
public sealed record DispatchDirective(
    ExecutionDecision Decision,
    ContinuationReason Reason,
    DispatchDirectiveKind Kind,
    string? Message,
    bool IncludeContinuationInstruction = false,
    string? NextStepId = null);

/// <summary>
/// The pure decision→directive mapping (P1.15 Phase A). Deterministic and unit-testable:
/// given a supervisor decision and the snapshot it was decided on, this produces the exact
/// directive. The runtime's <see cref="AgentRuntime.DispatchAsync"/> performs the durable
/// transitions and returns one of these; the loop renders it.
/// </summary>
public static class ExecutionDispatcher
{
    /// <summary>
    /// Maps a supervisor decision to the directive the loop must execute. The snapshot
    /// supplies the step context for step-aware messages; <paramref name="eligibility"/> is
    /// the completion gate result used by the Verify decision (produced by the runtime from
    /// the run's evidence ledger). Pure — no I/O, no state mutation.
    /// </summary>
    public static DispatchDirective BuildDirective(
        SupervisorDecision decision,
        TaskExecutionSnapshot snapshot,
        CompletionEligibility? eligibility = null)
    {
        return decision.Decision switch
        {
            ExecutionDecision.CompleteTask => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.SealCompletion,
                "Task completed — all plan items are complete and the run's verification evidence backs the checklist.",
                NextStepId: decision.NextStepId),

            ExecutionDecision.RepairProtocol => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.InjectRepair,
                BuildRepairInstruction(decision.Reason, snapshot),
                NextStepId: decision.NextStepId),

            ExecutionDecision.Replan => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.InjectReplan,
                "[System Prompt: The supervisor has determined the current approach is not producing progress. REVISE the plan now with 'plan' (action=create): replace the steps that are not working with a fresh, concrete approach, then execute the revised plan. Do NOT repeat the previous failing actions.]",
                NextStepId: decision.NextStepId),

            ExecutionDecision.Pause => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.EndTurnNotice,
                decision.Reason == ContinuationReason.VerificationFailed
                    ? "⏸ The supervisor paused this turn: completion was claimed too many times without satisfying verification. The task stays open — resume to continue the real verification work."
                    : "⏸ The supervisor paused this turn: the execution budget was exhausted. The task stays open — resume to continue.",
                NextStepId: decision.NextStepId),

            ExecutionDecision.FailTask => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.MarkFailed,
                "✖ The supervisor failed the task: a runtime generation error stopped execution. The task is left open and recoverable.",
                NextStepId: decision.NextStepId),

            ExecutionDecision.AwaitUser => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.EndTurnNotice,
                "⚠ The generation was interrupted (cancelled, model switch/unload, or user stop) while the task stays open. Your message is still here — send it again to continue the work.",
                NextStepId: decision.NextStepId),

            ExecutionDecision.Verify => BuildVerifyDirective(decision, snapshot, eligibility),

            // ContinueStep (and the legacy ContinueGeneration/ExecuteTool values, which the
            // snapshot-based supervisor never produces) → the loop continues. A truncated or
            // prematurely-ended generation carries the continuation-instruction flag so the
            // loop's truncation machinery is the RENDER of the directive, not a decision.
            _ => new(
                decision.Decision, decision.Reason, DispatchDirectiveKind.ContinueLoop,
                Message: null,
                IncludeContinuationInstruction:
                    decision.Reason is ContinuationReason.GenerationTruncated or ContinuationReason.ModelEndedEarly,
                NextStepId: decision.NextStepId)
        };
    }

    /// <summary>
    /// Verify dispatch: when the completion gate holds, the decision is upgraded to
    /// CompleteTask (the runtime seals it); otherwise the model is instructed to produce the
    /// missing verification evidence and regenerate — never a silent pass-through.
    /// </summary>
    private static DispatchDirective BuildVerifyDirective(
        SupervisorDecision decision,
        TaskExecutionSnapshot snapshot,
        CompletionEligibility? eligibility)
    {
        if (eligibility is { AllRequiredStepsComplete: true, AllVerificationPredicatesSatisfied: true, NoUnresolvedFailures: true })
        {
            return new DispatchDirective(
                ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted,
                DispatchDirectiveKind.SealCompletion,
                "Task completed — all plan items are complete and the run's verification evidence backs the checklist.",
                NextStepId: decision.NextStepId);
        }

        string missing = eligibility is { UnsatisfiedVerification.Count: > 0 }
            ? eligibility.UnsatisfiedVerification.Count <= 3
                ? string.Join("; ", eligibility.UnsatisfiedVerification)
                : string.Join("; ", eligibility.UnsatisfiedVerification.Take(3)) +
                  $"; (+{eligibility.UnsatisfiedVerification.Count - 3} more)"
            : "verification evidence (build/tests/preview) against the CURRENT files";

        return new DispatchDirective(
            decision.Decision, decision.Reason, DispatchDirectiveKind.InjectVerificationInstruction,
            "[System Instruction: The runtime verification gate is not yet satisfied. All plan items are complete, but the following verification evidence is missing or stale against the current files: " +
            missing +
            ". Run the verification (build/tests/preview) against the CURRENT files, and once the evidence is recorded call 'task_complete' to seal the task.]",
            NextStepId: decision.NextStepId);
    }

    /// <summary>
    /// The step-aware protocol-repair instruction. Demands the step's actual contract: a text
    /// deliverable (Reason/Summary/UserInput) as VISIBLE reply text, or exactly ONE executable
    /// action on every other step kind. Built from the snapshot's TaskStep — the single owner
    /// of step semantics — never from English matching in the loop.
    /// </summary>
    private static string BuildRepairInstruction(ContinuationReason reason, TaskExecutionSnapshot snapshot)
    {
        var step = snapshot.CurrentStep;
        string currentStep = !string.IsNullOrWhiteSpace(step?.Title)
            ? "'" + step.Title + "'"
            : "the current step";

        string issue = reason switch
        {
            ContinuationReason.FailedActionNoProgress =>
                "your previous action changed no task state — it did not execute, the plan did not move, and no file changed",
            _ =>
                "your previous response produced no executable action and changed no task state — text alone is not progress"
        };

        bool textDeliverable = step != null &&
            step.ExpectedActionKind is StepActionKind.Reason or StepActionKind.Summary or StepActionKind.UserInput;

        string demand = textDeliverable
            ? "Produce the required design direction / reasoning / summary as VISIBLE reply text — reasoning inside a think block does not count and is not delivered to the user."
            : "Perform exactly ONE tool action from the current step's allowed set — inspect, modify, or verify. Text alone is NOT accepted as task progress.";

        return "[System Instruction: The supervisor detected a protocol failure: " + issue +
               ". In autonomous task mode, text is not progress; only executed actions and changed task state count.\n" +
               "CURRENT STEP: " + currentStep + "\n" +
               demand + "\n" +
               "Do NOT greet the user. Do NOT ask what to do next. Do NOT describe what you would do — do it.]";
    }
}
