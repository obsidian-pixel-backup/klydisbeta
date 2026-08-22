using System.Text;
using System.Text.RegularExpressions;
using Klydis.Core.Web.Models;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Tools;

/// <summary>
/// Service executing fine-grained semantic web tools (search, crawl, find on page, get section, get links, get table).
/// Decouples web execution logic from <c>ToolExecutor</c>.
/// </summary>
public sealed class WebToolService
{
    private readonly WebOrchestrator _orchestrator;
    private readonly ILogger? _logger;

    public WebOrchestrator Orchestrator => _orchestrator;

    public WebToolService(WebOrchestrator orchestrator, ILogger? logger = null)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>Searches the web and formats structured search results.</summary>
    public async Task<(bool Success, string Output, string? FailureMessage)> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        try
        {
            var outcome = await _orchestrator.SearchAsync(new WebSearchRequest(query, maxResults), ct).ConfigureAwait(false);
            var output = _orchestrator.FormatSearchOutcome(outcome);
            return (outcome.Results.Count > 0, output, outcome.Failure?.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Web search failed for query: {Query}", query);
            return (false, string.Empty, $"Web search failed: {ex.Message}");
        }
    }

    /// <summary>Crawls a URL and formats a compact model projection.</summary>
    public async Task<(bool Success, string Output, string? FailureMessage, WebDocument? Document)> CrawlAsync(
        string url,
        int maxChars = 20000,
        bool allowBrowserFallback = true,
        CancellationToken ct = default)
    {
        try
        {
            var outcome = await _orchestrator.OpenAsync(
                new WebFetchRequest(url, MaxChars: maxChars, AllowBrowserFallback: allowBrowserFallback), ct).ConfigureAwait(false);

            var output = _orchestrator.FormatFetchOutcome(outcome);
            return (outcome.IsSuccess, output, outcome.Failure?.Message, outcome.Document);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Crawl URL failed for: {Url}", url);
            return (false, string.Empty, $"Crawl URL failed: {ex.Message}", null);
        }
    }

    /// <summary>Finds matching lines and context within a cached or fetched web document.</summary>
    public async Task<(bool Success, string Output)> FindOnPageAsync(
        string documentIdOrUrl,
        string pattern,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return (false, "Search pattern cannot be empty.");
        }

        var doc = await ResolveDocumentAsync(documentIdOrUrl, ct).ConfigureAwait(false);
        if (doc == null)
        {
            return (false, $"Document '{documentIdOrUrl}' was not found in cache and could not be fetched.");
        }

        var lines = doc.ContentMarkdown.Replace("\r\n", "\n").Split('\n');
        var matches = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                var contextStart = Math.Max(0, i - 1);
                var contextEnd = Math.Min(lines.Length - 1, i + 1);

                var snippet = new StringBuilder();
                snippet.AppendLine($"Line {i + 1}:");
                for (int j = contextStart; j <= contextEnd; j++)
                {
                    var prefix = (j == i) ? "> " : "  ";
                    snippet.AppendLine($"{prefix}{lines[j]}");
                }
                matches.Add(snippet.ToString().TrimEnd());

                if (matches.Count >= 10)
                {
                    matches.Add("... [TRUNCATED 10+ MATCHES]");
                    break;
                }
            }
        }

        if (matches.Count == 0)
        {
            return (true, $"No matches found for pattern '{pattern}' in document '{doc.Title ?? doc.RequestedUrl}'.");
        }

        var result = $"FIND_ON_PAGE id={doc.Id} url={doc.RequestedUrl} matches={matches.Count}\n\n" + string.Join("\n\n---\n\n", matches);
        return (true, result);
    }

    /// <summary>Retrieves a specific section of a document by heading name.</summary>
    public async Task<(bool Success, string Output)> GetSectionAsync(
        string documentIdOrUrl,
        string heading,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return (false, "Heading cannot be empty.");
        }

        var doc = await ResolveDocumentAsync(documentIdOrUrl, ct).ConfigureAwait(false);
        if (doc == null)
        {
            return (false, $"Document '{documentIdOrUrl}' was not found.");
        }

        var match = doc.Sections.FirstOrDefault(s => s.Heading.Contains(heading, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            var available = string.Join(", ", doc.Sections.Select(s => $"\"{s.Heading}\""));
            return (false, $"Section matching '{heading}' not found. Available sections: {available}");
        }

        var result = $"SECTION id={doc.Id} heading=\"{match.Heading}\" level={match.Level}\n\n" + match.ContentMarkdown;
        return (true, result);
    }

    /// <summary>Retrieves structured links from a web document with optional query filter.</summary>
    public async Task<(bool Success, string Output)> GetLinksAsync(
        string documentIdOrUrl,
        int limit = 25,
        string? filter = null,
        CancellationToken ct = default)
    {
        var doc = await ResolveDocumentAsync(documentIdOrUrl, ct).ConfigureAwait(false);
        if (doc == null)
        {
            return (false, $"Document '{documentIdOrUrl}' was not found.");
        }

        var links = doc.Links;
        if (!string.IsNullOrEmpty(filter))
        {
            links = links.Where(l => l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                     l.Url.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var selected = links.Take(limit).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"WEB_LINKS id={doc.Id} url={doc.RequestedUrl} total={selected.Count}");
        sb.AppendLine();

        int rank = 1;
        foreach (var link in selected)
        {
            var ext = link.IsExternal ? " [external]" : "";
            sb.AppendLine($"{rank}. [{link.Text}]({link.Url}){ext}");
            rank++;
        }

        return (true, sb.ToString().TrimEnd());
    }

    /// <summary>Retrieves a structured table from a web document by index.</summary>
    public async Task<(bool Success, string Output)> GetTableAsync(
        string documentIdOrUrl,
        int tableIndex = 0,
        CancellationToken ct = default)
    {
        var doc = await ResolveDocumentAsync(documentIdOrUrl, ct).ConfigureAwait(false);
        if (doc == null)
        {
            return (false, $"Document '{documentIdOrUrl}' was not found.");
        }

        if (tableIndex < 0 || tableIndex >= doc.Tables.Count)
        {
            return (false, $"Table index {tableIndex} out of range (document has {doc.Tables.Count} table(s)).");
        }

        var table = doc.Tables[tableIndex];
        var markdown = Extraction.TableExtractor.FormatAsMarkdown(table);
        var result = $"WEB_TABLE id={doc.Id} index={tableIndex}\n\n" + markdown;
        return (true, result);
    }

    /// <summary>Retrieves structured metadata, OpenGraph, and JSON-LD from a web document.</summary>
    public async Task<(bool Success, string Output)> GetMetadataAsync(
        string documentIdOrUrl,
        CancellationToken ct = default)
    {
        var doc = await ResolveDocumentAsync(documentIdOrUrl, ct).ConfigureAwait(false);
        if (doc == null)
        {
            return (false, $"Document '{documentIdOrUrl}' was not found.");
        }

        var meta = doc.Metadata;
        var sb = new StringBuilder();
        sb.AppendLine($"WEB_METADATA id={doc.Id} url={doc.RequestedUrl}");
        sb.AppendLine($"title: {meta.Title ?? "(none)"}");
        sb.AppendLine($"description: {meta.Description ?? "(none)"}");
        sb.AppendLine($"author: {meta.Author ?? "(none)"}");
        sb.AppendLine($"published: {meta.PublishedAt?.ToString("O") ?? "(none)"}");
        sb.AppendLine($"site_name: {meta.SiteName ?? "(none)"}");
        sb.AppendLine($"canonical_url: {meta.CanonicalUrl ?? "(none)"}");

        if (meta.OpenGraph != null && meta.OpenGraph.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("OPEN_GRAPH:");
            foreach (var kvp in meta.OpenGraph)
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        if (!string.IsNullOrEmpty(meta.JsonLd))
        {
            sb.AppendLine();
            sb.AppendLine("JSON_LD:");
            sb.AppendLine(meta.JsonLd);
        }

        return (true, sb.ToString().TrimEnd());
    }

    private async Task<WebDocument?> ResolveDocumentAsync(string documentIdOrUrl, CancellationToken ct)
    {
        // 1. Check cache by URL
        var (cached, _) = _orchestrator.Cache.Get(documentIdOrUrl);
        if (cached != null) return cached;

        // 2. If it's a URL, fetch it
        if (Uri.TryCreate(documentIdOrUrl, UriKind.Absolute, out _))
        {
            var outcome = await _orchestrator.OpenAsync(new WebFetchRequest(documentIdOrUrl), ct).ConfigureAwait(false);
            if (outcome.IsSuccess) return outcome.Document;
        }

        return null;
    }
}
