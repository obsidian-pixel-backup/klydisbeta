using System;
using System.Text.RegularExpressions;

namespace Klydis.Core.Chat;

/// <summary>
/// Centralized sanitizer for cleaning model output, removing internal thinking mode tags,
/// AntML markup tags, special prompt tokens, and formatting tool call blocks.
/// </summary>
public static class OutputSanitizer
{
    private static readonly Regex AntmlTagRegex = new(
        @"\{antml:thinking_mode\}[\s\S]*?\{/antml:thinking_mode\}|<antml:thinking_mode>[\s\S]*?</antml:thinking_mode>|\{antml:[^}]*\}[\s\S]*?\{/antml:[^}]*\}|<antml:[^>]*>[\s\S]*?</antml:[^>]*>|(?:\{|\<)/?antml:[^}\>]*[\}\>]|(?:\{|\<)/?thinking_mode:[^}\>]*[\}\>]|\{antml:[^}]*\}|<antml:[^>]*>|\{thinking_mode\}|\{/thinking_mode\}",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpecialTokenRegex = new(
        @"<\|im_start\|>|<\|im_end\|>|<\|eot_id\|>|<\|end_of_text\|>|<end_of_turn>|<\|end\|>|<\|END_OF_TURN_TOKEN\|>|<\|endoftext\|>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThinkingBlockRegex = new(
        @"<\|?(?:think|thought)\|?>([\s\S]*?)(?:</\|?(?:think|thought)\|?>|<\|/(?:think|thought)\|?>|$)|\[(?:THINK|THOUGHT)\]([\s\S]*?)(?:\[/(?:THINK|THOUGHT)\]|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RawThinkingTagsRegex = new(
        @"</?\|?(?:think|thought)\|?>|\[/?(?:THINK|THOUGHT)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ToolCallBlockRegex = new(
        @"<\|?tool_call\|?>[\s\S]*?(?:</\|?tool_call\|?>|<\|/tool_call\|>|$)|\[TOOL_CALLS\][\s\S]*?(?:\[/TOOL_CALLS\]|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strips antml system markup, special prompt tokens, and raw system tags from text.
    /// </summary>
    public static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string clean = AntmlTagRegex.Replace(text, "");
        clean = SpecialTokenRegex.Replace(clean, "");
        return clean.Trim();
    }

    /// <summary>
    /// Strips tool call blocks, antml system tags, and special tokens from message text.
    /// </summary>
    public static string StripToolCallBlocks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string clean = AntmlTagRegex.Replace(text, "");
        clean = SpecialTokenRegex.Replace(clean, "");
        clean = ToolCallBlockRegex.Replace(clean, "");
        return clean.Trim();
    }

    /// <summary>
    /// Strips thinking blocks, antml tags, special tokens, and tool calls to leave clean context history.
    /// </summary>
    public static string CleanHistoryResponse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string clean = ThinkingBlockRegex.Replace(text, "");
        clean = RawThinkingTagsRegex.Replace(clean, "");
        clean = AntmlTagRegex.Replace(clean, "");
        clean = SpecialTokenRegex.Replace(clean, "");
        return clean.Trim();
    }

    /// <summary>
    /// Splits message text into separate Thinking and Main Content strings,
    /// stripping antml tags and suppressing empty/invalid thinking content.
    /// </summary>
    public static (string Thinking, string Content) SplitThinkingContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, text ?? string.Empty);

        string sanitizedText = AntmlTagRegex.Replace(text, "");
        sanitizedText = SpecialTokenRegex.Replace(sanitizedText, "");

        var match = ThinkingBlockRegex.Match(sanitizedText);
        if (match.Success)
        {
            string thinkingGroup = !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
            string thinking = AntmlTagRegex.Replace(thinkingGroup, "").Trim();
            thinking = SpecialTokenRegex.Replace(thinking, "").Trim();

            string remaining = sanitizedText.Remove(match.Index, match.Length).Trim();
            remaining = AntmlTagRegex.Replace(remaining, "").Trim();

            if (string.IsNullOrWhiteSpace(thinking) || thinking.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                thinking = string.Empty;
            }

            if (remaining.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                remaining = string.Empty;
            }

            return (thinking, remaining);
        }

        string cleanRemaining = AntmlTagRegex.Replace(sanitizedText, "").Trim();
        if (cleanRemaining.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, string.Empty);
        }

        return (string.Empty, cleanRemaining);
    }
}
