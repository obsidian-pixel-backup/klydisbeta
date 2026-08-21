using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Tasks;

/// <summary>
/// The origin of a piece of information within the Klydis runtime.
/// Drives the epistemic authority hierarchy to prevent model hallucinations from
/// mutating verified runtime state.
/// </summary>
public enum EpistemicSource
{
    /// <summary>Directly supplied by the user (highest authority for requirements/preferences).</summary>
    UserFact = 0,

    /// <summary>Directly observed by runtime tools / system inspection (highest authority for environmental state).</summary>
    RuntimeTool = 1,

    /// <summary>Backed by verified execution evidence (build passing, tests passing, file verified on disk).</summary>
    VerifiedEvidence = 2,

    /// <summary>Derived by deterministic runtime state machines (step transitions, supervisor decisions).</summary>
    DerivedRuntimeState = 3,

    /// <summary>Explicit claim made by the model (unverified hypothesis or assertion).</summary>
    ModelClaim = 4,

    /// <summary>Implicit completion or assumption produced during model reasoning (lowest authority).</summary>
    ModelInference = 5
}

/// <summary>
/// The epistemic confidence and authority of a fact.
/// </summary>
public enum EpistemicAuthority
{
    /// <summary>Unknown or unverified. Environmental state without evidence evaluates strictly to Unknown.</summary>
    Unknown = 0,

    /// <summary>Untrusted model output or speculative reasoning.</summary>
    Untrusted = 1,

    /// <summary>Derived deterministically by the runtime.</summary>
    Derived = 2,

    /// <summary>Verified through execution proof.</summary>
    Verified = 3,

    /// <summary>Directly observed via system/tool execution.</summary>
    Observed = 4,

    /// <summary>Authoritative ground truth (user intent or immutable system property).</summary>
    Authoritative = 5
}

/// <summary>
/// The freshness of an epistemic fact relative to the active workspace.
/// </summary>
public enum EpistemicFreshness
{
    /// <summary>Valid against the current workspace version.</summary>
    Current,

    /// <summary>Invalidated by subsequent file mutations or runtime state changes.</summary>
    Stale,

    /// <summary>Freshness cannot be determined.</summary>
    Unknown
}

/// <summary>
/// An atomic epistemic fact or claim tracked by the runtime.
/// </summary>
public sealed record EpistemicEntry(
    string Key,
    string Value,
    EpistemicSource Source,
    EpistemicAuthority Authority,
    EpistemicFreshness Freshness,
    DateTime TimestampUtc,
    int WorkspaceVersion = 0,
    string? Subject = null,
    string? EvidenceProof = null)
{
    /// <summary>
    /// Checks whether this entry is authoritative and currently fresh.
    /// </summary>
    public bool IsAuthoritative => Authority >= EpistemicAuthority.Derived && Freshness == EpistemicFreshness.Current;

    /// <summary>
    /// Returns true if this entry represents an unverified model claim.
    /// </summary>
    public bool IsModelClaim => Source is EpistemicSource.ModelClaim or EpistemicSource.ModelInference;
}

/// <summary>
/// Epistemic Ledger: Manages authoritative environmental facts, user facts, and unverified model claims.
/// Enforces the rule: Model inference can NEVER overwrite higher-authority facts.
/// </summary>
public sealed class EpistemicLedger
{
    private readonly ConcurrentDictionary<string, EpistemicEntry> _facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<EpistemicEntry>> _claims = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records or updates an authoritative fact. If an existing fact has higher authority,
    /// a lower-authority write is rejected and logged.
    /// </summary>
    public bool RecordFact(EpistemicEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        // Model claims are tracked separately in the claim history and cannot overwrite facts
        if (entry.IsModelClaim)
        {
            RecordClaim(entry);
            return false;
        }

        return _facts.AddOrUpdate(
            entry.Key,
            entry,
            (key, existing) =>
            {
                // Strict authority check: higher authority wins. Equal authority takes newer timestamp.
                if ((int)entry.Authority > (int)existing.Authority)
                {
                    return entry;
                }
                if ((int)entry.Authority == (int)existing.Authority && entry.TimestampUtc >= existing.TimestampUtc)
                {
                    return entry;
                }
                return existing;
            }) == entry;
    }

    /// <summary>
    /// Records a model claim into the claim journal without upgrading it to an authoritative fact.
    /// </summary>
    public void RecordClaim(EpistemicEntry claim)
    {
        if (claim == null) throw new ArgumentNullException(nameof(claim));
        _claims.AddOrUpdate(
            claim.Key,
            _ => new List<EpistemicEntry> { claim },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(claim);
                    if (list.Count > 50) list.RemoveAt(0);
                }
                return list;
            });
    }

    /// <summary>
    /// Gets the current authoritative fact for the given key, or null if unknown or untrusted.
    /// </summary>
    public EpistemicEntry? GetFact(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_facts.TryGetValue(key, out var entry) && entry.Freshness == EpistemicFreshness.Current)
        {
            return entry;
        }
        return null;
    }

    /// <summary>
    /// Gets all currently tracked authoritative facts.
    /// </summary>
    public IReadOnlyList<EpistemicEntry> GetAllFacts()
        => _facts.Values.Where(f => f.Freshness == EpistemicFreshness.Current).ToList();

    /// <summary>
    /// Resolves the value for a key. If no authoritative fact exists, returns "UNKNOWN" instead of allowing speculation.
    /// </summary>
    public string ResolveValueOrUnknown(string key)
    {
        var fact = GetFact(key);
        return fact != null ? fact.Value : "UNKNOWN";
    }

    /// <summary>
    /// Invalidates all verification-dependent facts when a file mutation bumps the workspace version.
    /// </summary>
    public void InvalidateOnWorkspaceChange(int newWorkspaceVersion)
    {
        foreach (var kvp in _facts)
        {
            if (kvp.Value.Source == EpistemicSource.VerifiedEvidence && kvp.Value.WorkspaceVersion < newWorkspaceVersion)
            {
                var staled = kvp.Value with { Freshness = EpistemicFreshness.Stale };
                _facts.TryUpdate(kvp.Key, staled, kvp.Value);
            }
        }
    }

    /// <summary>
    /// Returns all current authoritative facts.
    /// </summary>
    public IReadOnlyList<EpistemicEntry> GetCurrentAuthoritativeFacts()
    {
        return _facts.Values
            .Where(f => f.IsAuthoritative)
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Formats authoritative state into a compact prompt section for context injection.
    /// </summary>
    public string FormatAuthoritativeContext()
    {
        var facts = GetCurrentAuthoritativeFacts();
        if (facts.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[AUTHORITATIVE STATE — VERIFIED FACTS]");
        sb.AppendLine("The following facts are verified by the runtime and user. Environmental facts not listed here are UNKNOWN.");
        foreach (var fact in facts)
        {
            sb.AppendLine($"  - {fact.Key}: {fact.Value} (Source: {fact.Source}, Authority: {fact.Authority})");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Resets the ledger for a fresh run.
    /// </summary>
    public void Reset()
    {
        _facts.Clear();
        _claims.Clear();
    }
}
