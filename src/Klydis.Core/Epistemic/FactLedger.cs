using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Capabilities;
using Klydis.Core.Memory;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Epistemic;

/// <summary>
/// Epistemic Fact Ledger storing verified assertions about physical machine state with TTL expiration.
/// </summary>
public sealed class FactLedger
{
    private readonly MessageStore _messageStore;
    private readonly ILogger<FactLedger>? _logger;
    private readonly ConcurrentDictionary<string, EpistemicFact> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FactLedger(MessageStore messageStore, ILogger<FactLedger>? logger = null)
    {
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _logger = logger;
    }

    private static string BuildKey(string domain, string entityKey, string propertyName) =>
        $"{domain.ToLowerInvariant()}:{entityKey.ToLowerInvariant()}:{propertyName.ToLowerInvariant()}";

    /// <summary>
    /// Records a new or updated verified fact in the ledger and SQLite database.
    /// </summary>
    public async Task AssertFactAsync(FactAssertion assertion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(assertion.Ttl);
        string factId = BuildKey(assertion.Domain, assertion.EntityKey, assertion.PropertyName);
        string valueJson = assertion.Value is string strVal
            ? JsonSerializer.Serialize(strVal)
            : JsonSerializer.Serialize(assertion.Value);

        var fact = new EpistemicFact(
            FactId: factId,
            Domain: assertion.Domain,
            EntityKey: assertion.EntityKey,
            PropertyName: assertion.PropertyName,
            ValueJson: valueJson,
            SourceCapability: assertion.SourceCapability,
            Confidence: assertion.Confidence,
            CreatedAtUtc: now,
            ExpiresAtUtc: expiresAt,
            IsInvalidated: false
        );

        _cache[factId] = fact;

        var row = new FactLedgerRow(
            FactId: fact.FactId,
            Domain: fact.Domain,
            EntityKey: fact.EntityKey,
            PropertyName: fact.PropertyName,
            ValueJson: fact.ValueJson,
            SourceCapability: fact.SourceCapability,
            Confidence: fact.Confidence,
            CreatedAtUtc: fact.CreatedAtUtc,
            ExpiresAtUtc: fact.ExpiresAtUtc,
            IsInvalidated: false,
            InvalidationReason: null
        );

        await _messageStore.UpsertFactAsync(row);
        _logger?.LogDebug("Fact asserted: {FactId} (Expires: {ExpiresIn}s)", factId, assertion.Ttl.TotalSeconds);
    }

    /// <summary>
    /// Retrieves a typed fact value if active and not expired.
    /// </summary>
    public async Task<T?> GetFactAsync<T>(string domain, string entityKey, string propertyName, CancellationToken ct = default)
    {
        var entry = await GetFactEntryAsync(domain, entityKey, propertyName, ct);
        if (entry is null || entry.IsExpired())
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(entry.ValueJson);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to deserialize fact {Domain}:{EntityKey}:{PropertyName} to {Type}", domain, entityKey, propertyName, typeof(T).Name);
            return default;
        }
    }

    /// <summary>
    /// Gets the raw epistemic fact entry, falling back to SQLite if not in cache.
    /// </summary>
    public async Task<EpistemicFact?> GetFactEntryAsync(string domain, string entityKey, string propertyName, CancellationToken ct = default)
    {
        string factId = BuildKey(domain, entityKey, propertyName);
        if (_cache.TryGetValue(factId, out var cached))
        {
            if (!cached.IsExpired())
            {
                return cached;
            }
            _cache.TryRemove(factId, out _);
        }

        var row = await _messageStore.GetFactAsync(domain, entityKey, propertyName);
        if (row is null) return null;

        var fact = new EpistemicFact(
            row.FactId,
            row.Domain,
            row.EntityKey,
            row.PropertyName,
            row.ValueJson,
            row.SourceCapability,
            row.Confidence,
            row.CreatedAtUtc,
            row.ExpiresAtUtc,
            row.IsInvalidated,
            row.InvalidationReason
        );

        if (!fact.IsExpired())
        {
            _cache[factId] = fact;
            return fact;
        }

        return null;
    }

    /// <summary>
    /// Invalidates facts matching domain and optional entity key.
    /// </summary>
    public async Task InvalidateAsync(string domain, string? entityKey = null, string? reason = null, CancellationToken ct = default)
    {
        foreach (var kvp in _cache.ToList())
        {
            if (kvp.Value.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(entityKey) || kvp.Value.EntityKey.Equals(entityKey, StringComparison.OrdinalIgnoreCase))
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }
        }

        await _messageStore.InvalidateFactsAsync(domain, entityKey, reason);
        _logger?.LogDebug("Invalidated facts for domain '{Domain}' (Entity: {EntityKey})", domain, entityKey ?? "*");
    }

    /// <summary>
    /// Queries all active unexpired facts within a domain.
    /// </summary>
    public async Task<IReadOnlyList<EpistemicFact>> QueryDomainFactsAsync(string domain, CancellationToken ct = default)
    {
        var rows = await _messageStore.GetActiveDomainFactsAsync(domain);
        var list = new List<EpistemicFact>();
        foreach (var r in rows)
        {
            var f = new EpistemicFact(
                r.FactId, r.Domain, r.EntityKey, r.PropertyName, r.ValueJson,
                r.SourceCapability, r.Confidence, r.CreatedAtUtc, r.ExpiresAtUtc,
                r.IsInvalidated, r.InvalidationReason);

            if (!f.IsExpired())
            {
                list.Add(f);
                _cache[f.FactId] = f;
            }
        }
        return list;
    }

    /// <summary>
    /// Retrieves all active unexpired facts across the entire system.
    /// </summary>
    public async Task<IReadOnlyList<EpistemicFact>> GetAllActiveFactsAsync(CancellationToken ct = default)
    {
        var rows = await _messageStore.GetAllActiveFactsAsync();
        var list = new List<EpistemicFact>();
        foreach (var r in rows)
        {
            var f = new EpistemicFact(
                r.FactId, r.Domain, r.EntityKey, r.PropertyName, r.ValueJson,
                r.SourceCapability, r.Confidence, r.CreatedAtUtc, r.ExpiresAtUtc,
                r.IsInvalidated, r.InvalidationReason);

            if (!f.IsExpired())
            {
                list.Add(f);
            }
        }
        return list;
    }
}
