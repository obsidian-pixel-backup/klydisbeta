using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Klydis.Core.Web.Models;

namespace Klydis.Core.Web.Extraction.Extractors;

/// <summary>
/// Baseline deterministic HTML DOM-to-Markdown extractor.
/// Purges noisy navigation/banner/footer elements and converts remaining structure cleanly.
/// </summary>
public sealed class GenericExtractor : IPageExtractor
{
    public PageType SupportedType => PageType.Generic;

    public ExtractedPage Extract(string url, string html, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new ExtractedPage(string.Empty, null, Array.Empty<WebSection>(), Array.Empty<WebLink>(), Array.Empty<WebTable>(), new WebMetadata(), false);
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var metadata = MetadataExtractor.Extract(doc, url);
        var links = LinkExtractor.Extract(doc, url);
        var tables = TableExtractor.Extract(doc);

        // Remove noise nodes
        var noise = doc.DocumentNode.Descendants()
            .Where(n => n.Name is "script" or "style" or "noscript" or "template"
                or "nav" or "footer" or "header" or "aside" or "iframe"
                or "form" or "svg" or "canvas" or "select" or "button" or "input")
            .ToList();
        foreach (var node in noise)
        {
            node.Remove();
        }

        var sb = new StringBuilder(html.Length / 4);
        ConvertNode(doc.DocumentNode, sb, 0);

        var rawMarkdown = Normalize(sb.ToString());
        bool truncated = rawMarkdown.Length > maxChars;
        var markdown = truncated ? rawMarkdown[..maxChars] + "\n\n… [truncated]" : rawMarkdown;

        var sections = SectionParser.ParseSections(markdown);

        return new ExtractedPage(
            markdown,
            metadata.Title,
            sections,
            links,
            tables,
            metadata,
            truncated);
    }

    private static void ConvertNode(HtmlNode node, StringBuilder sb, int depth)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text.Trim());
            }
            return;
        }

        if (node.NodeType == HtmlNodeType.Document)
        {
            foreach (var child in node.ChildNodes) ConvertNode(child, sb, depth);
            return;
        }

        if (node.NodeType != HtmlNodeType.Element) return;

        var name = node.Name.ToLowerInvariant();
        bool block = name is "p" or "div" or "section" or "article" or "main" or "ul" or "ol"
            or "table" or "tr" or "blockquote" or "pre" or "br" or "hr" or "li" or "h1" or "h2"
            or "h3" or "h4" or "h5" or "h6" or "dl" or "details" or "summary";

        if (name == "br" || name == "hr")
        {
            sb.Append('\n');
            return;
        }

        if (name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            sb.Append('\n').Append('#', int.Parse(name[1..])).Append(' ');
            foreach (var child in node.ChildNodes) ConvertNode(child, sb, depth);
            sb.Append('\n');
            return;
        }

        if (name == "li")
        {
            sb.Append("\n- ");
            foreach (var child in node.ChildNodes) ConvertNode(child, sb, depth);
            sb.Append('\n');
            return;
        }

        if (name == "pre")
        {
            var code = node.InnerText.Trim();
            if (code.Length > 0)
            {
                sb.Append("\n```\n").Append(code).Append("\n```\n");
            }
            return;
        }

        if (name == "code")
        {
            var code = node.InnerText.Trim();
            if (code.Length > 0)
            {
                sb.Append('`').Append(code).Append('`');
            }
            return;
        }

        if (name == "tr")
        {
            var cells = node.Descendants("td").Concat(node.Descendants("th")).ToList();
            if (cells.Count > 0)
            {
                sb.Append("\n| ");
                foreach (var cell in cells)
                {
                    var cellText = HtmlEntity.DeEntitize(cell.InnerText).Trim().Replace("|", "\\|");
                    sb.Append(cellText).Append(" | ");
                }
                sb.Append('\n');
            }
            return;
        }

        if (name == "img")
        {
            var alt = node.GetAttributeValue("alt", "");
            if (!string.IsNullOrWhiteSpace(alt))
            {
                sb.Append("[image: ").Append(alt.Trim()).Append(']');
            }
            return;
        }

        if (name == "a")
        {
            foreach (var child in node.ChildNodes) ConvertNode(child, sb, depth);
            return;
        }

        if (block) sb.Append('\n');
        foreach (var child in node.ChildNodes) ConvertNode(child, sb, depth + 1);
        if (block) sb.Append('\n');
    }

    private static string Normalize(string input)
    {
        var lines = input.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(input.Length);
        bool previousBlank = true;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (!previousBlank && sb.Length > 0) sb.Append('\n');
                previousBlank = true;
                continue;
            }
            sb.Append(line).Append('\n');
            previousBlank = false;
        }
        return sb.ToString().Trim();
    }
}
