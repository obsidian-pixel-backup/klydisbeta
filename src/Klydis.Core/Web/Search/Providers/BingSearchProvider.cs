using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Klydis.Core.Chat;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search.Providers;

public sealed class BingSearchProvider : ISearchProvider
{
    private readonly IWebSecurityPolicy _policy;
    private readonly StealthBrowserService? _browser;
    private readonly HttpClient _client;
    private readonly ProviderHealthRegistry _healthRegistry;
    private readonly ILogger? _logger;

    public string Name => "Bing";
    public int Priority => 1;

    public BingSearchProvider(
        IWebSecurityPolicy policy,
        HttpClient client,
        ProviderHealthRegistry healthRegistry,
        StealthBrowserService? browser = null,
        ILogger? logger = null)
    {
        _policy = policy;
        _client = client;
        _healthRegistry = healthRegistry;
        _browser = browser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("BING_SEARCH_ENDPOINT") ?? "https://www.bing.com/search";
            var searchUrl = $"{endpoint}?q={Uri.EscapeDataString(query)}";

            var failure = await _policy.ValidateAsync(searchUrl, ct).ConfigureAwait(false);
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
                using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                using var response = await _client.SendAsync(req, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    _healthRegistry.RecordFailure(Name, (int)response.StatusCode);
                    return [];
                }
            }

            if (string.IsNullOrEmpty(html))
            {
                _healthRegistry.RecordFailure(Name);
                return [];
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<WebSearchResult>();
            var algoNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'b_algo')]");
            if (algoNodes == null)
            {
                _healthRegistry.RecordSuccess(Name, sw.ElapsedMilliseconds);
                return results;
            }

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

                if (!string.IsNullOrEmpty(link))
                {
                    results.Add(new WebSearchResult(
                        $"search-{rank}",
                        title,
                        link,
                        snippet,
                        TryGetDomain(link),
                        rank));
                    rank++;
                }
            }

            _healthRegistry.RecordSuccess(Name, sw.ElapsedMilliseconds);
            return results;
        }
        catch (Exception ex)
        {
            _healthRegistry.RecordFailure(Name);
            _logger?.LogWarning(ex, "Bing search failed.");
            return [];
        }
    }

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
                catch { }
            }
        }
        return url;
    }

    private static string TryGetDomain(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }
}
