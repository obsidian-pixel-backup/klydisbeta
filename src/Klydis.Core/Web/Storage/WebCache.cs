using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Klydis.Core.Web.Models;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Storage;

/// <summary>
/// Content-addressed web document cache with fine-grained freshness semantics and TTL policies.
/// Prevents expensive repeated HTTP and browser fetches for identical resources.
/// </summary>
public sealed class WebCache
{
    private sealed record CacheEntry(
        WebDocument Document,
        DateTimeOffset CachedAt,
        DateTimeOffset ExpiresAt,
        bool IsInvalidated);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public int Count => _cache.Count;

    public WebCache(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Looks up a cached document for <paramref name="urlOrId"/>, respecting optional max-age constraints.
    /// </summary>
    public (WebDocument? Document, FreshnessState Freshness) Get(string urlOrId, int? maxAgeSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(urlOrId)) return (null, FreshnessState.NotFound);

        var key = ComputeKey(urlOrId);
        if (!_cache.TryGetValue(key, out var entry) && !_cache.TryGetValue(urlOrId, out entry))
        {
            return (null, FreshnessState.NotFound);
        }

        if (entry.IsInvalidated)
        {
            return (entry.Document, FreshnessState.Invalidated);
        }

        var age = DateTimeOffset.UtcNow - entry.CachedAt;
        if (maxAgeSeconds.HasValue && age.TotalSeconds > maxAgeSeconds.Value)
        {
            return (entry.Document, FreshnessState.Stale);
        }

        if (DateTimeOffset.UtcNow > entry.ExpiresAt)
        {
            return (entry.Document, FreshnessState.Expired);
        }

        return (entry.Document, FreshnessState.Fresh);
    }

    /// <summary>
    /// Stores a document in the cache with a default or custom TTL.
    /// </summary>
    public void Put(WebDocument doc, TimeSpan? customTtl = null)
    {
        var key = ComputeKey(doc.RequestedUrl);
        var ttl = customTtl ?? GetDefaultTtl(doc.PageType);
        var now = DateTimeOffset.UtcNow;
        var entry = new CacheEntry(doc, now, now.Add(ttl), false);
        _cache[key] = entry;
        _cache[doc.Id] = entry;

        if (!string.IsNullOrEmpty(doc.FinalUrl) && !string.Equals(doc.FinalUrl, doc.RequestedUrl, StringComparison.OrdinalIgnoreCase))
        {
            _cache[ComputeKey(doc.FinalUrl)] = entry;
        }

        _logger?.LogDebug("Cached web document {Id} for URL {Url} (TTL: {Ttl})", doc.Id, doc.RequestedUrl, ttl);
    }

    /// <summary>
    /// Marks a cached entry as invalidated.
    /// </summary>
    public void Invalidate(string url)
    {
        var key = ComputeKey(url);
        if (_cache.TryGetValue(key, out var entry))
        {
            _cache[key] = entry with { IsInvalidated = true };
        }
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public static TimeSpan GetDefaultTtl(PageType pageType) => pageType switch
    {
        PageType.Documentation => TimeSpan.FromHours(24),
        PageType.Wikipedia => TimeSpan.FromHours(24),
        PageType.GitHub => TimeSpan.FromDays(7),
        PageType.Article or PageType.Blog => TimeSpan.FromMinutes(30),
        PageType.SearchResults => TimeSpan.FromMinutes(5),
        PageType.Product => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1)
    };

    public static string ComputeKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var normalized = NormalizeUrl(url);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty // strip anchors
        };
        return builder.Uri.ToString().TrimEnd('/');
    }
}
