using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Klydis.Core.Chat;
using Klydis.Core.Memory;

namespace Klydis.Core.Tasks;

/// <summary>A decision the supervisor produced, recorded so a process death mid-dispatch is
/// recoverable and diagnosable (P1.12). Persisted durably (review §15) — the in-memory copy
/// is a cache of the execution_decisions table, not the source of truth.</summary>
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
/// The run-scoped, versioned execution evidence ledger (P1.12, review §2). This is the home
/// of DURABLE evidence — distinct from <see cref="StateDelta"/>, which is turn-local. It is
/// the backbone of the completion gate and recovery:
///
///   - evidence is recorded per run with the workspace version at recording time;
///   - a file change (write_file/edit_file) bumps the workspace version, which INVALIDATES
///     every older entry (build/preview evidence is only valid against the code it verified);
///   - the completion gate then refuses completion while required verification is stale or
///     missing ("plan marked complete but build never ran" can no longer seal a task);
///   - every evidence row and decision is PERSISTED to SQLite, and a fresh run rehydrates the
///     task's surviving (non-invalidated) evidence — a process crash cannot erase a recorded
///     BuildPassed, so completion recovery across restarts works.
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
        public readonly EpistemicLedger Epistemic = new();
    }

    private readonly MessageStore? _store;
    private readonly ConcurrentDictionary<string, RunLedger> _runs = new(StringComparer.Ordinal);

    /// <summary>Constructs the ledger. When a store is supplied, evidence/decisions are
    /// persisted durably and rehydrated on <see cref="Reset"/>; without one the ledger is
    /// in-memory only (legacy/test behavior).</summary>
    public ExecutionEvidenceLedger(MessageStore? store = null)
    {
        _store = store;
    }

    private RunLedger Ledger(string runKey)
        => _runs.GetOrAdd(runKey ?? string.Empty, _ => new RunLedger());

    /// <summary>
    /// Gets the authoritative epistemic ledger for a task/run.
    /// </summary>
    public EpistemicLedger GetEpistemicLedger(string runKey)
        => Ledger(runKey).Epistemic;

    /// <summary>
    /// Resets the ledger for a key — called when a FRESH run starts (a continued run keeps
    /// its ledger so evidence survives user turns within the run). With a durable store, the
    /// reset REHYDRATES the task's surviving evidence: the new run inherits the previous
    /// run's workspace version and non-invalidated verification facts, so a crash cannot
    /// erase a recorded BuildPassed and completion recovery works (review §2).
    /// </summary>
    public void Reset(string runKey)
    {
        _runs[runKey ?? string.Empty] = new RunLedger();
        if (_store == null) return;
        try
        {
            var rows = _store.GetCurrentExecutionEvidenceAsync(runKey ?? string.Empty).GetAwaiter().GetResult();
            var ledger = Ledger(runKey ?? string.Empty);
            lock (ledger)
            {
                foreach (var row in rows)
                {
                    ledger.Evidence.Add(new EvidenceLedgerEntry(
                        new Evidence(
                            Kind: row.Kind,
                            Description: string.Empty,
                            TimestampUtc: row.TimestampUtc,
                            Subject: row.Subject,
                            ToolName: row.ToolName,
                            StepId: row.StepId,
                            ExitCode: row.ExitCode,
                            Payload: row.PayloadJson,
                            WorkspaceVersion: row.WorkspaceVersion),
                        row.WorkspaceVersion));

                    ledger.Epistemic.RecordFact(new EpistemicEntry(
                        Key: !string.IsNullOrWhiteSpace(row.Subject) ? row.Subject : row.Kind.ToString(),
                        Value: row.PayloadJson ?? string.Empty,
                        Source: EpistemicSource.VerifiedEvidence,
                        Authority: EpistemicAuthority.Verified,
                        Freshness: EpistemicFreshness.Current,
                        TimestampUtc: row.TimestampUtc,
                        WorkspaceVersion: row.WorkspaceVersion,
                        StepId: row.StepId));

                    if (row.WorkspaceVersion > ledger.WorkspaceVersion)
                    {
                        ledger.WorkspaceVersion = row.WorkspaceVersion;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Rehydration failure must not crash the run start; the in-memory ledger is empty
            // and verification fails closed (missing evidence ⇒ not eligible) rather than
            // wrongly completing. The DB stays the durable copy for the next recovery.
        }
    }

    /// <summary>Records evidence against the current workspace version and persists it. The
    /// optional run/action ids give the durable row its lineage (which run and which action
    /// produced it — review §2's Evidence record).</summary>
    public void RecordEvidence(string runKey, Evidence evidence, string? runId = null, string? actionId = null)
    {
        if (evidence == null) throw new ArgumentNullException(nameof(evidence));
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            // Stamp the workspace version into the evidence itself so predicates can demand
            // MinWorkspaceVersion and the durable row is self-describing.
            var stamped = evidence.WorkspaceVersion == 0
                ? evidence with { WorkspaceVersion = ledger.WorkspaceVersion }
                : evidence;
            ledger.Evidence.Add(new EvidenceLedgerEntry(stamped, ledger.WorkspaceVersion));

            var epistemicSource = stamped.Kind switch
            {
                EvidenceKind.UserFact => EpistemicSource.UserFact,
                EvidenceKind.BuildPassed or EvidenceKind.TestPassed or EvidenceKind.VerificationPassed => EpistemicSource.VerifiedEvidence,
                _ => EpistemicSource.RuntimeTool
            };
            ledger.Epistemic.RecordFact(new EpistemicEntry(
                Key: !string.IsNullOrWhiteSpace(stamped.Subject) ? stamped.Subject : stamped.Kind.ToString(),
                Value: !string.IsNullOrWhiteSpace(stamped.Payload) ? stamped.Payload : stamped.Description,
                Source: epistemicSource,
                Authority: stamped.Authority,
                Freshness: EpistemicFreshness.Current,
                TimestampUtc: stamped.TimestampUtc,
                WorkspaceVersion: stamped.WorkspaceVersion,
                StepId: stamped.StepId));

            if (_store == null) return;
            try
            {
                var row = new DurableEvidenceRecord(
                    EvidenceId: "E-" + Guid.NewGuid().ToString("N")[..12],
                    TaskId: runKey,
                    RunId: runId,
                    StepId: stamped.StepId,
                    ActionId: actionId,
                    WorkspaceVersion: stamped.WorkspaceVersion,
                    Kind: stamped.Kind,
                    Subject: stamped.Subject,
                    ToolName: stamped.ToolName,
                    TimestampUtc: stamped.TimestampUtc,
                    ExitCode: stamped.ExitCode,
                    PayloadJson: stamped.Payload);
                _store.SaveExecutionEvidenceAsync(row).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // A persistence failure must not take down the executing turn; the in-memory
                // copy still serves the completion gate for this process. The durable write
                // is retried on the next reset/rehydration cycle.
            }
        }
    }

    /// <summary>Records a file change — bumps the workspace version, invalidating every
    /// evidence entry recorded against an older version (stale build/preview evidence),
    /// durably when a store is present.</summary>
    public void NoteFileChanged(string runKey)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            ledger.WorkspaceVersion++;
            ledger.Epistemic.InvalidateOnWorkspaceChange(ledger.WorkspaceVersion);
            if (_store == null) return;
            try
            {
                _store.InvalidateExecutionEvidenceAsync(
                    runKey ?? string.Empty, ledger.WorkspaceVersion, DateTime.UtcNow).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // See RecordEvidence: the in-memory version bump still invalidates for this
                // process; the durable stamp catches up on the next write.
            }
        }
    }

    /// <summary>Records a supervisor decision for the run and persists it (review §15).</summary>
    public void RecordDecision(string runKey, ExecutionDecisionRecord decision)
    {
        var ledger = Ledger(runKey);
        lock (ledger)
        {
            ledger.Decisions.Add(decision);
            if (_store == null) return;
            try
            {
                _store.SaveExecutionDecisionAsync(decision).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // See RecordEvidence.
            }
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
