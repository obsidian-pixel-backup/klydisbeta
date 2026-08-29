using System;
using System.Collections.Generic;
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

        raw = StripFrontMatter(raw);

        // Mask code first so tags inside examples survive untouched.
        var masked = new List<string>();
        string working = Mask(raw, FencedBlock, masked);
        working = Mask(working, InlineCode, masked);

        if (!ContainsStructuralHtml(working))
        {
            return Unmask(DemoteUndecodableImages(working), masked);
        }

        // Convert only the HTML regions. Handing the whole document to the converter would treat
        // Markdown line breaks as insignificant HTML whitespace and run lists, tables and fences
        // together onto a single line.
        string converted = ConvertHtmlRegions(working);

        converted = UnwrapEmphasisAroundLinks(converted);
        converted = DemoteUndecodableImages(converted);
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

    // A linked badge -- [![alt](img)](href) -- is the shape nearly every shields.io badge takes.
    private static readonly Regex LinkedImage = new(
        @"\[\s*!\[(?<alt>[^\]]*)\]\(\s*(?<img>[^)\s]+?)(?:\s+""[^""]*"")?\s*\)\s*\]\(\s*(?<href>[^)\s]+?)(?:\s+""[^""]*"")?\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex StandaloneImage = new(
        @"!\[(?<alt>[^\]]*)\]\(\s*(?<img>[^)\s]+?)(?:\s+""[^""]*"")?\s*\)",
        RegexOptions.Compiled);

    // What WPF's imaging stack can actually decode. SVG is the important omission: shields.io
    // serves SVG, so every badge on a card hit this.
    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico"
    };

    /// <summary>
    /// Replaces images the renderer cannot display with their alt text, keeping the surrounding
    /// link where there is one.
    /// </summary>
    /// <remarks>
    /// MdXaml renders an image it cannot decode as red error text reading "unsupported image
    /// format" next to the raw URL. Model cards open with rows of shields.io badges, which are
    /// SVG, so a typical card began with a block of red errors. Demoting them keeps the
    /// information (the badge label, and its link, which is what the badge was for) and drops
    /// only the picture that was never going to appear.
    /// </remarks>
    private static string DemoteUndecodableImages(string markdown)
    {
        string result = LinkedImage.Replace(markdown, m =>
        {
            if (IsDecodable(m.Groups["img"].Value)) return m.Value;
            string alt = m.Groups["alt"].Value.Trim();
            // No alt means nothing readable would survive, so the link goes with the image.
            return alt.Length == 0 ? string.Empty : $"[{alt}]({m.Groups["href"].Value})";
        });

        return StandaloneImage.Replace(result, m =>
        {
            if (IsDecodable(m.Groups["img"].Value)) return m.Value;
            string alt = m.Groups["alt"].Value.Trim();
            return alt.Length == 0 ? string.Empty : alt;
        });
    }

    private static bool IsDecodable(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = url.IndexOf(',');
            string header = comma > 0 ? url[..comma] : url;
            return header.Contains("image/png", StringComparison.OrdinalIgnoreCase)
                || header.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || header.Contains("image/gif", StringComparison.OrdinalIgnoreCase)
                || header.Contains("image/bmp", StringComparison.OrdinalIgnoreCase);
        }

        // A relative path has no base URI to resolve against here, so it cannot load either.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // Query and fragment are not part of the file name (…/x.png?raw=true).
        string path = uri.AbsolutePath;
        int dot = path.LastIndexOf('.');
        if (dot < 0) return false;   // extensionless endpoints (shields.io/badge/…) serve SVG
        return DecodableExtensions.Contains(path[dot..]);
    }

    private static readonly Regex YamlKey = new(@"^[A-Za-z_][\w.\-]*\s*:", RegexOptions.Compiled);

    /// <summary>
    /// Removes the YAML front matter block Hugging Face puts at the top of a model card.
    /// </summary>
    /// <remarks>
    /// The block is metadata for the Hub -- library_name, license, pipeline_tag, base_model --
    /// not documentation. Markdown has no syntax for it, so MdXaml rendered the delimiters as
    /// horizontal rules and the fields as a paragraph of "key: value" text, which is the first
    /// thing the reader met on every card.
    ///
    /// Deliberately conservative, because "---" is also a horizontal rule and a setext heading
    /// underline. The block only counts when the very first line is the delimiter, a closing
    /// delimiter follows, and something between them actually looks like a YAML key -- so a card
    /// that opens with a rule keeps it.
    /// </remarks>
    private static string StripFrontMatter(string raw)
    {
        string text = raw.TrimStart('\uFEFF');
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---") return raw;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line != "---" && line != "...") continue;

            // Require at least one key: value line, so a horizontal rule followed by prose and
            // another rule is not mistaken for metadata.
            bool looksLikeYaml = false;
            for (int j = 1; j < i; j++)
            {
                if (YamlKey.IsMatch(lines[j])) { looksLikeYaml = true; break; }
            }
            if (!looksLikeYaml) return raw;

            return string.Join("\n", lines[(i + 1)..]).TrimStart('\n');
        }

        // No closing delimiter: this is an ordinary rule, not front matter.
        return raw;
    }
}
