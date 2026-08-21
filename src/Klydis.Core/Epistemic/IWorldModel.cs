using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Capabilities;

namespace Klydis.Core.Epistemic;

/// <summary>
/// A persistent or cached epistemic fact with physical time-to-live (TTL).
/// </summary>
public sealed record EpistemicFact(
    string FactId,
    string Domain,
    string EntityKey,
    string PropertyName,
    string ValueJson,
    string SourceCapability,
    double Confidence,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    bool IsInvalidated = false,
    string? InvalidationReason = null
)
{
    public bool IsExpired(DateTime? nowUtc = null) =>
        (nowUtc ?? DateTime.UtcNow) > ExpiresAtUtc || IsInvalidated;
}

/// <summary>
/// World Model interface representing the agent's verified understanding of the machine state.
/// </summary>
public interface IWorldModel
{
    /// <summary>
    /// Retrieves a typed fact value if present and unexpired.
    /// </summary>
    Task<T?> GetFactAsync<T>(string domain, string entityKey, string propertyName, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the raw epistemic fact entry if present.
    /// </summary>
    Task<EpistemicFact?> GetFactEntryAsync(string domain, string entityKey, string propertyName, CancellationToken ct = default);

    /// <summary>
    /// Asserts or updates a factual claim in the world model.
    /// </summary>
    Task AssertFactAsync(FactAssertion assertion, CancellationToken ct = default);

    /// <summary>
    /// Invalidates facts in a domain or specific entity key (e.g. after a mutating action).
    /// </summary>
    Task InvalidateAsync(string domain, string? entityKey = null, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Queries active unexpired facts within a domain.
    /// </summary>
    Task<IReadOnlyList<EpistemicFact>> QueryDomainFactsAsync(string domain, CancellationToken ct = default);

    /// <summary>
    /// Exports a concise snapshot summary of active world facts for prompt injection.
    /// </summary>
    Task<string> SummarizeStateAsync(CancellationToken ct = default);
}
