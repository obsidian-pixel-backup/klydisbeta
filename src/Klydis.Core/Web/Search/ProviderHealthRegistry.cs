using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Tracks operational health, latency, and failure rates for search providers.
/// </summary>
public sealed class ProviderHealthRegistry
{
    private sealed class ProviderStats
    {
        public int TotalRequests;
        public int Successes;
        public int Failures;
        public int Throttles; // 429 / 403
        public long TotalLatencyMs;
        public DateTimeOffset? CooldownUntil;
    }

    private readonly ConcurrentDictionary<string, ProviderStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public ProviderHealthRegistry(ILogger? logger = null)
    {
        _logger = logger;
    }

    public void RecordSuccess(string providerName, long latencyMs)
    {
        var stat = _stats.GetOrAdd(providerName, _ => new ProviderStats());
        Interlocked.Increment(ref stat.TotalRequests);
        Interlocked.Increment(ref stat.Successes);
        Interlocked.Add(ref stat.TotalLatencyMs, latencyMs);
    }

    public void RecordFailure(string providerName, int? httpStatus = null)
    {
        var stat = _stats.GetOrAdd(providerName, _ => new ProviderStats());
        Interlocked.Increment(ref stat.TotalRequests);
        Interlocked.Increment(ref stat.Failures);

        if (httpStatus is 429 or 403)
        {
            Interlocked.Increment(ref stat.Throttles);
            stat.CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(2);
            _logger?.LogWarning("Search provider {Provider} throttled (HTTP {Status}). Cooling down for 2m.", providerName, httpStatus);
        }
    }

    public bool IsHealthy(string providerName)
    {
        if (!_stats.TryGetValue(providerName, out var stat)) return true;

        if (stat.CooldownUntil.HasValue)
        {
            if (DateTimeOffset.UtcNow < stat.CooldownUntil.Value) return false;
            stat.CooldownUntil = null;
        }

        if (stat.TotalRequests >= 5)
        {
            double successRate = (double)stat.Successes / stat.TotalRequests;
            if (successRate < 0.20) return false;
        }

        return true;
    }
}
