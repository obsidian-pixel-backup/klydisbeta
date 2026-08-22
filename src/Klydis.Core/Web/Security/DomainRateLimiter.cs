using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Security;

/// <summary>
/// Controls concurrency and tracks domain-level rate limits and circuit breaker cooldowns.
/// Prevents hammering target domains with bursts that trigger 429s or IP bans.
/// </summary>
public sealed class DomainRateLimiter
{
    private sealed class DomainState
    {
        public SemaphoreSlim ConcurrencyLock { get; }
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }

        public DomainState(int maxConcurrent)
        {
            ConcurrencyLock = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }
    }

    private readonly ConcurrentDictionary<string, DomainState> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _defaultMaxConcurrentPerDomain;
    private readonly ILogger? _logger;

    public DomainRateLimiter(int defaultMaxConcurrentPerDomain = 2, ILogger? logger = null)
    {
        _defaultMaxConcurrentPerDomain = Math.Max(1, defaultMaxConcurrentPerDomain);
        _logger = logger;
    }

    public async Task<IDisposable?> AcquireAsync(string url, CancellationToken ct)
    {
        var host = GetHost(url);
        if (string.IsNullOrEmpty(host)) return new NoopReleaser();

        var state = _domains.GetOrAdd(host, _ => new DomainState(_defaultMaxConcurrentPerDomain));

        // Check if in cooldown
        if (state.CooldownUntil.HasValue)
        {
            if (DateTimeOffset.UtcNow < state.CooldownUntil.Value)
            {
                _logger?.LogWarning("Domain {Host} is in cooldown until {Cooldown}", host, state.CooldownUntil.Value);
                return null;
            }
            state.CooldownUntil = null;
        }

        await state.ConcurrencyLock.WaitAsync(ct).ConfigureAwait(false);
        return new DomainLease(state.ConcurrencyLock);
    }

    public void RecordOutcome(string url, int? httpStatus, bool isSuccess)
    {
        var host = GetHost(url);
        if (string.IsNullOrEmpty(host)) return;

        if (!_domains.TryGetValue(host, out var state)) return;

        if (isSuccess)
        {
            state.ConsecutiveFailures = 0;
            state.CooldownUntil = null;
            return;
        }

        if (httpStatus == 429)
        {
            state.ConsecutiveFailures++;
            var backoffSeconds = Math.Min(60, Math.Pow(2, Math.Min(state.ConsecutiveFailures, 6)));
            state.CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(backoffSeconds);
            _logger?.LogInformation("Domain {Host} triggered 429. Setting cooldown for {Seconds}s", host, backoffSeconds);
        }
        else if (httpStatus is 503 or 504)
        {
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= 3)
            {
                state.CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(15);
            }
        }
    }

    private static string GetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    private sealed class DomainLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public DomainLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            sem?.Release();
        }
    }

    private sealed class NoopReleaser : IDisposable
    {
        public void Dispose() { }
    }
}
