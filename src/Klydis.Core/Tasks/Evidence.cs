using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// The typed kind of verification evidence a tool execution produced (P1.10). A Verification
/// step is not verified by "a tool ran successfully" — it is verified by EVIDENCE OF A
/// SPECIFIC KIND. Reading package.json succeeding is not evidence the application builds; a
/// successful build command is. The supervisor's verification gate reasons over these kinds.
/// </summary>
public enum EvidenceKind
{
    /// <summary>Legacy/untyped evidence recorded before typed kinds existed (kept for back-compat; treated as verification-capable).</summary>
    Unspecified,

    BuildPassed,
    BuildFailed,
    TestPassed,
    TestFailed,
    PreviewStarted,
    PreviewLoaded,
    PreviewFailed,
    FileExists,
    FileChanged,
    CommandSucceeded,
    CommandFailed,
    ScreenshotCaptured,
    RequirementSatisfied,
    ArtifactCreated
}

/// <summary>A single piece of typed verification evidence.</summary>
public sealed record Evidence(EvidenceKind Kind, string Description, DateTime TimestampUtc)
{
    /// <summary>
    /// True when this evidence kind actually VERIFIES something (a build/test/preview result,
    /// a captured screenshot, a satisfied requirement, a successful command). Weak
    /// inspection-only kinds (FileExists, FileChanged) and failure kinds do not verify.
    /// </summary>
    public bool IsVerificationCapable => Kind is
        EvidenceKind.Unspecified or
        EvidenceKind.BuildPassed or
        EvidenceKind.TestPassed or
        EvidenceKind.PreviewLoaded or
        EvidenceKind.ScreenshotCaptured or
        EvidenceKind.RequirementSatisfied or
        EvidenceKind.CommandSucceeded or
        EvidenceKind.ArtifactCreated;
}
