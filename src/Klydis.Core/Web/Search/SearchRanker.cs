using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Scores and ranks multi-provider search results based on query term relevance, domain authority, and snippet quality.
/// </summary>
public static class SearchRanker
{
    private static readonly HashSet<string> HighAuthorityDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "en.wikipedia.org", "wikipedia.org", "github.com", "learn.microsoft.com", "docs.microsoft.com",
        "developer.mozilla.org", "w3.org", "python.org", "rust-lang.org", "dotnet.microsoft.com",
        "stackoverflow.com", "arxiv.org"
    };

    public static IReadOnlyList<WebSearchResult> Rank(string query, IEnumerable<WebSearchResult> results, int maxResults)
    {
        var terms = query.ToLowerInvariant()
            .Split(new[] { ' ', '+', '-', ',', '.', ':' }, StringSplitOptions.RemoveEmptyEntries);

        var scored = results.Select(r => new
        {
            Result = r,
            Score = ComputeScore(terms, r)
        })
        .OrderByDescending(x => x.Score)
        .Take(maxResults)
        .ToList();

        var reindexed = new List<WebSearchResult>();
        int rank = 1;
        foreach (var item in scored)
        {
            var r = item.Result;
            reindexed.Add(new WebSearchResult(
                $"search-{rank}",
                r.Title,
                r.Url,
                r.Snippet,
                r.Domain,
                rank));
            rank++;
        }

        return reindexed;
    }

    private static double ComputeScore(string[] terms, WebSearchResult result)
    {
        double score = 10.0 / Math.Max(1, result.Rank); // position score

        var titleLower = result.Title.ToLowerInvariant();
        var snippetLower = result.Snippet.ToLowerInvariant();
        var domainLower = result.Domain.ToLowerInvariant();

        foreach (var term in terms)
        {
            if (titleLower.Contains(term)) score += 5.0;
            if (snippetLower.Contains(term)) score += 2.0;
            if (domainLower.Contains(term)) score += 3.0;
        }

        if (HighAuthorityDomains.Contains(result.Domain))
        {
            score += 4.0;
        }

        if (!string.IsNullOrEmpty(result.Snippet) && result.Snippet != "No Snippet" && result.Snippet.Length > 40)
        {
            score += 1.5;
        }

        return score;
    }
}
