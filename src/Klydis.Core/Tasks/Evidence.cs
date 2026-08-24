using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// The typed kind of verification evidence a tool execution produced (P1.10). A Verification
/// step is not verified by "a tool ran successfully" — it is verified by EVIDENCE OF A
/// SPECIFIC KIND. Reading package.json succeeding is not evidence the application builds; a
/// successful build command is. The supervisor's verification gate reasons over these kinds,
/// and a step's EXPECTED kinds (from <see cref="StepClassifier.ClassifyEvidenceKinds"/>) act
/// as the verification predicate: evidence must match what the step actually requires.
/// </summary>
public enum EvidenceKind
{
    /// <summary>Legacy/untyped evidence recorded before typed kinds existed (kept for back-compat; treated as verification-capable only when the step declares no typed criteria).</summary>
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
    AssertionPassed,
    AssertionFailed,

    /// <summary>An artifact was CREATED — proves existence, NOT correctness (never verification-capable).</summary>
    ArtifactCreated,

    /// <summary>An artifact was actually VALIDATED (rendered, checked, opened) — verification-capable.</summary>
    ArtifactValidated,

    /// <summary>A verified web search result listing from an external search engine.</summary>
    WebSearchResult,

    /// <summary>A structured web document successfully fetched, verified by SSRF policy and extracted.</summary>
    WebDocument,

    /// <summary>An external web source citation or reference link.</summary>
    WebSource,

    /// <summary>A specific factual claim verified against an authoritative primary web source document.</summary>
    WebFact,

    /// <summary>An authoritative system metric directly observed via runtime API or telemetry.</summary>
    SystemMetricObserved,

    /// <summary>A verified hardware or OS specification observed via native hardware inspection.</summary>
    HardwareSpecificationVerified,

    /// <summary>An observed process or system runtime state verified via OS process APIs.</summary>
    ProcessStateObserved,
    
    // NEW values from Phase 3:
    FileCreated,
    FileModified,
    FileDeleted,
    DiffGenerated,
    CommandExecuted,
    BuildSucceeded,
    ArtifactProduced,
    VerificationPassed,
    VerificationFailed,
    Observation
}

/// <summary>
/// Origin provenance of an evidence item.
/// </summary>
public enum EpistemicProvenance
{
    /// <summary>Generated directly by native runtime profiler/code.</summary>
    NativeRuntime = 0,

    /// <summary>Directly observed by external tool / system CLI query.</summary>
    RuntimeObserved = 1,

    /// <summary>Derived deterministically by runtime state machines.</summary>
    DerivedRuntime = 2,

    /// <summary>Authored by the LLM model (e.g. echo, string literals).</summary>
    ModelAuthored = 3,

    /// <summary>Unverified external or untrusted data.</summary>
    Untrusted = 4
}

/// <summary>
/// A single piece of typed verification evidence. The subject identifies WHAT was verified
/// (a project file, a URL, a command) so evidence cannot accidentally satisfy the wrong
/// step, and the step id ties it to the step that produced it (P1.10). The exit code (when
/// the producing tool captured one) lets predicates demand ExitCode == 0, not merely a
/// "command ran" kind — the runtime derives BuildPassed from ExitCode == 0 + expected
/// subject, never from the model's interpretation of output (review §6–§7).
/// </summary>
public sealed record Evidence(
    EvidenceKind Kind,
    string Description,
    DateTime TimestampUtc,
    string? Subject = null,
    string? ToolName = null,
    string? StepId = null,
    int? ExitCode = null,
    string? Payload = null,
    int WorkspaceVersion = 0,
    EpistemicAuthority Authority = EpistemicAuthority.Verified,
    EpistemicProvenance Provenance = EpistemicProvenance.RuntimeObserved)
{
    /// <summary>
    /// True when this evidence kind actually VERIFIES something (a build/test/preview result,
    /// a captured screenshot, a validated artifact, a satisfied requirement). Weak
    /// inspection-only kinds (FileExists, FileChanged), mere creation (ArtifactCreated),
    /// outcome-independent command success (CommandSucceeded) and failure kinds do not
    /// verify. NOTE: CommandSucceeded is intentionally NOT in this set for steps with typed
    /// criteria — "echo hello succeeded" must never satisfy "the application builds".
    /// </summary>
    public bool IsVerificationCapable => Kind is
        EvidenceKind.Unspecified or
        EvidenceKind.BuildPassed or
        EvidenceKind.TestPassed or
        EvidenceKind.PreviewLoaded or
        EvidenceKind.ScreenshotCaptured or
        EvidenceKind.RequirementSatisfied or
        EvidenceKind.ArtifactValidated or
        EvidenceKind.WebDocument or
        EvidenceKind.WebFact or
        EvidenceKind.SystemMetricObserved or
        EvidenceKind.HardwareSpecificationVerified or
        EvidenceKind.ProcessStateObserved;

    public static Evidence Requirement(string description, string? subject = null, string? toolName = null)
        => new(
            Kind: EvidenceKind.RequirementSatisfied,
            Description: description,
            TimestampUtc: DateTime.UtcNow,
            Subject: subject,
            ToolName: toolName);

    public static Evidence Command(string description, string command, int exitCode = 0, string? toolName = "run_command")
        => new(
            Kind: exitCode == 0 ? EvidenceKind.CommandSucceeded : EvidenceKind.CommandFailed,
            Description: description,
            TimestampUtc: DateTime.UtcNow,
            Subject: command,
            ToolName: toolName,
            ExitCode: exitCode);
}