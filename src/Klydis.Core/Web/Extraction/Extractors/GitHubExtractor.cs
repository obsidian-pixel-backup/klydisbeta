using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction.Extractors;

/// <summary>
/// Specialized extractor for GitHub repository pages, READMEs, releases, and source listings.
/// </summary>
public sealed class GitHubExtractor : IPageExtractor
{
    private readonly GenericExtractor _generic = new();

    public PageType SupportedType => PageType.GitHub;

    public ExtractedPage Extract(string url, string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return _generic.Extract(url, html, maxChars);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var metadata = MetadataExtractor.Extract(doc, url);
        var links = LinkExtractor.Extract(doc, url);
        var tables = TableExtractor.Extract(doc);

        var readmeNode = doc.DocumentNode.SelectSingleNode("//article[contains(@class, 'markdown-body')]")
            ?? doc.DocumentNode.SelectSingleNode("//div[contains(@id, 'readme')]")
            ?? doc.DocumentNode.SelectSingleNode("//main");

        if (readmeNode != null)
        {
            var subPage = _generic.Extract(url, readmeNode.OuterHtml, maxChars);
            return new ExtractedPage(
                subPage.Markdown,
                metadata.Title,
                subPage.Sections,
                links,
                tables,
                metadata,
                subPage.Truncated);
        }

        return _generic.Extract(url, html, maxChars);
    }
}
