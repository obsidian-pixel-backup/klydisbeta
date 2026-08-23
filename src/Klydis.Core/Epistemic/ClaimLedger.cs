using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Klydis.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Epistemic;

/// <summary>
/// Authoritative 6-level Evidence Hierarchy (P0/P2).
/// Defines the epistemic authority of factual statements in the runtime.
/// </summary>
public enum EvidenceLevel
{
    /// <summary>Level 0: Direct user-provided constraint or parameter.</summary>
    Level0_UserProvidedFact = 0,

    /// <summary>Level 1: Direct tool execution observation (OS API, hardware counter, network result).</summary>
    Level1_DirectObservation = 1,

    /// <summary>Level 2: Deterministic mathematical or structured derivation from Level 1 data.</summary>
    Level2_DerivedDeterministicFact = 2,

    /// <summary>Level 3: Verified empirical inference (e.g. build passed, test suite succeeded).</summary>
    Level3_VerifiedInference = 3,

    /// <summary>Level 4: Model hypothesis or diagnostic inference (must be qualified in final output).</summary>
    Level4_ModelHypothesis = 4,

    /// <summary>Level 5: Unsupported claim without backing observation (rejected from authoritative reports).</summary>
    Level5_UnsupportedClaim = 5
}

/// <summary>
/// Status of a recorded factual claim in the ledger.
/// </summary>
public enum ClaimStatus
{
    Pending,
    Verified,
    Qualified,
    Hypothesis,
    Rejected
}

/// <summary>
/// Immutable record of an asserted factual claim with provenance and evidence level.
/// </summary>
public sealed record ClaimRecord(
    string ClaimId,
    string? TaskId,
    string? RunId,
    string? StepId,
    string ClaimText,
    string? Domain,
    string? Property,
    string? Value,
    string? SourceType,
    string? SourceId,
    EvidenceLevel Level,
    ClaimStatus Status,
    double Confidence,
    int WorkspaceVersion,
    DateTime CreatedAtUtc);

/// <summary>
/// Epistemic Claim Ledger (P0/P2) that maintains factual assertions with provenance,
/// versioning, and authoritative evidence levels.
/// </summary>
public sealed class ClaimLedger
{
    private readonly ConcurrentDictionary<string, ClaimRecord> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly MessageStore? _store;
    private readonly ILogger<ClaimLedger>? _logger;

    public ClaimLedger(MessageStore? store = null, ILogger<ClaimLedger>? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Records a factual claim into the ledger.
    /// </summary>
    public ClaimRecord RecordClaim(
        string claimText,
        EvidenceLevel level,
        string? taskId = null,
        string? runId = null,
        string? stepId = null,
        string? domain = null,
        string? property = null,
        string? value = null,
        string? sourceType = null,
        string? sourceId = null,
        double confidence = 1.0,
        int workspaceVersion = 0)
    {
        var status = level switch
        {
            EvidenceLevel.Level0_UserProvidedFact or
            EvidenceLevel.Level1_DirectObservation or
            EvidenceLevel.Level2_DerivedDeterministicFact or
            EvidenceLevel.Level3_VerifiedInference => ClaimStatus.Verified,
            EvidenceLevel.Level4_ModelHypothesis => ClaimStatus.Hypothesis,
            _ => ClaimStatus.Rejected
        };

        string claimId = $"CLM-{Guid.NewGuid():N}";
        var record = new ClaimRecord(
            ClaimId: claimId,
            TaskId: taskId,
            RunId: runId,
            StepId: stepId,
            ClaimText: claimText,
            Domain: domain,
            Property: property,
            Value: value,
            SourceType: sourceType,
            SourceId: sourceId,
            Level: level,
            Status: status,
            Confidence: confidence,
            WorkspaceVersion: workspaceVersion,
            CreatedAtUtc: DateTime.UtcNow);

        _claims[claimId] = record;
        _logger?.LogDebug("Claim recorded: {ClaimId} [{Level}] '{Text}'", claimId, level, claimText);
        return record;
    }

    /// <summary>
    /// Retrieves all claims, optionally filtered by task.
    /// </summary>
    public IReadOnlyList<ClaimRecord> GetClaims(string? taskId = null)
    {
        var list = _claims.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(taskId))
        {
            list = list.Where(c => string.Equals(c.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
        }
        return list.OrderBy(c => c.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// Retrieves all verified claims (Levels 0–3).
    /// </summary>
    public IReadOnlyList<ClaimRecord> GetVerifiedClaims(string? taskId = null)
    {
        return GetClaims(taskId).Where(c => c.Status == ClaimStatus.Verified).ToList();
    }

    /// <summary>
    /// Invalidates workspace-dependent claims when workspace version changes.
    /// </summary>
    public void InvalidateStaleClaims(int currentWorkspaceVersion, string? taskId = null)
    {
        foreach (var (k, claim) in _claims)
        {
            if (!string.IsNullOrEmpty(taskId) && !string.Equals(claim.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (claim.WorkspaceVersion < currentWorkspaceVersion &&
                (claim.Level == EvidenceLevel.Level3_VerifiedInference || claim.Domain == "filesystem" || claim.Domain == "build"))
            {
                var invalidated = claim with { Status = ClaimStatus.Rejected };
                _claims[k] = invalidated;
            }
        }
    }

    /// <summary>Resets the in-memory ledger for a fresh run.</summary>
    public void Reset(string? taskId = null)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            _claims.Clear();
        }
        else
        {
            foreach (var key in _claims.Where(kv => string.Equals(kv.Value.TaskId, taskId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            {
                _claims.TryRemove(key, out _);
            }
        }
    }
}
