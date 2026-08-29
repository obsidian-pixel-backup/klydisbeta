using System.Text;
using System.Text.RegularExpressions;

namespace Klydis.Core.Models;

/// <summary>
/// Hugging Face model cards are HTML-in-Markdown: authors wrap banners, badges and layout in
/// raw tags that GitHub-flavoured Markdown has no syntax for. MdXaml renders Markdown only, so
/// those tags reached the UI as literal text and its indentation heuristics promoted them to
/// code blocks — the model card showed a stack of grey boxes each holding one HTML tag.
///
/// This converts the HTML back to Markdown before rendering. Fenced and inline code are masked
/// first, so a README that legitimately *documents* HTML keeps its examples intact.
/// </summary>
public static class ModelCardFormatter
{
    // Fenced blocks (``` or ~~~), then inline spans. Order matters: fences may contain backticks.
    private static readonly Regex FencedBlock = new(@"^([ \t]*)(`{3,}|~{3,})[^\n]*\n.*?^\1?\2[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`[^`\n]+`", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"<\s*/?\s*([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex ExcessBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// Tags that mean the document really is HTML rather than Markdown containing a stray
    /// angle bracket. Comparisons like "a &lt; b" or generic types must not trigger conversion.
    /// </summary>
    private static readonly HashSet<string> StructuralTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "p", "img", "a", "br", "span", "center", "table", "thead", "tbody", "tr", "td", "th",
        "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "b", "em", "i", "picture",
        "source", "details", "summary", "figure", "figcaption", "blockquote", "hr", "font", "small"
    };

    /// <summary>Elements with no closing tag, so they must not affect nesting depth.</summary>
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "img", "br", "hr", "source"
    };

    /// <summary>
    /// Returns <paramref name="raw"/> with any HTML converted to Markdown. Input that contains no
    /// HTML is returned unchanged, so a well-formed Markdown card never passes through the
    /// converter and cannot be altered by it.
    /// </summary>
    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Mask code first so tags inside examples survive untouched.
        var masked = new List<string>();
        string working = Mask(raw, FencedBlock, masked);
        working = Mask(working, InlineCode, masked);

        if (!ContainsStructuralHtml(working))
        {
            return Unmask(working, masked);
        }

        // Convert only the HTML regions. Handing the whole document to the converter would treat
        // Markdown line breaks as insignificant HTML whitespace and run lists, tables and fences
        // together onto a single line.
        string converted = ConvertHtmlRegions(working);

        converted = UnwrapEmphasisAroundLinks(converted);
        converted = Unmask(converted, masked);
        return ExcessBlankLines.Replace(converted, "\n\n").Trim();
    }

    private static string ConvertHtmlRegions(string text)
    {
        var converter = BuildConverter();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        int i = 0;

        while (i < lines.Length)
        {
            if (!StartsHtmlBlock(lines[i]))
            {
                output.Append(lines[i]).Append('\n');
                i++;
                continue;
            }

            // Consume until every element opened here closes again OR a blank line arrives.
            // The blank-line stop is what CommonMark uses to end an HTML block, and it is what
            // keeps an unclosed or mismatched tag — common in hand-written model cards — from
            // swallowing the rest of the document and collapsing its headings, lists and fences
            // onto one line.
            int depth = 0;
            var block = new StringBuilder();
            do
            {
                depth += NetTagDepth(lines[i]);
                block.Append(lines[i]).Append('\n');
                i++;
            }
            while (i < lines.Length && depth > 0 && lines[i].Trim().Length > 0);

            string md;
            try { md = converter.Convert(block.ToString()); }
            catch { md = block.ToString(); }   // a malformed card must still render

            output.Append(md.Trim()).Append('\n');
        }

        return output.ToString();
    }

    private static readonly Regex BoldSpan = new(@"\*\*(?<inner>[^*\n]*\]\([^)\n]*\)[^*\n]*)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicSpan = new(@"(?<![*\w])\*(?<inner>[^*\n]*\]\([^)\n]*\)[^*\n]*)\*(?![*\w])", RegexOptions.Compiled);

    /// <summary>
    /// MdXaml's emphasis matcher does not span an inline link, so <c>**text [a](b) more**</c>
    /// renders with the asterisks printed literally. Dropping the emphasis reads better than
    /// showing stray punctuation; the link and its text are preserved either way.
    /// </summary>
    private static string UnwrapEmphasisAroundLinks(string markdown)
    {
        markdown = BoldSpan.Replace(markdown, m => m.Groups["inner"].Value);
        return ItalicSpan.Replace(markdown, m => m.Groups["inner"].Value);
    }

    private static ReverseMarkdown.Converter BuildConverter()
    {
#pragma warning disable CS0618
        var config = new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass
        };
#pragma warning restore CS0618
        return new ReverseMarkdown.Converter(config);
    }

    /// <summary>True when the line's first non-space content opens a structural HTML element.</summary>
    private static bool StartsHtmlBlock(string line)
    {
        string t = line.TrimStart();
        if (t.Length < 2 || t[0] != '<') return false;
        var m = HtmlTag.Match(t);
        return m.Success && m.Index == 0 && StructuralTags.Contains(m.Groups[1].Value);
    }

    /// <summary>Opening structural tags minus closing ones; void elements never change depth.</summary>
    private static int NetTagDepth(string line)
    {
        int depth = 0;
        foreach (Match m in HtmlTag.Matches(line))
        {
            string name = m.Groups[1].Value;
            if (!StructuralTags.Contains(name)) continue;
            if (VoidTags.Contains(name)) continue;
            if (m.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal)) continue;
            depth += m.Value.TrimStart().StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
        }
        return depth;
    }

    private static bool ContainsStructuralHtml(string text)
    {
        foreach (Match m in HtmlTag.Matches(text))
        {
            if (StructuralTags.Contains(m.Groups[1].Value)) return true;
        }
        return false;
    }

    private static string Mask(string text, Regex pattern, List<string> store)
    {
        return pattern.Replace(text, m =>
        {
            store.Add(m.Value);
            // A token the HTML parser cannot mistake for markup, and that survives a round trip.
            return $"KLYDISCODE{store.Count - 1}";
        });
    }

    private static string Unmask(string text, List<string> store)
    {
        if (store.Count == 0) return text;
        var sb = new StringBuilder(text);
        for (int i = 0; i < store.Count; i++)
        {
            // The converter may escape the delimiters or the digits' surroundings; match loosely.
            sb.Replace($"KLYDISCODE{i}", store[i]);
        }
        return sb.ToString();
    }
}
