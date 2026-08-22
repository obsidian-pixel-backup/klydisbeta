using System.Text;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction.Extractors;

/// <summary>
/// Specialized extractor for news articles, blog posts, and long-form essays.
/// Focuses on headline, author bylines, publication timestamp, and core narrative body.
/// </summary>
public sealed class ArticleExtractor : IPageExtractor
{
    private readonly GenericExtractor _generic = new();

    public PageType SupportedType => PageType.Article;

    public ExtractedPage Extract(string url, string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html)) return _generic.Extract(url, html, maxChars);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var metadata = MetadataExtractor.Extract(doc, url);
        var links = LinkExtractor.Extract(doc, url);
        var tables = TableExtractor.Extract(doc);

        var articleNode = doc.DocumentNode.SelectSingleNode("//article")
            ?? doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'article-body') or contains(@class, 'post-content') or contains(@class, 'entry-content')]");

        if (articleNode == null)
        {
            return _generic.Extract(url, html, maxChars);
        }

        var bodyHtml = articleNode.OuterHtml;
        var subPage = _generic.Extract(url, bodyHtml, maxChars);
        var bodyMarkdown = subPage.Markdown;

        var byline = new StringBuilder();
        if (!string.IsNullOrEmpty(metadata.Author))
        {
            byline.Append($"**Author:** {metadata.Author} ");
        }
        if (metadata.PublishedAt.HasValue)
        {
            byline.Append($"| **Published:** {metadata.PublishedAt.Value:yyyy-MM-dd}");
        }

        var title = metadata.Title ?? doc.DocumentNode.SelectSingleNode("//h1")?.InnerText?.Trim();
        title = !string.IsNullOrEmpty(title) ? HtmlEntity.DeEntitize(title) : null;

        string finalMarkdown;
        if (bodyMarkdown.StartsWith("# "))
        {
            var firstLineEnd = bodyMarkdown.IndexOf('\n');
            if (firstLineEnd > 0 && byline.Length > 0)
            {
                finalMarkdown = bodyMarkdown[..firstLineEnd] + "\n\n" + byline.ToString().Trim() + "\n\n" + bodyMarkdown[(firstLineEnd + 1)..].TrimStart();
            }
            else
            {
                finalMarkdown = bodyMarkdown;
            }
        }
        else
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(title))
            {
                sb.AppendLine($"# {title}\n");
            }
            if (byline.Length > 0)
            {
                sb.AppendLine(byline.ToString().Trim() + "\n");
            }
            sb.AppendLine(bodyMarkdown);
            finalMarkdown = sb.ToString().Trim();
        }

        bool truncated = finalMarkdown.Length > maxChars;
        if (truncated)
        {
            finalMarkdown = finalMarkdown[..maxChars] + "\n\n… [truncated]";
        }

        var sections = SectionParser.ParseSections(finalMarkdown);

        return new ExtractedPage(
            finalMarkdown,
            title,
            sections,
            links,
            tables,
            metadata,
            truncated);
    }
}
