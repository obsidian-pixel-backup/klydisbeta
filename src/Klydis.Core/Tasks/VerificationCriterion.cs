using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// A verification PREDICATE (P1.14 / review §5): the evidence a step's verification
/// obligation actually requires. Matching is NOT kind-level only — a criterion may also
/// constrain the subject (the file path or command an evidence entry names), the exit code
/// (BuildPassed from a command that exited non-zero is NOT passing) and the workspace
/// version it was produced against (stale evidence must not satisfy). A null SubjectPattern
/// matches any subject of that kind; null ExitCode accepts any recorded exit code.
/// </summary>
public sealed record VerificationCriterion(
    EvidenceKind Kind,
    string? SubjectPattern = null,
    string? Description = null,
    int? ExitCode = null,
    int? MinWorkspaceVersion = null)
{
    /// <summary>
    /// True when the evidence satisfies this criterion:
    ///   - same kind;
    ///   - the subject matches the pattern (case-insensitive contains) when one is declared
    ///     (evidence without a subject never satisfies a subject-bearing criterion — an
    ///     unscoped "build passed" is not proof the named project builds);
    ///   - the exit code equals the required code when one is declared (default 0 semantics
    ///     for build/test/preview kinds: evidence that ran but failed must not satisfy);
    ///   - the workspace version is at least the required version when one is declared.
    /// </summary>
    public bool Satisfies(Evidence? evidence)
    {
        if (evidence == null || evidence.Kind != Kind) return false;
        if (SubjectPattern != null)
        {
            if (string.IsNullOrWhiteSpace(evidence.Subject) ||
                !evidence.Subject.Contains(SubjectPattern, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        if (ExitCode != null && evidence.ExitCode != ExitCode.Value) return false;
        if (MinWorkspaceVersion != null && evidence.WorkspaceVersion < MinWorkspaceVersion.Value) return false;
        return true;
    }

    public override string ToString()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (SubjectPattern != null) parts.Add($"subject ~ {SubjectPattern}");
        if (ExitCode != null) parts.Add($"exit = {ExitCode}");
        if (MinWorkspaceVersion != null) parts.Add($"ws >= {MinWorkspaceVersion}");
        return parts.Count == 0 ? Kind.ToString() : $"{Kind} ({string.Join(", ", parts)})";
    }
}