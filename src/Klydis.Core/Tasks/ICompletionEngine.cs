using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Context provided to the completion engine to evaluate task/run completion eligibility.
/// </summary>
public sealed record RunContext(
    string TaskId,
    string? RunId,
    IReadOnlyList<ToolExecutor.PlanEntry>? Plan,
    IReadOnlyList<EvidenceLedgerEntry>? CurrentEvidence = null,
    IReadOnlyList<TaskActionRecord>? Actions = null);

/// <summary>
/// Deterministic decision regarding task completion eligibility.
/// </summary>
public sealed record CompletionDecision(
    bool IsComplete,
    string? Reason,
    CompletionEligibility? Eligibility = null);

/// <summary>
/// Evaluates whether a task or run meets all required completion criteria backed by durable evidence.
/// </summary>
public interface ICompletionEngine
{
    /// <summary>
    /// Evaluates completion for the specified run context.
    /// </summary>
    Task<CompletionDecision> EvaluateAsync(RunContext context, CancellationToken cancellationToken = default);
}
