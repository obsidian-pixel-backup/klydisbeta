using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Capabilities.Policy;

/// <summary>
/// Policy Gate enforcing authority modes, safety tiers, and user overrides.
/// </summary>
public sealed class CapabilityPolicyGate : IPolicyGate
{
    private readonly ConcurrentDictionary<string, PolicyDecision> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CapabilityPolicyGate>? _logger;

    public AuthorityMode Mode { get; set; }

    public CapabilityPolicyGate(AuthorityMode initialMode = AuthorityMode.LocalFullControl, ILogger<CapabilityPolicyGate>? logger = null)
    {
        Mode = initialMode;
        _logger = logger;
    }

    /// <summary>
    /// Sets an explicit policy decision override for a capability ID.
    /// </summary>
    public void SetOverride(string capabilityId, PolicyDecision decision)
    {
        _overrides[capabilityId] = decision;
        _logger?.LogInformation("Policy override set for {CapabilityId} -> {Decision}", capabilityId, decision);
    }

    public Task<PolicyEvaluationResult> EvaluateAsync(
        ICapability capability,
        CapabilityRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(request);

        // 1. Check explicit capability override
        if (_overrides.TryGetValue(capability.Id, out var customDecision))
        {
            return Task.FromResult(customDecision switch
            {
                PolicyDecision.Auto => PolicyEvaluationResult.Auto($"Explicit policy override: {capability.Id} is AUTO"),
                PolicyDecision.Confirm => PolicyEvaluationResult.Confirm($"Explicit policy override: {capability.Id} requires confirmation"),
                _ => PolicyEvaluationResult.Deny($"Explicit policy override: {capability.Id} is DENIED")
            });
        }

        // 2. Strict Read-Only Mode
        if (Mode == AuthorityMode.StrictReadOnly)
        {
            if (capability.Policy != PolicyDefault.Auto)
            {
                return Task.FromResult(PolicyEvaluationResult.Deny(
                    $"Capability '{capability.Id}' is denied because Klydis is running in StrictReadOnly mode."));
            }
            return Task.FromResult(PolicyEvaluationResult.Auto("Permitted read-only operation."));
        }

        // 3. Local Full Control Mode
        if (Mode == AuthorityMode.LocalFullControl)
        {
            if (capability.Policy == PolicyDefault.Deny)
            {
                return Task.FromResult(PolicyEvaluationResult.Deny(
                    $"Capability '{capability.Id}' is classified as hard-DENY by safety policy."));
            }
            return Task.FromResult(PolicyEvaluationResult.Auto("Permitted by LocalFullControl authority."));
        }

        // 4. Standard Supervised Mode
        return Task.FromResult(capability.Policy switch
        {
            PolicyDefault.Auto => PolicyEvaluationResult.Auto("Permitted non-mutating operation."),
            PolicyDefault.Confirm => PolicyEvaluationResult.Confirm($"Capability '{capability.Id}' modifies machine/filesystem state and requires confirmation."),
            _ => PolicyEvaluationResult.Deny($"Capability '{capability.Id}' is denied in Supervised mode.")
        });
    }
}
