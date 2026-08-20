using System;
using System.Collections.Generic;
using System.Threading;
using Klydis.Core.Chat;
using Klydis.Core.Tasks;

namespace Klydis.Core.Chat;

/// <summary>
/// Immutable turn context capturing all session, task, run, workspace, model, protocol, and
/// security scope for a single primary user or autonomous turn.
/// Replaces mutable singleton properties across ChatEngine and ToolExecutor.
/// </summary>
public sealed record TurnContext
{
    public required string TurnId { get; init; }
    public required string SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public required string UserMessage { get; init; }
    public required InteractionMode Mode { get; init; }
    public string? WorkspaceRoot { get; init; }
    public required string ModelId { get; init; }
    public required string ProtocolKey { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public RiskLevel RiskPolicy { get; init; } = RiskLevel.Standard;
    public required CancellationToken CancellationToken { get; init; }
    public GoalBudget? Budget { get; init; }
    public int? MaxIterations { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Creates a child context with an updated step ID or model/protocol key for sub-iterations.
    /// </summary>
    public TurnContext WithStep(string? stepId, IReadOnlyList<string>? allowedTools = null)
    {
        return this with
        {
            StepId = stepId,
            AllowedTools = allowedTools ?? AllowedTools
        };
    }

    /// <summary>
    /// Creates a child context with a different model or protocol for routed generations.
    /// </summary>
    public TurnContext WithModel(string modelId, string protocolKey)
    {
        return this with
        {
            ModelId = modelId,
            ProtocolKey = protocolKey
        };
    }
}
