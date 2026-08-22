using System.Text;
using HtmlAgilityPack;

namespace Klydis.Core.Web.Extraction;

/// <summary>
/// Deterministic content extraction: HTML → clean Markdown via HtmlAgilityPack (noise
/// elements removed, headings/list/code/table structure preserved), JSON/XML fenced for
/// passthrough, plain text kept as-is. No LLM involvement — this is the fast, reproducible
/// path; model summarization can layer on top later.
/// </summary>
public sealed class ContentExtractor : IContentExtractor
{
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
                (markdown, title) = ExtractHtml(text);
                break;

            default:
                // Unknown/unsupported content (PDF, binary, ...): keep a tiny prefix so the
                // caller can classify it, without leaking binary junk into context.
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

    private static (string Markdown, string? Title) ExtractHtml(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = null;

        // Remove noise nodes before walking the tree.
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
        return (Normalize(sb.ToString()), title);
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

        if (node.NodeType != HtmlNodeType.Element)
        {
            return;
        }

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
                    var cellText = cell.InnerText.Trim().Replace("|", "\\|");
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
            // Inline links: keep the anchor text only (compact; full link lists are P1).
            foreach (var child in node.ChildNodes) ConvertNode(child, sb, depth);
            return;
        }

        if (block)
        {
            sb.Append('\n');
        }

        foreach (var child in node.ChildNodes)
        {
            ConvertNode(child, sb, depth + 1);
        }

        if (block)
        {
            sb.Append('\n');
        }
    }

    private static string Normalize(string input)
    {
        // Collapse runs of blank lines, trim each line, and strip leading/trailing whitespace.
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
