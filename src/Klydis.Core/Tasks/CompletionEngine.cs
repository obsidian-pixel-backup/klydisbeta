using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>
/// Concrete implementation of <see cref="ICompletionEngine"/> that uses deterministic supervisor rules
/// and evidence ledger validation to evaluate whether a task is complete.
/// </summary>
public sealed class CompletionEngine : ICompletionEngine
{
    /// <inheritdoc />
    public Task<CompletionDecision> EvaluateAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context == null || string.IsNullOrWhiteSpace(context.TaskId))
        {
            return Task.FromResult(new CompletionDecision(false, "Invalid run context: TaskId is missing."));
        }

        var plan = context.Plan ?? Array.Empty<ToolExecutor.PlanEntry>();
        var evidence = context.CurrentEvidence ?? Array.Empty<EvidenceLedgerEntry>();

        var eligibility = AgentSupervisor.EvaluateEligibility(plan, context.TaskId, evidence);
        var openItems = plan.Where(p => !p.Done).Select(p => p.Text).ToList();
        var verdict = AgentSupervisor.EvaluateCompletion(openItems, eligibility);

        return Task.FromResult(new CompletionDecision(
            IsComplete: verdict.Accepted,
            Reason: verdict.Reason,
            Eligibility: eligibility));
    }
}
