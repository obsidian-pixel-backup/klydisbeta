using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Result of extracting a web page using a specialized extraction strategy.
/// </summary>
public sealed record ExtractedPage(
    string Markdown,
    string? Title,
    IReadOnlyList<WebSection> Sections,
    IReadOnlyList<WebLink> Links,
    IReadOnlyList<WebTable> Tables,
    WebMetadata Metadata,
    bool Truncated);

/// <summary>
/// Strategy interface for extracting structured content from classified web pages.
/// </summary>
public interface IPageExtractor
{
    PageType SupportedType { get; }
    ExtractedPage Extract(string url, string html, int maxChars);
}
