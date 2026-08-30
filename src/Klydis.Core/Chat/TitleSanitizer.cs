using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Klydis.Core.Chat;

/// <summary>
/// Utility for sanitizing, formatting, and deriving clean conversation titles.
/// </summary>
public static class TitleSanitizer
{
    private static readonly Regex ThinkingTagRegex = new(
        @"<\|?(?:think|thought)\|?>.*?(?:</\|?(?:think|thought)\|?>|<\|/(?:think|thought)\|?>|$)|\[(?:THINK|THOUGHT)\](.*?)(?:\[/(?:THINK|THOUGHT)\]|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CodeBlockRegex = new(
        @"```[\s\S]*?```|`[^`]+`",
        RegexOptions.Compiled);

    private static readonly Regex ToolCallRegex = new(
        @"<\|?tool_call\|?>.*?(?:</\|?tool_call\|?>|<\|/tool_call\|>|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PrefixRegex = new(
        @"^(?:title|topic|subject|summary|suggested title|conversation title|here is a title|here's a title|name|header)\s*:\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BulletNumberPrefixRegex = new(
        @"^(?:\d+[\.\)]|[-*•])\s*",
        RegexOptions.Compiled);

    private static readonly Regex SpecialCharsRegex = new(
        @"[^\w\s\-\']",
        RegexOptions.Compiled);

    private static readonly Regex ExcessWhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Cleans and formats a raw title string into a clean, human-readable summary.
    /// </summary>
    private static readonly Regex FenceDelimiterRegex = new(
        @"^[ \t]*(?:`{3,}|~{3,})[^\n]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Emphasis and heading markers hide the label from the anchored prefix pattern: "**Title**:"
    // and "# Summary:" both reached the character stripper with the colon intact, which removed
    // the colon and left the bare word sitting at the front of the title.
    private static readonly Regex MarkdownNoiseRegex = new(
        @"[*_`#>]+",
        RegexOptions.Compiled);

    private static readonly Regex BareLabelRegex = new(
        @"^(?:title|topic|subject|summary|name|header)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string SanitizeTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return "New Chat";

        string text = OutputSanitizer.SanitizeText(rawTitle);
        text = ThinkingTagRegex.Replace(text, "");
        text = Regex.Replace(text, @"</?think>|</?thought>", "", RegexOptions.IgnoreCase);
        text = ToolCallRegex.Replace(text, "");

        // Dropping fenced blocks whole is right when the model answered with a ```json payload --
        // its contents are data, not a title. But a model that simply wrapped the title in a bare
        // fence would lose it, so if nothing usable survives, try again keeping the fenced text
        // and removing only the delimiter lines.
        string title = SelectTitle(CodeBlockRegex.Replace(text, "\n"));
        if (title.Length == 0)
            title = SelectTitle(FenceDelimiterRegex.Replace(text, "\n"));
        if (title.Length == 0)
            return "New Chat";

        // Truncate to maximum 50 characters at word boundary
        if (title.Length > 50)
        {
            title = title.Substring(0, 50);
            int lastSpace = title.LastIndexOf(' ');
            if (lastSpace > 15) // Ensure we don't chop down to an overly short string
            {
                title = title.Substring(0, lastSpace);
            }
            title = title.TrimEnd('-', ' ', ',', '.', '\'', '"');
        }

        return string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
    }

    /// <summary>
    /// Picks the line that carries the title, preferring one the model labelled.
    /// </summary>
    /// <remarks>
    /// Models routinely restate the question and then label the answer
    /// ("...capital of France\nTitle: Paris"). Collapsing that to a single line moved the label
    /// into the middle, where the anchored prefix pattern could not match it and the colon was
    /// stripped as a special character -- the observed "What's the capital of France Title Paris
    /// is the".
    /// </remarks>
    private static string SelectTitle(string text)
    {
        string? labelled = null;
        string? firstPlain = null;
        bool labelAwaitingValue = false;

        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            // Everything that can sit in front of the label has to go before the label is tested
            // for, or the anchored pattern misses it: a list number ("1. Title: x"), a bullet, or
            // an opening quote ("\"Name: x") all hid it, and the colon was then stripped as a
            // special character leaving the bare word at the front of the title.
            string line = MarkdownNoiseRegex.Replace(rawLine, " ").Trim();
            line = BulletNumberPrefixRegex.Replace(line, "");
            line = line.TrimStart('"', '\'', '“', '‘', '[', '(', '{', ' ', '\t', '.', '…', '—', '–');
            if (line.Length == 0) continue;

            Match prefix = PrefixRegex.Match(line);
            bool hasLabel = prefix.Success;
            if (hasLabel) line = line[prefix.Length..];

            string cleaned = CleanLine(line);

            // "Title:" or a bare "Title" heading: the value is on a following line.
            if (cleaned.Length == 0 || BareLabelRegex.IsMatch(cleaned))
            {
                if (hasLabel || BareLabelRegex.IsMatch(cleaned)) labelAwaitingValue = true;
                continue;
            }

            if (hasLabel || labelAwaitingValue) return cleaned;
            firstPlain ??= cleaned;
        }

        return labelled ?? firstPlain ?? string.Empty;
    }

    /// <summary>
    /// Reduces a single candidate line to plain title text.
    /// </summary>
    private static string CleanLine(string line)
    {
        line = BulletNumberPrefixRegex.Replace(line.Trim(), "");
        line = line.Trim('"', '\'', '`', ' ', '\t', '.', '*', '_', '~', '#', ':', '-', '\u2014', '[', ']', '(', ')', '{', '}', '<', '>');
        line = SpecialCharsRegex.Replace(line, " ");
        line = ExcessWhitespaceRegex.Replace(line, " ").Trim();
        line = PrefixRegex.Replace(line, "").Trim();
        return line;
    }

    /// <summary>
    /// Sanitizes raw message text to prepare it as clean input for title generation.
    /// Strips code blocks, URLs, tool calls, and thinking tags.
    /// </summary>
    public static string PrepareTextForPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string clean = text;

        // Strip thinking blocks
        clean = ThinkingTagRegex.Replace(clean, "");

        // Strip tool calls
        clean = ToolCallRegex.Replace(clean, "");

        // Strip code blocks and inline code
        clean = CodeBlockRegex.Replace(clean, " ");

        // Strip URLs
        clean = UrlRegex.Replace(clean, " ");

        // Remove markdown headings and symbols
        clean = Regex.Replace(clean, @"[#*_`~>|]+", " ");

        // Collapse whitespace
        clean = ExcessWhitespaceRegex.Replace(clean, " ").Trim();

        // Limit length for prompt usage
        if (clean.Length > 300)
        {
            clean = clean.Substring(0, 300).Trim();
        }

        return clean;
    }

    /// <summary>
    /// Derives a clean fallback title from the initial user message.
    /// </summary>
    public static string DeriveTitleFromMessage(string userMessage)
    {
        string cleanedMessage = PrepareTextForPrompt(userMessage);

        if (string.IsNullOrWhiteSpace(cleanedMessage))
            return "New Chat";

        var words = cleanedMessage.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(7);
        string initialTitle = string.Join(" ", words);

        return SanitizeTitle(initialTitle);
    }
}
