using Klydis.Core.Web.Models;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Orchestrates multi-provider search execution, provider failover, result normalization,
/// deduplication, and quality ranking.
/// </summary>
public sealed class SearchOrchestrator
{
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly ProviderHealthRegistry _healthRegistry;
    private readonly ILogger? _logger;

    public SearchOrchestrator(
        IEnumerable<ISearchProvider> providers,
        ProviderHealthRegistry? healthRegistry = null,
        ILogger? logger = null)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
        _healthRegistry = healthRegistry ?? new ProviderHealthRegistry(logger);
        _logger = logger;
    }

    public async Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, CancellationToken ct)
    {
        var rawResults = new List<WebSearchResult>();

        foreach (var provider in _providers)
        {
            if (!_healthRegistry.IsHealthy(provider.Name))
            {
                _logger?.LogInformation("Skipping unhealthy search provider {Provider}", provider.Name);
                continue;
            }

            try
            {
                var hits = await provider.SearchAsync(request.Query, request.MaxResults, ct).ConfigureAwait(false);
                if (hits != null && hits.Count > 0)
                {
                    rawResults.AddRange(hits);
                    // If primary provider returned sufficient results, stop early
                    if (rawResults.Count >= request.MaxResults)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Provider {Provider} search threw exception", provider.Name);
            }
        }

        if (rawResults.Count == 0)
        {
            return new WebSearchOutcome(
                Array.Empty<WebSearchResult>(),
                new WebFailure(WebFailureCode.SearchProviderFailed, true, false,
                    "All search providers failed or returned no results for the query.", Stage: "search"));
        }

        // Deduplicate across providers
        var deduplicated = SearchDeduplicator.Deduplicate(rawResults);

        // Rank by relevance and domain authority
        var ranked = SearchRanker.Rank(request.Query, deduplicated, request.MaxResults);

        return new WebSearchOutcome(ranked, null);
    }
}
