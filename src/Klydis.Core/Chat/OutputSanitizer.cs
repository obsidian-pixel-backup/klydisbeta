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
        @"<\|?(?:think|thought)\|?>([\s\S]*?)(?:</\|?(?:think|thought)\|?>|<\|/(?:think|thought)\|?>|$)|\[(?:THINK|THOUGHT)\]([\s\S]*?)(?:\[/(?:THINK|THOUGHT)\]|$)|<antml:thinking>([\s\S]*?)</antml:thinking>|\{antml:thinking\}([\s\S]*?)\{/antml:thinking\}",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RawThinkingTagsRegex = new(
        @"</?\|?(?:think|thought)\|?>|\[/?(?:THINK|THOUGHT)\]|</?antml:thinking>|\{/?antml:thinking\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ToolCallBlockRegex = new(
        @"<\|?tool_call\|?>[\s\S]*?(?:</\|?tool_call\|?>|<\|/tool_call\|>|$)|\[TOOL_CALLS\][\s\S]*?(?:\[/TOOL_CALLS\]|$)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ThinkCloseTags = new[]
    {
        "</think>", "</|think|>", "<|/think|>", "</thought>", "</|thought|>", "<|/thought|>",
        "[/THINK]", "[/THOUGHT]", "</antml:thinking>", "{/antml:thinking}", "</antml:thinking_mode>",
        "{/antml:thinking_mode}", "</thinking_mode>", "{/thinking_mode}", "</thinking>"
    };

    private static readonly string[] ThinkOpenTags = new[]
    {
        "<think>", "<|think|>", "<thought>", "<|thought|>",
        "[THINK]", "[THOUGHT]", "<antml:thinking>", "{antml:thinking}", "<antml:thinking_mode>",
        "{antml:thinking_mode}", "<thinking_mode>", "{thinking_mode}", "<thinking>"
    };

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
    /// Strips ONLY thinking blocks (all tag families) from text, leaving everything else —
    /// including antml tool calls — intact. Used by the tool-call parser before its JSON
    /// fallbacks run, so reasoning that merely looks like JSON never becomes a phantom call
    /// while real antml/qwen calls survive.
    /// </summary>
    public static string StripThinkingBlocks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string current = text;
        // Check for prompt-opened thinking block (close tag with no prior open tag)
        int earliestClose = -1;
        int closeLen = 0;
        foreach (var close in ThinkCloseTags)
        {
            int idx = current.IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (earliestClose < 0 || idx < earliestClose))
            {
                earliestClose = idx;
                closeLen = close.Length;
            }
        }

        if (earliestClose >= 0)
        {
            bool hasOpenBefore = false;
            foreach (var open in ThinkOpenTags)
            {
                int openIdx = current.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (openIdx >= 0 && openIdx < earliestClose)
                {
                    hasOpenBefore = true;
                    break;
                }
            }
            if (!hasOpenBefore)
            {
                current = current.Substring(earliestClose + closeLen);
            }
        }

        string clean = ThinkingBlockRegex.Replace(current, "");
        clean = RawThinkingTagsRegex.Replace(clean, "");
        return clean.Trim();
    }

    /// <summary>
    /// Strips thinking blocks, antml tags, special tokens, and tool calls to leave clean context history.
    /// </summary>
    public static string CleanHistoryResponse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        string clean = StripThinkingBlocks(text);
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

        // Match thinking FIRST on the RAW text — the antml markup regex below would otherwise
        // consume <antml:thinking>…</antml:thinking> blocks before they can be extracted
        // (the antml strip targets system markup, not the thinking block itself).
        var match = ThinkingBlockRegex.Match(text);
        string thinking = string.Empty;
        string remaining = text;

        if (match.Success)
        {
            string thinkingGroup = FirstNonEmptyGroup(match, 1, 2, 3, 4);
            thinking = AntmlTagRegex.Replace(thinkingGroup, "").Trim();
            thinking = SpecialTokenRegex.Replace(thinking, "").Trim();

            remaining = text.Remove(match.Index, match.Length).Trim();
        }
        else
        {
            // Prompt-opened thinking block check (close tag with no prior open tag)
            int earliestClose = -1;
            int closeLen = 0;
            foreach (var close in ThinkCloseTags)
            {
                int idx = text.IndexOf(close, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (earliestClose < 0 || idx < earliestClose))
                {
                    earliestClose = idx;
                    closeLen = close.Length;
                }
            }

            if (earliestClose >= 0)
            {
                bool hasOpenBefore = false;
                foreach (var open in ThinkOpenTags)
                {
                    int openIdx = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                    if (openIdx >= 0 && openIdx < earliestClose)
                    {
                        hasOpenBefore = true;
                        break;
                    }
                }

                if (!hasOpenBefore)
                {
                    thinking = text.Substring(0, earliestClose).Trim();
                    thinking = AntmlTagRegex.Replace(thinking, "").Trim();
                    thinking = SpecialTokenRegex.Replace(thinking, "").Trim();
                    remaining = text.Substring(earliestClose + closeLen).Trim();
                }
            }
        }

        string cleanRemaining = AntmlTagRegex.Replace(remaining, "");
        cleanRemaining = SpecialTokenRegex.Replace(cleanRemaining, "").Trim();

        if (string.IsNullOrWhiteSpace(thinking) || thinking.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            thinking = string.Empty;
        }

        if (cleanRemaining.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            cleanRemaining = string.Empty;
        }

        return (thinking, cleanRemaining);
    }

    private static string FirstNonEmptyGroup(System.Text.RegularExpressions.Match match, params int[] groups)
    {
        foreach (int g in groups)
        {
            if (match.Groups[g].Success && !string.IsNullOrEmpty(match.Groups[g].Value))
            {
                return match.Groups[g].Value;
            }
        }
        return string.Empty;
    }
}
