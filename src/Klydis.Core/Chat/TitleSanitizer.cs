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
    public static string SanitizeTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return "New Chat";

        string title = OutputSanitizer.SanitizeText(rawTitle);

        // 1. Remove thinking/thought blocks and unclosed thinking tags
        title = ThinkingTagRegex.Replace(title, "");
        title = Regex.Replace(title, @"</?think>|</?thought>", "", RegexOptions.IgnoreCase);

        // 2. Remove tool call artifacts
        title = ToolCallRegex.Replace(title, "");

        // 3. Remove leading prefixes like "Title:", "Subject:", "1."
        title = title.Trim();
        title = PrefixRegex.Replace(title, "");
        title = BulletNumberPrefixRegex.Replace(title, "");

        // 4. Strip quotes, backticks, brackets, braces, angle brackets
        title = title.Trim('"', '\'', '`', ' ', '\t', '\n', '\r', '.', '*', '_', '~', '#', ':', '-', '—', '[', ']', '(', ')', '{', '}', '<', '>');

        // 5. Replace markdown formatting and special symbols (keep alphanumeric, space, hyphen, apostrophe)
        title = SpecialCharsRegex.Replace(title, " ");

        // 6. Collapse multiple spaces
        title = ExcessWhitespaceRegex.Replace(title, " ").Trim();

        // 7. Remove any newly exposed leading prefixes after symbol cleanup
        title = PrefixRegex.Replace(title, "").Trim();

        if (string.IsNullOrWhiteSpace(title))
            return "New Chat";

        // 8. Truncate to maximum 50 characters at word boundary
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
