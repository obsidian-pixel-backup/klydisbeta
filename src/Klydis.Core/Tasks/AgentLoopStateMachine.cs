using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Execution context for a single step/iteration of the 6-phase agent loop.
/// </summary>
public sealed record AgentLoopContext(
    string SessionId,
    string TaskId,
    string RunId,
    string? StepId,
    int IterationNumber,
    AgentLoopPhase CurrentPhase,
    TaskStep? CurrentStep,
    IReadOnlyList<ToolExecutor.PlanEntry> CurrentPlan,
    ScratchpadState Scratchpad,
    StateDelta CurrentStateDelta,
    IReadOnlyList<EvidenceLedgerEntry> CurrentEvidence,
    IReadOnlySet<string> ExecutedReplayKeys,
    TurnContext TurnContext,
    int ConsecutiveRejections = 0,
    int ConsecutiveStalls = 0);

/// <summary>
/// The result of an AgentLoopStateMachine step transition.
/// </summary>
public sealed record AgentLoopStepResult(
    AgentLoopPhase NextPhase,
    SupervisorDecision? Decision,
    DispatchDirective? Directive,
    StateDelta StateDelta,
    ScratchpadState UpdatedScratchpad,
    IReadOnlyList<ToolCallRequest>? ExecutedTools,
    IReadOnlyList<ToolResult>? ToolResults,
    IReadOnlyList<Evidence>? ProducedEvidence,
    string? ModelResponse,
    bool IsTerminal,
    string? StatusMessage = null);

/// <summary>
/// Deterministic 6-phase OODA-VR (Observe-Orient-Decide-Act-Verify-Reflect) Agent Loop State Machine.
/// </summary>
public static class AgentLoopStateMachine
{
    /// <summary>
    /// Checks whether a phase transition is legal in the OODA-VR lifecycle.
    /// </summary>
    public static bool CanTransition(AgentLoopPhase from, AgentLoopPhase to)
    {
        if (from == to) return true;
        return (from, to) switch
        {
            (AgentLoopPhase.Observe, AgentLoopPhase.Orient or AgentLoopPhase.Failed or AgentLoopPhase.Paused) => true,
            (AgentLoopPhase.Orient, AgentLoopPhase.Decide or AgentLoopPhase.Reflect or AgentLoopPhase.Failed or AgentLoopPhase.Paused) => true,
            (AgentLoopPhase.Decide, AgentLoopPhase.Act or AgentLoopPhase.Verify or AgentLoopPhase.Reflect or AgentLoopPhase.Failed or AgentLoopPhase.Paused) => true,
            (AgentLoopPhase.Act, AgentLoopPhase.Verify or AgentLoopPhase.Reflect or AgentLoopPhase.Orient or AgentLoopPhase.Failed or AgentLoopPhase.Paused) => true,
            (AgentLoopPhase.Verify, AgentLoopPhase.Reflect or AgentLoopPhase.Completed or AgentLoopPhase.Orient or AgentLoopPhase.Failed or AgentLoopPhase.Paused) => true,
            (AgentLoopPhase.Reflect, AgentLoopPhase.Observe or AgentLoopPhase.Orient or AgentLoopPhase.Decide or AgentLoopPhase.Completed or AgentLoopPhase.Failed or AgentLoopPhase.Paused) => true,
            (AgentLoopPhase.Paused, AgentLoopPhase.Observe or AgentLoopPhase.Orient) => true,
            _ => false
        };
    }

    /// <summary>
    /// Evaluates the current loop context and transitions deterministically to the next phase.
    /// </summary>
    public static Task<AgentLoopStepResult> StepAsync(AgentLoopContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Terminal state guards
        if (context.CurrentPhase == AgentLoopPhase.Completed)
        {
            return Task.FromResult(new AgentLoopStepResult(
                AgentLoopPhase.Completed,
                new SupervisorDecision(ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted),
                null,
                context.CurrentStateDelta,
                context.Scratchpad,
                null, null, null, null,
                IsTerminal: true,
                StatusMessage: "Task already completed."));
        }

        if (context.CurrentPhase == AgentLoopPhase.Failed)
        {
            return Task.FromResult(new AgentLoopStepResult(
                AgentLoopPhase.Failed,
                new SupervisorDecision(ExecutionDecision.FailTask, ContinuationReason.UnresolvedFailure),
                null,
                context.CurrentStateDelta,
                context.Scratchpad,
                null, null, null, null,
                IsTerminal: true,
                StatusMessage: "Task execution failed."));
        }

        if (context.CurrentPhase == AgentLoopPhase.Paused)
        {
            return Task.FromResult(new AgentLoopStepResult(
                AgentLoopPhase.Paused,
                new SupervisorDecision(ExecutionDecision.Pause, ContinuationReason.UserMessageAvailable),
                null,
                context.CurrentStateDelta,
                context.Scratchpad,
                null, null, null, null,
                IsTerminal: false,
                StatusMessage: "Task paused for user interaction."));
        }

        AgentLoopPhase nextPhase;
        SupervisorDecision? decision = null;
        DispatchDirective? directive = null;
        string? statusMessage = null;

        switch (context.CurrentPhase)
        {
            case AgentLoopPhase.Observe:
                // Phase 1 -> Phase 2: From environment/context observation into cognitive orientation
                nextPhase = AgentLoopPhase.Orient;
                decision = new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.None);
                directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                statusMessage = "Observed environment; orienting situational context.";
                break;

            case AgentLoopPhase.Orient:
                // Phase 2 -> Phase 3: Synthesized scratchpad and hypotheses; proceeding to action decision
                nextPhase = AgentLoopPhase.Decide;
                decision = new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.None);
                directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                statusMessage = "Oriented reasoning; selecting actions.";
                break;

            case AgentLoopPhase.Decide:
                // Phase 3 -> Phase 4: Action selected
                nextPhase = AgentLoopPhase.Act;
                decision = new SupervisorDecision(ExecutionDecision.ExecuteTool, ContinuationReason.None);
                directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                statusMessage = "Action decided; executing tools.";
                break;

            case AgentLoopPhase.Act:
                // Phase 4 -> Phase 5: Tools executed; advancing to closed-loop verification
                nextPhase = AgentLoopPhase.Verify;
                decision = new SupervisorDecision(ExecutionDecision.Verify, ContinuationReason.None);
                directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                statusMessage = "Actions executed; verifying outcomes.";
                break;

            case AgentLoopPhase.Verify:
                // Closed-loop verification check
                bool hasFailedEvidence = context.CurrentEvidence.Any(e =>
                    e.Evidence.Kind is EvidenceKind.BuildFailed or EvidenceKind.TestFailed or EvidenceKind.AssertionFailed);

                bool allPlanItemsDone = context.CurrentPlan.Count > 0 && context.CurrentPlan.All(p => p.Done);

                if (hasFailedEvidence)
                {
                    // Verification failure -> Self-repair loop: transition to Reflect / Orient with repair directive
                    nextPhase = AgentLoopPhase.Reflect;
                    decision = new SupervisorDecision(ExecutionDecision.RepairProtocol, ContinuationReason.VerificationFailed);
                    directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                    statusMessage = "Verification failed; reflecting and initiating self-repair loop.";
                }
                else if (allPlanItemsDone && context.ConsecutiveRejections == 0)
                {
                    // Successfully verified complete
                    nextPhase = AgentLoopPhase.Completed;
                    decision = new SupervisorDecision(ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted);
                    directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                    statusMessage = "Verification passed; task complete.";
                }
                else
                {
                    // Standard reflection and plan update
                    nextPhase = AgentLoopPhase.Reflect;
                    decision = new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.None);
                    directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                    statusMessage = "Verification evaluated; reflecting on progress.";
                }
                break;

            case AgentLoopPhase.Reflect:
                // Phase 6 -> Phase 1: Reflect completed; starting next OODA-VR cycle or completing
                if (context.CurrentPlan.Count > 0 && context.CurrentPlan.All(p => p.Done) && context.ConsecutiveRejections == 0)
                {
                    nextPhase = AgentLoopPhase.Completed;
                    decision = new SupervisorDecision(ExecutionDecision.CompleteTask, ContinuationReason.CompletionAccepted);
                    statusMessage = "All work verified and reflected; task completed.";
                }
                else
                {
                    nextPhase = AgentLoopPhase.Observe;
                    decision = new SupervisorDecision(ExecutionDecision.ContinueStep, ContinuationReason.None);
                    directive = ExecutionDispatcher.Build(decision.Value, context.CurrentStep?.StepId, context.CurrentStep?.Title, ActionObligation.FromStep(context.CurrentStep));
                    statusMessage = "Reflection complete; starting next observation cycle.";
                }
                break;

            default:
                nextPhase = AgentLoopPhase.Observe;
                statusMessage = "Resetting to Observe phase.";
                break;
        }

        return Task.FromResult(new AgentLoopStepResult(
            NextPhase: nextPhase,
            Decision: decision,
            Directive: directive,
            StateDelta: context.CurrentStateDelta,
            UpdatedScratchpad: context.Scratchpad,
            ExecutedTools: null,
            ToolResults: null,
            ProducedEvidence: null,
            ModelResponse: null,
            IsTerminal: nextPhase is AgentLoopPhase.Completed or AgentLoopPhase.Failed,
            StatusMessage: statusMessage));
    }
}
