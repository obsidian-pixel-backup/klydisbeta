using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// Detailed evaluation report from verifying a task step against recorded evidence.
/// </summary>
public sealed record StepVerificationReport(
    bool Verified,
    IReadOnlyList<VerificationCriterion> Criteria,
    IReadOnlyList<VerificationCriterion> SatisfiedCriteria,
    IReadOnlyList<VerificationCriterion> UnsatisfiedCriteria,
    string? Reason);

/// <summary>
/// Authoritative interface for evaluating step verification against factual evidence (Phase 8).
/// Enforces anti-simulation: model assertions without corresponding verification evidence are rejected.
/// </summary>
public interface IVerificationEngine
{
    /// <summary>
    /// Evaluates whether the given task step has satisfied its required verification obligations
    /// based on the factual evidence recorded against the current workspace version.
    /// </summary>
    StepVerificationReport EvaluateStep(
        TaskStep step,
        IReadOnlyList<Evidence> evidence,
        int currentWorkspaceVersion);
}

/// <summary>
/// Deterministic verification engine implementing typed predicate matching.
/// </summary>
public sealed class VerificationEngine : IVerificationEngine
{
    /// <inheritdoc />
    public StepVerificationReport EvaluateStep(
        TaskStep step,
        IReadOnlyList<Evidence> evidence,
        int currentWorkspaceVersion)
    {
        if (step == null) throw new ArgumentNullException(nameof(step));

        var currentEvidence = (evidence ?? Array.Empty<Evidence>())
            .Where(e => e.WorkspaceVersion == currentWorkspaceVersion)
            .ToList();

        // 1. Resolve criteria for the step
        var criteria = new List<VerificationCriterion>();
        if (step.VerificationCriteria != null && step.VerificationCriteria.Count > 0)
        {
            foreach (var critStr in step.VerificationCriteria)
            {
                if (Enum.TryParse<EvidenceKind>(critStr, out var kind))
                {
                    criteria.Add(new VerificationCriterion(kind, MinWorkspaceVersion: currentWorkspaceVersion));
                }
                else
                {
                    // If stored as full predicate (e.g. "BuildPassed:src/App.csproj")
                    var parts = critStr.Split(':', 2);
                    if (parts.Length == 2 && Enum.TryParse<EvidenceKind>(parts[0], out var parsedKind))
                    {
                        criteria.Add(new VerificationCriterion(parsedKind, SubjectPattern: parts[1], MinWorkspaceVersion: currentWorkspaceVersion));
                    }
                }
            }
        }

        if (criteria.Count == 0)
        {
            var classified = StepClassifier.ClassifyCriteria(step.Title);
            criteria.AddRange(classified.Select(c => c with { MinWorkspaceVersion = currentWorkspaceVersion }));
        }

        // Non-verification step with no criteria
        if (criteria.Count == 0 && step.ExpectedActionKind != StepActionKind.Verification)
        {
            return new StepVerificationReport(
                Verified: true,
                Criteria: Array.Empty<VerificationCriterion>(),
                SatisfiedCriteria: Array.Empty<VerificationCriterion>(),
                UnsatisfiedCriteria: Array.Empty<VerificationCriterion>(),
                Reason: "Step has no verification obligations.");
        }

        // Fallback for verification step with no specific predicates: requires at least one verification-capable evidence
        if (criteria.Count == 0)
        {
            bool hasGenericVerification = currentEvidence.Any(e => e.IsVerificationCapable);
            return new StepVerificationReport(
                Verified: hasGenericVerification,
                Criteria: Array.Empty<VerificationCriterion>(),
                SatisfiedCriteria: Array.Empty<VerificationCriterion>(),
                UnsatisfiedCriteria: Array.Empty<VerificationCriterion>(),
                Reason: hasGenericVerification ? "Generic verification evidence observed." : "Missing verification-capable evidence.");
        }

        var satisfied = new List<VerificationCriterion>();
        var unsatisfied = new List<VerificationCriterion>();

        foreach (var crit in criteria)
        {
            if (currentEvidence.Any(e => crit.Satisfies(e)))
            {
                satisfied.Add(crit);
            }
            else
            {
                unsatisfied.Add(crit);
            }
        }

        bool allSatisfied = unsatisfied.Count == 0;
        string? reason = allSatisfied
            ? $"All {criteria.Count} verification criteria satisfied by current evidence."
            : $"Unsatisfied verification criteria: [{string.Join(", ", unsatisfied.Select(u => u.ToString()))}].";

        return new StepVerificationReport(
            Verified: allSatisfied,
            Criteria: criteria,
            SatisfiedCriteria: satisfied,
            UnsatisfiedCriteria: unsatisfied,
            Reason: reason);
    }
}
