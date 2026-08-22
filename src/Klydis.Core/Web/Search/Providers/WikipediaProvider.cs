using System.Diagnostics;
using System.Text.Json;
using Klydis.Core.Web.Models;
using Klydis.Core.Web.Security;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Search.Providers;

public sealed class WikipediaProvider : ISearchProvider
{
    private readonly IWebSecurityPolicy _policy;
    private readonly HttpClient _client;
    private readonly ProviderHealthRegistry _healthRegistry;
    private readonly ILogger? _logger;

    public string Name => "Wikipedia";
    public int Priority => 3;

    public WikipediaProvider(
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
            var endpoint = Environment.GetEnvironmentVariable("WIKIPEDIA_API_ENDPOINT") ?? "https://en.wikipedia.org/w/api.php";
            var wikiUrl = $"{endpoint}?action=opensearch&search={Uri.EscapeDataString(query)}&limit={maxResults}&namespace=0&format=json";

            var failure = await _policy.ValidateAsync(wikiUrl, ct).ConfigureAwait(false);
            if (failure != null)
            {
                _logger?.LogWarning("Wikipedia search URL blocked by policy: {Message}", failure.Message);
                return [];
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, wikiUrl);
            req.Headers.Add("User-Agent", "KlydisAssistant/1.0 (contact: info@klydis.local)");

            using var response = await _client.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _healthRegistry.RecordFailure(Name, (int)response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 4)
            {
                _healthRegistry.RecordSuccess(Name, sw.ElapsedMilliseconds);
                return [];
            }

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

                if (!string.IsNullOrEmpty(url))
                {
                    results.Add(new WebSearchResult($"search-{i + 1}", title, url, snippet, TryGetDomain(url), i + 1));
                }
            }

            _healthRegistry.RecordSuccess(Name, sw.ElapsedMilliseconds);
            return results;
        }
        catch (Exception ex)
        {
            _healthRegistry.RecordFailure(Name);
            _logger?.LogWarning(ex, "Wikipedia search failed.");
            return [];
        }
    }

    private static string TryGetDomain(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    }
}
