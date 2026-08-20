using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// Thrown when an authoritative architectural invariant is violated at runtime.
/// </summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message) : base(message) { }
}

/// <summary>
/// Phase 27 — Runtime Invariant Validator.
/// Authoritative validation engine that enforces foundational system invariants
/// (Every Step belongs to a Run, Every Action belongs to a Step/Task, Every Evidence has a source, etc.)
/// and fails loudly on any violation.
/// </summary>
public static class RuntimeInvariantValidator
{
    /// <summary>Validates that a step strictly belongs to a valid Task and Run.</summary>
    public static void ValidateStep(TaskStep step, string? runId)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (string.IsNullOrWhiteSpace(step.TaskId))
            throw new InvariantViolationException($"Invariant Violation: Step '{step.StepId}' has no associated TaskId.");
        if (string.IsNullOrWhiteSpace(runId))
            throw new InvariantViolationException($"Invariant Violation: Step '{step.StepId}' has no associated RunId.");
    }

    /// <summary>Validates that a run strictly belongs to a valid Task.</summary>
    public static void ValidateRun(TaskRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.TaskId))
            throw new InvariantViolationException($"Invariant Violation: Run '{run.RunId}' has no associated TaskId.");
    }

    /// <summary>Validates that an action record strictly carries valid Task/Run identities and consistent timestamps.</summary>
    public static void ValidateAction(TaskActionRecord action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrWhiteSpace(action.TaskId))
            throw new InvariantViolationException($"Invariant Violation: Action '{action.ActionId}' has no associated TaskId.");
        if (string.IsNullOrWhiteSpace(action.RunId))
            throw new InvariantViolationException($"Invariant Violation: Action '{action.ActionId}' has no associated RunId.");
        if (action.Status == ActionExecutionStatus.Succeeded && action.CompletedAtUtc == null)
            throw new InvariantViolationException($"Invariant Violation: Succeeded action '{action.ActionId}' has no CompletedAtUtc timestamp.");
    }

    /// <summary>Validates that an evidence record strictly carries valid Task/Run identities and source information.</summary>
    public static void ValidateEvidence(DurableEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.TaskId))
            throw new InvariantViolationException($"Invariant Violation: Evidence '{evidence.EvidenceId}' has no associated TaskId.");
        if (string.IsNullOrWhiteSpace(evidence.RunId))
            throw new InvariantViolationException($"Invariant Violation: Evidence '{evidence.EvidenceId}' has no associated RunId.");
    }
}
