using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Capabilities.Policy;
using Klydis.Core.Chat;
using Klydis.Core.Epistemic;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Capabilities.Bridge;

/// <summary>
/// Bridges first-class machine capabilities into the ToolExecutor / LLM agent loop.
/// Handles precondition checking, policy gating, execution, postcondition verification, and Fact Ledger sync.
/// </summary>
public sealed class CapabilityToolBridge
{
    private readonly ICapabilityRegistry _registry;
    private readonly IWorldModel _worldModel;
    private readonly IPolicyGate _policyGate;
    private readonly ILogger<CapabilityToolBridge>? _logger;

    public CapabilityToolBridge(
        ICapabilityRegistry registry,
        IWorldModel worldModel,
        IPolicyGate policyGate,
        ILogger<CapabilityToolBridge>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _worldModel = worldModel ?? throw new ArgumentNullException(nameof(worldModel));
        _policyGate = policyGate ?? throw new ArgumentNullException(nameof(policyGate));
        _logger = logger;
    }

    /// <summary>
    /// Gets the registered capability registry.
    /// </summary>
    public ICapabilityRegistry Registry => _registry;

    /// <summary>
    /// Gets the epistemic world model.
    /// </summary>
    public IWorldModel WorldModel => _worldModel;

    /// <summary>
    /// Gets the policy gate.
    /// </summary>
    public IPolicyGate PolicyGate => _policyGate;

    /// <summary>
    /// Checks if a tool name matches any registered machine capability.
    /// </summary>
    public bool CanHandle(string toolName) => _registry.Contains(toolName);

    /// <summary>
    /// Executes a capability request through the complete closed-loop harness.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(
        string capabilityId,
        IDictionary<string, object>? arguments,
        string? taskId = null,
        string? runId = null,
        CancellationToken ct = default)
    {
        var capability = _registry.Get(capabilityId);
        if (capability is null)
        {
            return new ToolResult(capabilityId, false, "", $"Capability '{capabilityId}' not found in registry.", true);
        }

        var readOnlyArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments != null)
        {
            foreach (var (k, v) in arguments)
            {
                readOnlyArgs[k] = v;
            }
        }
        var request = new CapabilityRequest(
            CapabilityId: capabilityId,
            Parameters: readOnlyArgs,
            TaskId: taskId,
            RunId: runId,
            Reason: null
        );

        // 1. Policy Gate Evaluation
        var policyResult = await _policyGate.EvaluateAsync(capability, request, ct);
        if (policyResult.Decision == PolicyDecision.Deny)
        {
            _logger?.LogWarning("Policy gate denied execution for {CapabilityId}: {Reason}", capabilityId, policyResult.Reason);
            return new ToolResult(capabilityId, false, "", $"Policy Gate Denied: {policyResult.Reason}", true);
        }

        if (policyResult.Decision == PolicyDecision.Confirm)
        {
            _logger?.LogInformation("Capability {CapabilityId} requires confirmation: {Reason}", capabilityId, policyResult.Reason);
            // In supervised mode, if not pre-approved, flag approval requirement
        }

        // 2. Preconditions Check
        var preCheck = await capability.CheckPreconditionsAsync(request, _worldModel, ct);
        if (!preCheck.IsSatisfied)
        {
            string failureMsg = preCheck.FailureReason ?? "Preconditions for capability were not satisfied.";
            _logger?.LogWarning("Precondition failure for {CapabilityId}: {Reason}", capabilityId, failureMsg);
            return new ToolResult(capabilityId, false, "", $"Precondition Failed: {failureMsg}", true);
        }

        // 3. Execution
        _logger?.LogInformation("Executing capability: {CapabilityId}", capabilityId);
        var execResult = await capability.ExecuteAsync(request, ct);

        // 4. Postconditions & Verification
        var verifResult = await capability.VerifyPostconditionsAsync(request, execResult, _worldModel, ct);

        // 5. Fact Ledger Commit
        if (verifResult.EstablishedFacts != null)
        {
            foreach (var fact in verifResult.EstablishedFacts)
            {
                await _worldModel.AssertFactAsync(fact, ct);
            }
        }

        if (verifResult.InvalidatedFacts != null)
        {
            foreach (var inv in verifResult.InvalidatedFacts)
            {
                await _worldModel.InvalidateAsync(inv.Domain, inv.EntityKey, "Invalidated by postcondition check", ct);
            }
        }

        // 6. Format Output
        string outputText = execResult.Evidence?.RawOutput
            ?? (execResult.Data != null ? JsonSerializer.Serialize(execResult.Data, new JsonSerializerOptions { WriteIndented = true }) : "");

        return new ToolResult(
            ToolName: capabilityId,
            Success: execResult.Success && verifResult.IsVerified,
            Output: outputText,
            Error: execResult.Error ?? (!verifResult.IsVerified ? verifResult.Explanation : null),
            IsValidationError: false,
            ExitCode: execResult.ExitCode
        );
    }
}
