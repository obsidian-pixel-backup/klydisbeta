using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Explicit runtime-governed continuation state.
/// Tracks why an autonomous continuation turn exists so the runtime owns the execution loop
/// instead of relying on model self-continuation heuristics.
/// </summary>
public sealed record ContinuationState(
    string ParentGenerationId,
    string ExpectedContinuation,
    string? OutstandingAction,
    int ContinuationAttempt,
    int ContinuationBudget,
    DateTime TimestampUtc)
{
    /// <summary>Maximum autonomous continuation retries allowed for a single parent step.</summary>
    public const int MaxContinuationAttempts = 3;

    /// <summary>True when continuation retries have been exhausted.</summary>
    public bool IsExhausted => ContinuationAttempt >= MaxContinuationAttempts;
}
