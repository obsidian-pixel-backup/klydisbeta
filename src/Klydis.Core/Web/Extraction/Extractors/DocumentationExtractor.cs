using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction.Extractors;

/// <summary>
/// Specialized extractor for technical documentation, API references, and developer guides.
/// Emphasizes code blocks, section hierarchy, parameter tables, and navigation context.
/// </summary>
public sealed class DocumentationExtractor : IPageExtractor
{
    private readonly GenericExtractor _generic = new();

    public PageType SupportedType => PageType.Documentation;

    public ExtractedPage Extract(string url, string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return _generic.Extract(url, html, maxChars);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var metadata = MetadataExtractor.Extract(doc, url);
        var links = LinkExtractor.Extract(doc, url);
        var tables = TableExtractor.Extract(doc);

        var contentNode = doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode.SelectSingleNode("//article")
            ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'content') or contains(@class, 'doc') or contains(@id, 'main-content')]")
            ?? doc.DocumentNode;

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
