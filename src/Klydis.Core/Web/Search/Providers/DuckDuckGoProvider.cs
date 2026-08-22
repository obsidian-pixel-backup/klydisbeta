using System.Diagnostics;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search.Providers;

public sealed class DuckDuckGoProvider : ISearchProvider
{
    private readonly IWebSecurityPolicy _policy;
    private readonly HttpClient _client;
    private readonly ProviderHealthRegistry _healthRegistry;
    private readonly ILogger? _logger;

    public string Name => "DuckDuckGo";
    public int Priority => 2;

    public DuckDuckGoProvider(
        IWebSecurityPolicy policy,
        HttpClient client,
        ProviderHealthRegistry healthRegistry,
        ILogger? logger = null)
    {
        _policy = policy;
        _client = client;
        _healthRegistry = healthRegistry;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("DUCKDUCKGO_SEARCH_ENDPOINT") ?? "https://lite.duckduckgo.com/lite/";
            var failure = await _policy.ValidateAsync(endpoint, ct).ConfigureAwait(false);
            if (failure != null)
            {
                _logger?.LogWarning("DuckDuckGo search URL blocked by policy: {Message}", failure.Message);
                return [];
            }

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query) });
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

            using var response = await _client.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _healthRegistry.RecordFailure(Name, (int)response.StatusCode);
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var resultNodes = doc.DocumentNode.SelectNodes("//td[contains(@class, 'result-snippet')]");
            var linkNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'result-link')]");
            if (linkNodes == null)
            {
                _healthRegistry.RecordSuccess(Name, sw.ElapsedMilliseconds);
                return [];
            }

            var results = new List<WebSearchResult>();
            int count = Math.Min(linkNodes.Count, maxResults);
            for (int i = 0; i < count; i++)
            {
                var title = HtmlEntity.DeEntitize(linkNodes[i].InnerText).Trim();
                var link = linkNodes[i].GetAttributeValue("href", "");
                var snippet = resultNodes != null && i < resultNodes.Count
                    ? HtmlEntity.DeEntitize(resultNodes[i].InnerText).Trim()
                    : "No Snippet";

                if (!string.IsNullOrEmpty(link))
                {
                    results.Add(new WebSearchResult($"search-{i + 1}", title, link, snippet, TryGetDomain(link), i + 1));
                }
            }

            _healthRegistry.RecordSuccess(Name, sw.ElapsedMilliseconds);
            return results;
        }
        catch (Exception ex)
        {
            _healthRegistry.RecordFailure(Name);
            _logger?.LogWarning(ex, "DuckDuckGo search failed.");
            return [];
        }
    }

    private static string TryGetDomain(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }
}
