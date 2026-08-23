using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The runtime facts the semantic validation layer needs beyond the static tool surface
/// (P1.10/P1.14): whether completion is currently eligible (the checklist gate's evidence
/// dimension) and the run's executed-action set. Used only by
/// <see cref="ActionValidator"/>'s contextual overload.
/// </summary>
public sealed record ActionValidationContext(
    bool CompletionIsEligible = true,
    string? CompletionIneligibilityReason = null,
    IReadOnlySet<string>? RunAlreadyExecuted = null,
    string? WorkspaceRoot = null,
    Klydis.Core.Workspace.AgentWorkspaceContext? WorkspaceContext = null);

/// <summary>
/// The second validation layer (P1.8): <see cref="ActionGate"/> answers "is this tool call
/// legal?" (static capability: existence, schema, arguments, command disguise, replay,
/// workspace); this answers "is this the RIGHT action for the current state?" against the
/// current <see cref="ActionObligation"/> (step compatibility) and the contextual semantics
/// (P1.14: completion-eligibility of task_complete claims). Kept separate so diagnostics can
/// tell a hallucinated tool (gate) apart from a legal-but-wrong-for-this-state action
/// (validator).
///
/// The step's AllowedTools — produced by <see cref="StepClassifier"/> into the TaskStep and
/// carried by the obligation — is the enforcement mechanism: a Verification step cannot call
/// write_file because write_file is simply not in the obligation's allowed set.
/// </summary>
public static class ActionValidator
{
    /// <summary>
    /// Validates an action against the registered surface AND the current step's obligation.
    /// Null obligation = no step contract (existence-gated only). Never throws.
    /// </summary>
    public static ActionGateVerdict ValidateForStep(
        ToolCallRequest request,
        IEnumerable<ToolDefinition> registeredTools,
        ActionObligation? obligation)
        => ValidateForStep(request, registeredTools, obligation, new ActionValidationContext());

    /// <summary>
    /// Validates an action against the registered surface, the current step's obligation AND
    /// the current execution semantics (P1.14):
    ///
    ///   1. the ActionGate verdict (existence, step scoping, schema, replay, workspace);
    ///   2. completion semantics — a task_complete claim is rejected with
    ///      <see cref="ActionGateError.PrematureCompletion"/> while the runtime's completion
    ///      eligibility is false. The model is never allowed to "finish" a task the
    ///      evidence says is unfinished — the eligibility object (open items, verification
    ///      predicates, unresolved failures) is the ONLY authority.
    ///
    /// Never throws. Never executes.
    /// </summary>
    public static ActionGateVerdict ValidateForStep(
        ToolCallRequest request,
        IEnumerable<ToolDefinition> registeredTools,
        ActionObligation? obligation,
        ActionValidationContext context)
    {
        var verdict = ActionGate.Validate(
            request,
            registeredTools,
            obligation?.AllowedTools,
            obligation == null ? null : obligation.Title,
            // REVIEW §12: the task workspace root is propagated through the context so the
            // WorkspaceBoundaryValidator is active for EVERY filesystem action in the live
            // gate path — not just the call sites that remember to pass it.
            workspaceRoot: context.WorkspaceRoot,
            alreadyExecuted: context.RunAlreadyExecuted,
            workspaceContext: context.WorkspaceContext);
        if (!verdict.Allowed) return verdict;

        // Completion claims are SEMANTIC, not just schema: the runtime's eligibility is the
        // only authority on whether the task may be finished. A claim while the evidence
        // does not back the checklist is rejected here, before any execution.
        if (string.Equals(request.Name, "task_complete", StringComparison.OrdinalIgnoreCase) &&
            !context.CompletionIsEligible)
        {
            return new ActionGateVerdict(false, ActionGateError.PrematureCompletion,
                context.CompletionIneligibilityReason ??
                "Completion is not yet eligible: the task's verification is not satisfied. " +
                "Finish the open work and re-verify before claiming completion.",
                null, obligation == null ? null : obligation.Title);
        }

        return verdict;
    }

    /// <summary>
    /// True when the obligation permits the tool — a helper for callers that already ran the
    /// gate and only need the step-compatibility answer.
    /// </summary>
    public static bool IsToolPermittedByStep(ActionObligation? obligation, string toolName)
        => obligation == null || obligation.IsToolAllowed(toolName);
}