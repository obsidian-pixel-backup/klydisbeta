using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Deterministic content extraction: HTML → structured <see cref="WebDocument"/> via classification
/// and specialized extractors (headings, sections, links, tables, metadata), JSON/XML fenced for
/// passthrough, plain text kept as-is.
/// </summary>
public sealed class ContentExtractor : IContentExtractor
{
    private readonly IPageClassifier _classifier;
    private readonly ExtractorRegistry _registry;

    public ContentExtractor(IPageClassifier? classifier = null, ExtractorRegistry? registry = null)
    {
        _classifier = classifier ?? new PageClassifier();
        _registry = registry ?? ExtractorRegistry.Default;
    }

    public ExtractResult Extract(byte[] body, string contentType, int maxChars)
    {
        if (body.Length == 0)
        {
            return new ExtractResult(string.Empty, null, false);
        }

        var text = Encoding.UTF8.GetString(body);
        string markdown;
        string? title = null;

        switch (contentType)
        {
            case ContentTypeDetector.Json:
                markdown = "```json\n" + text.Trim() + "\n```";
                break;

            case ContentTypeDetector.Xml:
                markdown = "```xml\n" + text.Trim() + "\n```";
                break;

            case ContentTypeDetector.Text:
                markdown = text.Trim();
                break;

            case ContentTypeDetector.Html:
                var classification = _classifier.Classify(string.Empty, text, contentType);
                var extractor = _registry.Resolve(classification.PageType);
                var extracted = extractor.Extract(string.Empty, text, maxChars);
                return new ExtractResult(extracted.Markdown, extracted.Title, extracted.Truncated);

            default:
                markdown = "[" + (contentType ?? "unknown") + " content — not extractable]";
                break;
        }

        bool truncated = markdown.Length > maxChars;
        if (truncated)
        {
            markdown = markdown[..maxChars] + "\n\n… [truncated]";
        }

        return new ExtractResult(markdown, title, truncated);
    }

    /// <summary>
    /// Extracts a full structured <see cref="WebDocument"/> including sections, links, tables, metadata, and page classification.
    /// </summary>
    public WebDocument ExtractDocument(
        byte[] body,
        string requestedUrl,
        string? finalUrl,
        string contentType,
        int? httpStatus,
        WebFetchMethod method,
        int maxChars,
        WebDiagnostics diagnostics)
    {
        var rawText = Encoding.UTF8.GetString(body);
        var targetUrl = finalUrl ?? requestedUrl;

        if (contentType == ContentTypeDetector.Html)
        {
            var classification = _classifier.Classify(targetUrl, rawText, contentType);
            var extractor = _registry.Resolve(classification.PageType);
            var extracted = extractor.Extract(targetUrl, rawText, maxChars);

            return new WebDocument(
                requestedUrl,
                finalUrl ?? requestedUrl,
                extracted.Title,
                extracted.Markdown,
                contentType,
                httpStatus,
                method,
                extracted.Truncated,
                DateTimeOffset.UtcNow,
                ComputeHash(extracted.Markdown),
                diagnostics,
                PageType: classification.PageType,
                Metadata: extracted.Metadata,
                Sections: extracted.Sections,
                Links: extracted.Links,
                Tables: extracted.Tables,
                RawHtml: rawText);
        }

        // Non-HTML Fallback
        var simple = Extract(body, contentType, maxChars);
        return new WebDocument(
            requestedUrl,
            finalUrl ?? requestedUrl,
            simple.Title,
            simple.Markdown,
            contentType,
            httpStatus,
            method,
            simple.Truncated,
            DateTimeOffset.UtcNow,
            ComputeHash(simple.Markdown),
            diagnostics,
            PageType: PageType.Generic,
            Metadata: new WebMetadata(Title: simple.Title, ContentType: contentType),
            RawHtml: rawText);
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
