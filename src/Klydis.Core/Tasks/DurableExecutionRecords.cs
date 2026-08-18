using System;

namespace Klydis.Core.Tasks;

/// <summary>
/// The lifecycle state of an executed action (review §9–§10). The recovery-critical state is
/// <see cref="Unknown"/>: a process death mid-command leaves the action UNKNOWN — never
/// silently Succeeded or Failed — so recovery inspects the process/filesystem instead of
/// blindly re-executing the same action.
/// </summary>
public enum ActionExecutionStatus
{
    /// <summary>Recorded but not yet started.</summary>
    Pending,

    /// <summary>Execution is in flight (started, not yet completed).</summary>
    InProgress,

    /// <summary>Completed successfully.</summary>
    Succeeded,

    /// <summary>Completed with a failure.</summary>
    Failed,

    /// <summary>Cancelled before completion (user stop, teardown).</summary>
    Cancelled,

    /// <summary>Terminated by a timeout.</summary>
    TimedOut,

    /// <summary>The outcome is NOT known — the process died mid-execution, so the action may
    /// have completed, failed, or still be running. Recovery must inspect, never re-run
    /// blindly.</summary>
    Unknown
}

/// <summary>
/// A durable record of one executed action (review §9 — the TaskAction ledger). Every tool
/// execution the runtime performs is recorded here with its replay identity, side-effect
/// level, lifecycle status and result, so recovery can distinguish Succeeded / Failed /
/// Cancelled / TimedOut / Unknown instead of guessing from process memory.
/// </summary>
public sealed record TaskActionRecord(
    string ActionId,
    string? ReplayKey,
    string? TaskId,
    string? RunId,
    string? StepId,
    string? TurnId,
    string? ToolName,
    string? ArgumentsJson,
    ToolSideEffectLevel SideEffectLevel,
    ActionExecutionStatus Status,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc = null,
    string? ResultPreview = null,
    string? Error = null,
    string? ModelId = null,
    string? ProtocolKey = null);

/// <summary>
/// A durable evidence row (review §2 — the Evidence record). Persisted so verification
/// evidence (BuildPassed, PreviewLoaded, TestPassed) survives process restarts even when the
/// in-memory ledger is empty: a recovered run must still know the build was verified.
/// <see cref="InvalidatedAtUtc"/> is set when a file change bumps the workspace version past
/// the version this evidence was produced against.
/// </summary>
public sealed record DurableEvidenceRecord(
    string EvidenceId,
    string? TaskId,
    string? RunId,
    string? StepId,
    string? ActionId,
    int WorkspaceVersion,
    EvidenceKind Kind,
    string? Subject,
    string? ToolName,
    DateTime TimestampUtc,
    int? ExitCode = null,
    string? PayloadJson = null,
    DateTime? InvalidatedAtUtc = null);
