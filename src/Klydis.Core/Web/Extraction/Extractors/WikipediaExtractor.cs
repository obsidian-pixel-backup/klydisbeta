using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction.Extractors;

/// <summary>
/// Specialized extractor for Wikipedia encyclopedic articles.
/// Purges edit links, navigation boxes, and citation brackets while preserving infoboxes and structured sections.
/// </summary>
public sealed class WikipediaExtractor : IPageExtractor
{
    private readonly GenericExtractor _generic = new();

    public PageType SupportedType => PageType.Wikipedia;

    public ExtractedPage Extract(string url, string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return _generic.Extract(url, html, maxChars);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var metadata = MetadataExtractor.Extract(doc, url);
        var links = LinkExtractor.Extract(doc, url);
        var tables = TableExtractor.Extract(doc);

        var contentNode = doc.DocumentNode.SelectSingleNode("//div[@id='mw-content-text']")
            ?? doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode;

        // Remove Wikipedia-specific chrome: .mw-editsection, .navbox, .vector-toc, .reference
        var wikiNoise = contentNode.Descendants()
            .Where(n => n.HasClass("mw-editsection") ||
                        n.HasClass("navbox") ||
                        n.HasClass("vector-toc") ||
                        n.HasClass("reflist") ||
                        n.HasClass("reference") ||
                        n.HasClass("noprint"))
            .ToList();

        foreach (var node in wikiNoise)
        {
            node.Remove();
        }

        var subPage = _generic.Extract(url, contentNode.OuterHtml, maxChars);
        return new ExtractedPage(
            subPage.Markdown,
            metadata.Title,
            subPage.Sections,
            links,
            tables,
            metadata,
            subPage.Truncated);
    }
}
