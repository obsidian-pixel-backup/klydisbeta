using System.Web;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Normalizes search result URLs by removing tracking parameters, normalizing schemes/hosts, and stripping fragments.
/// </summary>
public static class SearchNormalizer
{
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "fbclid", "gclid", "msclkid", "ref", "ref_src", "source", "feature", "ved", "ei"
    };

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var cleanQuery = HttpUtility.ParseQueryString(string.Empty);

        foreach (string key in query.Keys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (!TrackingParams.Contains(key))
            {
                cleanQuery[key] = query[key];
            }
        }

        var builder = new UriBuilder(uri)
        {
            Query = cleanQuery.ToString(),
            Fragment = string.Empty
        };

        var host = builder.Host.ToLowerInvariant();
        if (host.StartsWith("www."))
        {
            host = host[4..];
        }
        builder.Host = host;

        var result = builder.Uri.ToString();
        return result.TrimEnd('/');
    }
}
