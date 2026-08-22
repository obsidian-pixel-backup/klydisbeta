namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Extraction seam: converts raw bytes + detected content type into clean Markdown.
/// The current implementation is a deterministic HtmlAgilityPack-based extractor; the
/// interface exists so a full extraction pipeline (Readability-style scoring, documentation
/// extractor, JSON-path extraction) can replace it without touching the fetch layer.
/// </summary>
public interface IContentExtractor
{
    /// <summary>
    /// Extracts Markdown from a response body.
    /// </summary>
    /// <param name="body">Raw response bytes.</param>
    /// <param name="contentType">Detected content type (see <see cref="ContentTypeDetector"/>).</param>
    /// <param name="maxChars">Hard cap on the returned Markdown length.</param>
    /// <param name="truncated">True when <paramref name="maxChars"/> was exceeded.</param>
    /// <param name="title">Document title when one could be extracted.</param>
    ExtractResult Extract(byte[] body, string contentType, int maxChars);
}

/// <summary>Result of <see cref="IContentExtractor.Extract"/>.</summary>
public sealed record ExtractResult(string Markdown, string? Title, bool Truncated);
