using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// The second validation layer (P1.8): <see cref="ActionGate"/> answers \"is this tool call
/// legal?\" (static capability: existence, schema, types, command disguise); this answers
/// \"is this the right action for the actual current state?\" against the current
/// <see cref="ActionObligation"/> (step compatibility). Kept separate so diagnostics can tell
/// a hallucinated tool (gate) apart from a legal-but-wrong-for-this-step action (validator).
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
        => ActionGate.Validate(
            request,
            registeredTools,
            obligation?.AllowedTools,
            obligation == null ? null : obligation.Title);

    /// <summary>
    /// True when the obligation permits the tool — a helper for callers that already ran the
    /// gate and only need the step-compatibility answer.
    /// </summary>
    public static bool IsToolPermittedByStep(ActionObligation? obligation, string toolName)
        => obligation == null || obligation.IsToolAllowed(toolName);
}
