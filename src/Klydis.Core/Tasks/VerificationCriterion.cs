using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// A verification PREDICATE (P1.14): the evidence a step's verification obligation actually
/// requires. Matching is NOT kind-level only — a criterion may also constrain the subject
/// (the file path or command an evidence entry names), so "BuildPassed for AnotherProject"
/// never satisfies "build MyApp". A null SubjectPattern matches any subject of that kind.
/// </summary>
public sealed record VerificationCriterion(
    EvidenceKind Kind,
    string? SubjectPattern = null,
    string? Description = null)
{
    /// <summary>
    /// True when the evidence satisfies this criterion: same kind, and the subject matches
    /// the pattern (case-insensitive contains) when one is declared. Evidence without a
    /// subject never satisfies a subject-bearing criterion — an unscoped "build passed" is
    /// not proof the named project builds.
    /// </summary>
    public bool Satisfies(Evidence? evidence)
    {
        if (evidence == null || evidence.Kind != Kind) return false;
        if (SubjectPattern == null) return true;
        return !string.IsNullOrWhiteSpace(evidence.Subject) &&
               evidence.Subject.Contains(SubjectPattern, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
        => SubjectPattern == null
            ? Kind.ToString()
            : $"{Kind} (subject ~ {SubjectPattern})";
}