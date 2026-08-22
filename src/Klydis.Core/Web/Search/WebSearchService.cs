using System.Net;
using Klydis.Core.Chat;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Search.Providers;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Multi-provider web search service with health-aware failover, URL normalization, deduplication,
/// and ranking across Bing, DuckDuckGo, and Wikipedia.
/// Auto-redirect is strictly disabled to prevent SSRF bypasses via search redirection.
/// </summary>
public sealed class WebSearchService : IDisposable
{
    private readonly IWebSecurityPolicy _guard;
    private readonly StealthBrowserService? _browser;
    private readonly ILogger? _logger;
    private readonly HttpClient _client;
    private readonly SearchOrchestrator _orchestrator;

    public SearchOrchestrator Orchestrator => _orchestrator;

    public WebSearchService(
        IWebSecurityPolicy guard,
        StealthBrowserService? browser = null,
        ILogger? logger = null,
        ProviderHealthRegistry? healthRegistry = null)
    {
        _guard = guard;
        _browser = browser;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false, // Secure redirect handling: no automatic redirect bypass
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };

        var health = healthRegistry ?? new ProviderHealthRegistry(logger);
        var providers = new ISearchProvider[]
        {
            new BingSearchProvider(_guard, _client, health, _browser, logger),
            new DuckDuckGoProvider(_guard, _client, health, logger),
            new WikipediaProvider(_guard, _client, health, logger)
        };

        _orchestrator = new SearchOrchestrator(providers, health, logger);
    }

    public Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, CancellationToken ct) =>
        _orchestrator.SearchAsync(request, ct);

    public void Dispose()
    {
        _client.Dispose();
    }
}
