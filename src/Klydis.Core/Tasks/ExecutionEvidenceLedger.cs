using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Tasks;

/// <summary>A decision the supervisor produced, recorded so a process death mid-dispatch is
/// recoverable and diagnosable (P1.12 runtime phase A). In-memory + logged for now; the
/// durable store write lands with the persistence milestone.</summary>
public sealed record ExecutionDecisionRecord(
    string DecisionId,
    string? TaskId,
    string? RunId,
    string? StepId,
    ExecutionDecision Decision,
    ContinuationReason Reason,
    DateTime TimestampUtc);

/// <summary>An evidence entry held in the run ledger, stamped with the workspace version it
/// was produced against. An entry whose version no longer equals the current workspace
/// version is STALE — file changes after it are assumed to have invalidated it.</summary>
public sealed record EvidenceLedgerEntry(Evidence Evidence, int WorkspaceVersion)
{
    /// <summary>True when this entry was produced against the CURRENT workspace version —
    /// i.e. no file change has invalidated it since.</summary>
    public bool IsCurrent(int currentWorkspaceVersion) => WorkspaceVersion == currentWorkspaceVersion;

    /// <summary>True when the evidence kind is an unresolved FAILURE (build/test/preview/
    /// command failed) that is still current.</summary>
    public bool IsUnresolvedFailure => Evidence.Kind is
        EvidenceKind.BuildFailed or
        EvidenceKind.TestFailed or
        EvidenceKind.PreviewFailed or
        EvidenceKind.CommandFailed;
}

/// <summary>
/// The run-scoped, versioned execution evidence ledger (P1.12). This is the home of
/// DURABLE evidence — distinct from <see cref="StateDelta"/>, which is turn-local. It is
/// the backbone of the completion gate and recovery:
///
///   - evidence is recorded per run with the workspace version at recording time;
///   - a file change (write_file/edit_file) bumps the workspace version, which INVALIDATES
///     every older entry (build/preview evidence is only valid against the code it verified);
///   - the completion gate then refuses completion while required verification is stale or
///     missing ("plan marked complete but build never ran" can no longer seal a task).
///
/// Thread-safe; keyed by task (reset when a fresh run starts).
/// </summary>
public sealed class ExecutionEvidenceLedger
{
    private sealed class RunLedger
    {
        public int WorkspaceVersion;
        public readonly List<EvidenceLedgerEntry> Evidence = new();
        public readonly List<ExecutionDecisionRecord> Decisions = new();
    }

    private readonly ConcurrentDictionary<string, RunLedger> _runs = new(StringComparer.Ordinal);

    private RunLedger Ledger(string runKey)
        => _runs.GetOrAdd(runKey ?? string.Empty, _ => new RunLedger());

    /// <summary>Resets the ledger for a key — called when a FRESH run starts (a continued run
    /// keeps its ledger so evidence survives user turns within the run).</summary>
    public void Reset(string runKey)
    {
        _runs[runKey ?? string.Empty] = new RunLedger();
    }

    /// <summary>Records evidence against the current workspace version.</summary>
    public void RecordEvidence(string runKey, Evidence evidence)
    {
        if (evidence == null) throw new ArgumentNullException(nameof(evidence));
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            ledger.Evidence.Add(new EvidenceLedgerEntry(evidence, ledger.WorkspaceVersion));
        }
    }

    /// <summary>Records a file change — bumps the workspace version, invalidating every
    /// evidence entry recorded against an older version (stale build/preview evidence).</summary>
    public void NoteFileChanged(string runKey)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            ledger.WorkspaceVersion++;
        }
    }

    /// <summary>Records a supervisor decision for the run.</summary>
    public void RecordDecision(string runKey, ExecutionDecisionRecord decision)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            ledger.Decisions.Add(decision);
        }
    }

    /// <summary>The current workspace version for the run (0 = no changes yet).</summary>
    public int GetWorkspaceVersion(string runKey)
        => Ledger(runKey).WorkspaceVersion;

    /// <summary>All evidence entries that are still CURRENT (recorded at the current
    /// workspace version). Stale entries are invisible to verification.</summary>
    public IReadOnlyList<EvidenceLedgerEntry> GetCurrentEvidence(string runKey)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            return ledger.Evidence
                .Where(e => e.IsCurrent(ledger.WorkspaceVersion))
                .ToArray();
        }
    }

    /// <summary>All evidence entries recorded in the run, current or stale.</summary>
    public IReadOnlyList<EvidenceLedgerEntry> GetAllEvidence(string runKey)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            return ledger.Evidence.ToArray();
        }
    }

    /// <summary>All recorded supervisor decisions for the run (most recent last).</summary>
    public IReadOnlyList<ExecutionDecisionRecord> GetDecisions(string runKey)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            return ledger.Decisions.ToArray();
        }
    }
}