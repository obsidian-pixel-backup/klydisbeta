using System.Text.Json;
using Klydis.Core.Web.Models;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Web.Storage;

/// <summary>
/// Persists durable multi-file crawl bundles to local disk under <c>.klydis/web/{year}/{month}/crawl-{id}/</c>.
/// Enables long-horizon agent inspection, provenance verification, and offline retrieval.
/// </summary>
public sealed class WebArtifactStore
{
    private readonly string _baseDirectory;
    private readonly ILogger? _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string BaseDirectory => _baseDirectory;

    public WebArtifactStore(string? baseDirectory = null, ILogger? logger = null)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _baseDirectory = baseDirectory ?? Path.Combine(userProfile, ".klydis", "web");
        _logger = logger;
    }

    /// <summary>
    /// Writes a complete crawl artifact bundle to disk and returns the artifact folder path.
    /// </summary>
    public async Task<string> StoreAsync(WebDocument doc, CancellationToken ct = default)
    {
        try
        {
            var now = doc.RetrievedAt;
            var folderName = $"crawl-{doc.Id}";
            var crawlDir = Path.Combine(_baseDirectory, now.Year.ToString("0000"), now.Month.ToString("00"), folderName);
            Directory.CreateDirectory(crawlDir);

            // 1. extracted.md
            var mdPath = Path.Combine(crawlDir, "extracted.md");
            await File.WriteAllTextAsync(mdPath, doc.ContentMarkdown, ct).ConfigureAwait(false);

            // 2. raw.html (if present)
            if (!string.IsNullOrEmpty(doc.RawHtml))
            {
                var htmlPath = Path.Combine(crawlDir, "raw.html");
                await File.WriteAllTextAsync(htmlPath, doc.RawHtml, ct).ConfigureAwait(false);
            }

            // 3. links.json
            if (doc.Links.Count > 0)
            {
                var linksPath = Path.Combine(crawlDir, "links.json");
                await File.WriteAllTextAsync(linksPath, JsonSerializer.Serialize(doc.Links, JsonOpts), ct).ConfigureAwait(false);
            }

            // 4. tables.json
            if (doc.Tables.Count > 0)
            {
                var tablesPath = Path.Combine(crawlDir, "tables.json");
                await File.WriteAllTextAsync(tablesPath, JsonSerializer.Serialize(doc.Tables, JsonOpts), ct).ConfigureAwait(false);
            }

            // 5. metadata.json
            var metaPath = Path.Combine(crawlDir, "metadata.json");
            await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(doc.Metadata, JsonOpts), ct).ConfigureAwait(false);

            // 6. manifest.json
            var manifest = new
            {
                id = doc.Id,
                requested_url = doc.RequestedUrl,
                final_url = doc.FinalUrl,
                title = doc.Title,
                page_type = doc.PageType.ToString(),
                fetch_method = doc.FetchMethod.ToString(),
                http_status = doc.HttpStatus,
                content_chars = doc.MeaningfulCharCount,
                content_hash = doc.ContentHash,
                retrieved_at = doc.RetrievedAt,
                truncated = doc.ContentWasTruncated,
                sections_count = doc.Sections.Count,
                links_count = doc.Links.Count,
                tables_count = doc.Tables.Count
            };
            var manifestPath = Path.Combine(crawlDir, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts), ct).ConfigureAwait(false);

            _logger?.LogDebug("Stored web artifact bundle at: {Path}", crawlDir);
            return crawlDir;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist web artifact bundle for document {Id}", doc.Id);
            return string.Empty;
        }
    }
}
