using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Deduplicates search results across multiple providers based on normalized URLs and domain similarity.
/// </summary>
public static class SearchDeduplicator
{
    public static IReadOnlyList<WebSearchResult> Deduplicate(IEnumerable<WebSearchResult> results)
    {
        var deduplicated = new List<WebSearchResult>();
        var seenNormalizedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            var normalized = SearchNormalizer.NormalizeUrl(r.Url);
            if (string.IsNullOrEmpty(normalized)) continue;

            if (seenNormalizedUrls.Add(normalized))
            {
                deduplicated.Add(r);
            }
        }

        return deduplicated;
    }
}
