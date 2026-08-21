using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Capabilities.Policy;

/// <summary>
/// The decision result from evaluating a capability request against the policy engine.
/// </summary>
public enum PolicyDecision
{
    /// <summary>Execution is automatically permitted.</summary>
    Auto,

    /// <summary>Execution requires explicit interactive user confirmation.</summary>
    Confirm,

    /// <summary>Execution is explicitly denied.</summary>
    Deny
}

/// <summary>
/// Result of evaluating a capability request through the Policy Gate.
/// </summary>
public sealed record PolicyEvaluationResult(
    PolicyDecision Decision,
    string Reason,
    bool IsAllowed = true
)
{
    public static PolicyEvaluationResult Auto(string reason = "Automatically permitted by policy.") =>
        new(PolicyDecision.Auto, reason, true);

    public static PolicyEvaluationResult Confirm(string reason) =>
        new(PolicyDecision.Confirm, reason, false);

    public static PolicyEvaluationResult Deny(string reason) =>
        new(PolicyDecision.Deny, reason, false);
}

/// <summary>
/// Gate interface evaluating safety policies, permissions, and authority levels before capability execution.
/// </summary>
public interface IPolicyGate
{
    /// <summary>
    /// Current operating authority mode.
    /// </summary>
    AuthorityMode Mode { get; set; }

    /// <summary>
    /// Evaluates a capability request to determine whether it can run automatically, requires confirmation, or is denied.
    /// </summary>
    Task<PolicyEvaluationResult> EvaluateAsync(
        ICapability capability,
        CapabilityRequest request,
        CancellationToken ct = default);
}
