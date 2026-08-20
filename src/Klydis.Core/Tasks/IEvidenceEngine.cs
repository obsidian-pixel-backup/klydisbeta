using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Klydis.Core.Tasks;

/// <summary>
/// Authoritative interface for recording, retrieving, and invalidating execution evidence (Phase 7).
/// Provides workspace-versioned evidence management with stale invalidation.
/// </summary>
public interface IEvidenceEngine
{
    /// <summary>Records evidence against the specified task's current workspace version.</summary>
    void RecordEvidence(string taskId, Evidence evidence, string? runId = null, string? actionId = null);

    /// <summary>Bumps the workspace version, invalidating previously recorded version-sensitive evidence.</summary>
    void NoteFileChanged(string taskId);

    /// <summary>Gets the current workspace version for a task.</summary>
    int GetWorkspaceVersion(string taskId);

    /// <summary>Gets all current (non-stale) evidence entries for a task.</summary>
    IReadOnlyList<EvidenceLedgerEntry> GetCurrentEvidence(string taskId);

    /// <summary>Returns true if there are unresolved failures in the current evidence ledger.</summary>
    bool HasUnresolvedFailures(string taskId);

    /// <summary>Resets/rehydrates the evidence ledger for a task.</summary>
    void Reset(string taskId);
}

/// <summary>
/// Standalone Evidence Engine implementation wrapping <see cref="ExecutionEvidenceLedger"/>.
/// </summary>
public sealed class EvidenceEngine : IEvidenceEngine
{
    private readonly ExecutionEvidenceLedger _ledger;

    public EvidenceEngine(ExecutionEvidenceLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public EvidenceEngine(Memory.MessageStore? store = null)
    {
        _ledger = new ExecutionEvidenceLedger(store);
    }

    /// <inheritdoc />
    public void RecordEvidence(string taskId, Evidence evidence, string? runId = null, string? actionId = null)
        => _ledger.RecordEvidence(taskId, evidence, runId, actionId);

    /// <inheritdoc />
    public void NoteFileChanged(string taskId)
        => _ledger.NoteFileChanged(taskId);

    /// <inheritdoc />
    public int GetWorkspaceVersion(string taskId)
        => _ledger.GetWorkspaceVersion(taskId);

    /// <inheritdoc />
    public IReadOnlyList<EvidenceLedgerEntry> GetCurrentEvidence(string taskId)
        => _ledger.GetCurrentEvidence(taskId);

    /// <inheritdoc />
    public bool HasUnresolvedFailures(string taskId)
        => _ledger.GetCurrentEvidence(taskId).Any(e => e.IsUnresolvedFailure);

    /// <inheritdoc />
    public void Reset(string taskId)
        => _ledger.Reset(taskId);
}
