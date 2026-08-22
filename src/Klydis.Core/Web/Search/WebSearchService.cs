using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Klydis.Core.Chat;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search;

/// <summary>
/// Multi-provider web search with provider fallback (Bing via stealth browser or HTTP, then
/// DuckDuckGo Lite, then Wikipedia OpenSearch). Every request URL — including the
/// env-configurable search endpoints — passes the SSRF guard before a single byte leaves the
/// machine, and results come back as structured <see cref="WebSearchResult"/> entries (IDs,
/// titles, URLs, snippets) instead of raw prose.
/// </summary>
public sealed class WebSearchService
{
    private readonly SsrfGuard _guard;
    private readonly StealthBrowserService? _browser;
    private readonly ILogger? _logger;
    private readonly HttpClient _client;

    public WebSearchService(SsrfGuard guard, StealthBrowserService? browser = null, ILogger? logger = null)
    {
        _guard = guard;
        _browser = browser;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<WebSearchOutcome> SearchAsync(WebSearchRequest request, CancellationToken ct)
    {
        var results = new List<WebSearchResult>();

        // Tier 1: Bing — stealth browser rendering, or plain HTTP when the browser is absent.
        results.AddRange(await SearchBingAsync(request.Query, request.MaxResults, ct).ConfigureAwait(false));

        // Tier 2: DuckDuckGo Lite.
        if (results.Count == 0)
        {
            results.AddRange(await SearchDuckDuckGoAsync(request.Query, request.MaxResults, ct).ConfigureAwait(false));
        }

        // Tier 3: Wikipedia OpenSearch.
        if (results.Count == 0)
        {
            results.AddRange(await SearchWikipediaAsync(request.Query, request.MaxResults, ct).ConfigureAwait(false));
        }

        if (results.Count == 0)
        {
            return new WebSearchOutcome([], new WebFailure(WebFailureCode.SearchProviderFailed, true, false,
                "All search providers failed or returned no results for the query.", Stage: "search"));
        }

        return new WebSearchOutcome(results, null);
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchBingAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("BING_SEARCH_ENDPOINT") ?? "https://www.bing.com/search";
            var searchUrl = $"{endpoint}?q={Uri.EscapeDataString(query)}";
            var failure = await _guard.ValidateAsync(searchUrl, ct).ConfigureAwait(false);
            if (failure != null)
            {
                _logger?.LogWarning("Bing search URL blocked by policy: {Message}", failure.Message);
                return [];
            }

            string? html = null;
            if (_browser != null)
            {
                html = await _browser.RenderPageHtmlAsync(searchUrl, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(html))
            {
                html = await GetStringSafelyAsync(searchUrl, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(html)) return [];

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<WebSearchResult>();
            var algoNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'b_algo')]");
            if (algoNodes == null) return results;

            var rank = 1;
            foreach (var node in algoNodes.Take(maxResults))
            {
                var titleNode = node.SelectSingleNode(".//h2/a") ?? node.SelectSingleNode(".//a");
                var title = titleNode != null ? HtmlEntity.DeEntitize(titleNode.InnerText).Trim() : "No Title";
                var rawLink = titleNode?.GetAttributeValue("href", "") ?? "";
                var link = UnwrapBingUrl(rawLink);

                var snippetNode = node.SelectSingleNode(".//p")
                    ?? node.SelectSingleNode(".//div[contains(@class, 'b_caption')]/p")
                    ?? node.SelectSingleNode(".//span[contains(@class, 'b_snippet')]")
                    ?? node.SelectSingleNode(".//span");
                var snippet = snippetNode != null ? HtmlEntity.DeEntitize(snippetNode.InnerText).Trim() : "No Snippet";
                snippet = Regex.Replace(snippet, @"\s+", " ");

                results.Add(new WebSearchResult(
                    $"search-{rank}",
                    title,
                    link,
                    snippet,
                    TryGetDomain(link),
                    rank));
                rank++;
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Bing search failed.");
            return [];
        }
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchDuckDuckGoAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("DUCKDUCKGO_SEARCH_ENDPOINT") ?? "https://lite.duckduckgo.com/lite/";
            var failure = await _guard.ValidateAsync(endpoint, ct).ConfigureAwait(false);
            if (failure != null)
            {
                _logger?.LogWarning("DuckDuckGo search URL blocked by policy: {Message}", failure.Message);
                return [];
            }

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", query) });
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            req.Headers.TryAddWithoutValidation("User-Agent", HttpFetcherUserAgent);
            using var response = await _client.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var resultNodes = doc.DocumentNode.SelectNodes("//td[contains(@class, 'result-snippet')]");
            var linkNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'result-link')]");
            if (linkNodes == null) return [];

            var results = new List<WebSearchResult>();
            int count = Math.Min(linkNodes.Count, maxResults);
            for (int i = 0; i < count; i++)
            {
                var title = HtmlEntity.DeEntitize(linkNodes[i].InnerText).Trim();
                var link = linkNodes[i].GetAttributeValue("href", "");
                var snippet = resultNodes != null && i < resultNodes.Count
                    ? HtmlEntity.DeEntitize(resultNodes[i].InnerText).Trim()
                    : "No Snippet";
                results.Add(new WebSearchResult($"search-{i + 1}", title, link, snippet, TryGetDomain(link), i + 1));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DuckDuckGo Lite search failed.");
            return [];
        }
    }

    private async Task<IReadOnlyList<WebSearchResult>> SearchWikipediaAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("WIKIPEDIA_API_ENDPOINT") ?? "https://en.wikipedia.org/w/api.php";
            var wikiUrl = $"{endpoint}?action=opensearch&search={Uri.EscapeDataString(query)}&limit={maxResults}&namespace=0&format=json";
            var failure = await _guard.ValidateAsync(wikiUrl, ct).ConfigureAwait(false);
            if (failure != null) return [];

            using var req = new HttpRequestMessage(HttpMethod.Get, wikiUrl);
            req.Headers.Add("User-Agent", "KlydisAssistant/1.0 (contact: info@klydis.local)");
            using var response = await _client.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 4) return [];

            var titles = root[1];
            var descriptions = root[2];
            var urls = root[3];
            int count = Math.Min(titles.GetArrayLength(), maxResults);

            var results = new List<WebSearchResult>();
            for (int i = 0; i < count; i++)
            {
                var title = titles[i].GetString() ?? "No Title";
                var url = urls[i].GetString() ?? "";
                var snippet = descriptions[i].GetString() ?? "";
                results.Add(new WebSearchResult($"search-{i + 1}", title, url, snippet, TryGetDomain(url), i + 1));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Wikipedia search failed.");
            return [];
        }
    }

    private async Task<string?> GetStringSafelyAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", HttpFetcherUserAgent);
        using var response = await _client.SendAsync(req, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
    }

    /// <summary>Decodes Bing's /ck/a redirect URLs back to the real destination.</summary>
    private static string UnwrapBingUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.Contains("bing.com/ck/a") && url.Contains("u=a1"))
        {
            var match = Regex.Match(url, @"u=a1([a-zA-Z0-9_\-]+)");
            if (match.Success)
            {
                try
                {
                    string b64 = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
                    switch (b64.Length % 4)
                    {
                        case 2: b64 += "=="; break;
                        case 3: b64 += "="; break;
                    }
                    var bytes = Convert.FromBase64String(b64);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    // Fall through and return the raw URL.
                }
            }
        }
        return url;
    }

    private static string TryGetDomain(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }

    private const string HttpFetcherUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
}
