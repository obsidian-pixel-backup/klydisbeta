using System;
using System.Collections.Generic;

namespace Klydis.Core.Chat;

/// <summary>
/// Incremental parser for the chat token stream. It owns the streaming state machine that used
/// to live inline in <c>ChatEngine.StreamResponseAsync</c>: thinking-block tracking, tool-call
/// tracking, stray close-tag suppression, and partial-tag withholding across token boundaries.
///
/// Usage: feed tokens with <see cref="Append"/>, then drain events with <see cref="TryDequeue"/>
/// until it returns false. The caller decides how to render each event and accumulates the raw
/// stream (including the <c>&lt;/think&gt;</c> the parser may inject when a qwen model opens a
/// tool call inside its pre-opened think block — see <see cref="ConsumeInjectedRawText"/>).
/// </summary>
public sealed class ChatStreamParser
{
    private static readonly string[] ThinkStartTags = new[]
    {
        "<think>", "<|think|>", "<thought>", "<|thought|>", "[THINK]", "[THOUGHT]",
        "{antml:thinking_mode}", "<antml:thinking_mode>", "{thinking_mode}", "<thinking_mode>",
        // Anthropic/Claude native thinking (real Claude models use <antml:thinking>, no
        // _mode suffix — the _mode forms are the older system-reminder variant).
        "<antml:thinking>", "{antml:thinking}", "<thinking>"
    };

    private static readonly string[] ThinkEndTags = new[]
    {
        "</think>", "</|think|>", "<|/think|>", "</thought>", "</|thought|>", "<|/thought|>",
        "[/THINK]", "[/THOUGHT]", "{/antml:thinking_mode}", "</antml:thinking_mode>", "{/thinking_mode}", "</thinking_mode>",
        "</antml:thinking>", "{/antml:thinking}", "</thinking>"
    };

    private static readonly string[] StrayThinkCloseTags = ThinkEndTags;

    private static readonly string[] ToolStartTags = new[]
    {
        "<tool_call>", "<|tool_call|>", "<tool_calls>", "<|tool_calls|>",
        "[TOOL_CALLS]", "[TOOL_CALL]", "<TOOL_CALL>", "<TOOL_CALLS>",
        // Anthropic/Claude native calls (antml) — kept out of the visible stream just like
        // the other tool formats so the user sees the call only in the UI's tool-call card.
        "<antml:invoke"
    };

    private static readonly string[] ToolEndTags = new[]
    {
        "</tool_call>", "</|tool_call|>", "<|/tool_call|>", "</tool_calls>", "</|tool_calls|>", "<|/tool_calls|>",
        "[/TOOL_CALLS]", "[/TOOL_CALL]", "</TOOL_CALL>", "</TOOL_CALLS>",
        "</antml:invoke>"
    };

    // All proper prefixes of the closing-think and opening-tool tags, used to avoid yielding a
    // partial tag across token boundaries. Precomputed once.
    private static readonly string[] PartialCloseThinkOrToolTags = BuildPartialTagPrefixes(ThinkEndTags, ToolStartTags);
    private static readonly string[] PartialOpenThinkOrToolTags = BuildPartialTagPrefixes(ThinkStartTags, ToolStartTags);

    private static string[] BuildPartialTagPrefixes(params string[][] tagSets)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tags in tagSets)
        {
            foreach (var tag in tags)
            {
                for (int len = 1; len < tag.Length; len++)
                {
                    set.Add(tag.Substring(0, len));
                }
            }
        }
        return new List<string>(set).ToArray();
    }

    private readonly Queue<ChatStreamEvent> _events = new();
    private readonly System.Text.StringBuilder _injectedRawText = new();

    private string _unyieldedText = string.Empty;
    private bool _isThinking;
    private bool _isToolCall;
    private bool _streamEnded;

    /// <summary>
    /// True while the parser believes the model is inside a thinking block.
    /// </summary>
    public bool IsThinking => _isThinking;

    /// <summary>
    /// True while the parser believes the model is inside a tool-call block.
    /// </summary>
    public bool IsToolCall => _isToolCall;

    /// <summary>
    /// Whether the parser was initialized inside a pre-opened thinking block
    /// (the prompt ended with <c>&lt;think&gt;</c>).
    /// </summary>
    public bool PromptStartedInsideThink { get; }

    /// <summary>
    /// Whether the model explicitly opened a thinking block during the stream
    /// (distinct from the harness pre-opening one).
    /// </summary>
    public bool ModelOpenedThink { get; private set; }

    /// <summary>
    /// Whether the model closed a thinking block during the stream.
    /// </summary>
    public bool ModelClosedThink { get; private set; }

    /// <summary>
    /// Whether any visible (non-thinking, non-tool-call) tokens were produced.
    /// </summary>
    public bool HasVisibleTokens { get; private set; }

    /// <summary>
    /// Whether any thinking tokens were produced.
    /// </summary>
    public bool HasThinkingTokens { get; private set; }

    /// <summary>
    /// Whether the stream ended with an unclosed thinking block.
    /// </summary>
    public bool IsUnclosedThink => _isThinking && _streamEnded;

    public ChatStreamParser(bool startsInsideThinkBlock)
    {
        // For qwen thinking models the generation prompt already ends with an OPEN <think> block
        // (the model continues it, then closes it). Start INSIDE that block — otherwise the
        // model's first </think> close tag is treated as plain text and streamed to the user.
        _isThinking = startsInsideThinkBlock;
        PromptStartedInsideThink = startsInsideThinkBlock;
    }

    /// <summary>
    /// Feeds one streamed token (or chunk) into the parser. Events become available via
    /// <see cref="TryDequeue"/>.
    /// </summary>
    public void Append(string token)
    {
        if (_streamEnded) return;

        _unyieldedText += token;

        bool processedAny;
        do
        {
            processedAny = false;

            if (!_isToolCall)
            {
                // A closing tag for a thinking block while NO block is open is degenerate noise
                // — the model spamming </think> after already closing its think block (a common
                // MoE "psycho loop"). Drop the stray close tags silently instead of streaming
                // them to the user as visible garbage.
                if (!_isThinking)
                {
                    foreach (var strayClose in StrayThinkCloseTags)
                    {
                        int idx;
                        while ((idx = _unyieldedText.IndexOf(strayClose, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            // A close tag is only STRAY when no opener precedes it in the buffer.
                            // If an opening tag appears earlier (e.g. a whole
                            // "<thinking>…</thinking>" block arriving in one token batch), the
                            // close belongs to that block — leave it for the open/close state
                            // machine instead of deleting it (which would swallow the close and
                            // stream everything after it as thinking).
                            bool openerPrecedes = false;
                            foreach (var openTag in ThinkStartTags)
                            {
                                int openIdx = _unyieldedText.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
                                if (openIdx >= 0 && openIdx < idx)
                                {
                                    openerPrecedes = true;
                                    break;
                                }
                            }
                            if (openerPrecedes) break;

                            _unyieldedText = _unyieldedText.Remove(idx, strayClose.Length);
                            processedAny = true;
                        }
                    }
                }

                int thinkIndex = -1;
                int thinkTagLen = 0;
                if (!_isThinking)
                {
                    FindEarliest(ThinkStartTags, out thinkIndex, out thinkTagLen);
                }

                int thinkEndIndex = -1;
                int thinkEndTagLen = 0;
                if (_isThinking)
                {
                    FindEarliest(ThinkEndTags, out thinkEndIndex, out thinkEndTagLen);
                }

                int toolIndex = -1;
                int toolTagLen = 0;
                FindEarliest(ToolStartTags, out toolIndex, out toolTagLen);

                // Find which event comes earliest
                int earliest = int.MaxValue;
                if (thinkIndex >= 0 && thinkIndex < earliest) earliest = thinkIndex;
                if (thinkEndIndex >= 0 && thinkEndIndex < earliest) earliest = thinkEndIndex;
                if (toolIndex >= 0 && toolIndex < earliest) earliest = toolIndex;

                if (earliest == int.MaxValue)
                    break;

                if (earliest == thinkIndex)
                {
                    string before = _unyieldedText.Substring(0, thinkIndex);
                    if (!string.IsNullOrEmpty(before))
                    {
                        HasVisibleTokens = true;
                        _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.Token, before));
                    }

                    _isThinking = true;
                    ModelOpenedThink = true;
                    _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.ThinkingStart, string.Empty));
                    _unyieldedText = _unyieldedText.Substring(thinkIndex + thinkTagLen);
                    processedAny = true;
                }
                else if (earliest == thinkEndIndex)
                {
                    string before = _unyieldedText.Substring(0, thinkEndIndex);
                    if (!string.IsNullOrEmpty(before))
                    {
                        HasThinkingTokens = true;
                        _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.ThinkingToken, before));
                    }

                    _isThinking = false;
                    ModelClosedThink = true;
                    _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.ThinkingEnd, string.Empty));
                    _unyieldedText = _unyieldedText.Substring(thinkEndIndex + thinkEndTagLen);
                    processedAny = true;
                }
                else if (earliest == toolIndex)
                {
                    string before = _unyieldedText.Substring(0, toolIndex);
                    if (!string.IsNullOrEmpty(before))
                    {
                        if (_isThinking)
                        {
                            HasThinkingTokens = true;
                        }
                        else
                        {
                            HasVisibleTokens = true;
                        }
                        _events.Enqueue(new ChatStreamEvent(
                            _isThinking ? ChatStreamEventType.ThinkingToken : ChatStreamEventType.Token,
                            before));
                    }

                    // Qwen thinking models commonly emit <tool_call> while still "inside" the
                    // pre-opened think block. Treat the tool call as implicitly closing the think
                    // block: inject the close tag into the RAW stream (so the sanitizer keeps the
                    // call), exit thinking mode, and stream the call as visible content.
                    if (_isThinking)
                    {
                        _injectedRawText.Append("</think>");
                        _isThinking = false;
                        _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.ThinkingEnd, string.Empty));
                    }

                    _isToolCall = true;
                    _unyieldedText = _unyieldedText.Substring(toolIndex + toolTagLen);
                    processedAny = true;
                }
            }
            else // _isToolCall == true
            {
                int toolEndIndex = -1;
                int toolEndTagLen = 0;
                FindEarliest(ToolEndTags, out toolEndIndex, out toolEndTagLen);

                if (toolEndIndex >= 0)
                {
                    _isToolCall = false;
                    _unyieldedText = _unyieldedText.Substring(toolEndIndex + toolEndTagLen);
                    processedAny = true;
                }
            }
        } while (processedAny);

        // Yield safe text (avoiding cut-off partial tags)
        if (string.IsNullOrEmpty(_unyieldedText)) return;

        if (_isToolCall)
        {
            // Let unyieldedText accumulate so we can find the closing tool tag in the next
            // iteration. Do not yield the raw tool JSON, and do not clear it.
            return;
        }

        var tagsToCheck = _isThinking ? PartialCloseThinkOrToolTags : PartialOpenThinkOrToolTags;

        bool endsWithPartial = false;
        int maxPartialLen = 0;
        foreach (var prefix in tagsToCheck)
        {
            if (prefix.Length > maxPartialLen && _unyieldedText.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                endsWithPartial = true;
                maxPartialLen = prefix.Length;
            }
        }

        if (endsWithPartial && maxPartialLen > 0)
        {
            int safeLen = _unyieldedText.Length - maxPartialLen;
            string safePart = _unyieldedText.Substring(0, safeLen);
            if (!string.IsNullOrEmpty(safePart))
            {
                if (_isThinking)
                {
                    HasThinkingTokens = true;
                }
                else
                {
                    HasVisibleTokens = true;
                }
                _events.Enqueue(new ChatStreamEvent(
                    _isThinking ? ChatStreamEventType.ThinkingToken : ChatStreamEventType.Token,
                    safePart));
            }
            _unyieldedText = _unyieldedText.Substring(safeLen);
        }
        else
        {
            if (_isThinking)
            {
                HasThinkingTokens = true;
            }
            else
            {
                HasVisibleTokens = true;
            }
            _events.Enqueue(new ChatStreamEvent(
                _isThinking ? ChatStreamEventType.ThinkingToken : ChatStreamEventType.Token,
                _unyieldedText));
            _unyieldedText = string.Empty;
        }
    }

    /// <summary>
    /// Flushes whatever remains buffered at the end of the stream. Call once, after the last
    /// <see cref="Append"/>. Discards partial tool-call JSON (a stream ending inside a tool call
    /// block never surfaces its raw JSON as visible text) and removes stray think-close tags.
    /// </summary>
    public void EndStream()
    {
        if (_streamEnded) return;
        _streamEnded = true;

        if (string.IsNullOrEmpty(_unyieldedText)) return;

        if (_isToolCall)
        {
            // Stream ended inside an unclosed tool call block; discard partial tool payload so raw JSON never reaches user
            _unyieldedText = string.Empty;
            return;
        }

        if (!_isThinking)
        {
            // Drop stray think-close tags left over at stream end (see Append).
            foreach (var strayClose in StrayThinkCloseTags)
            {
                int idx;
                while ((idx = _unyieldedText.IndexOf(strayClose, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    _unyieldedText = _unyieldedText.Remove(idx, strayClose.Length);
                }
            }
        }

        if (_isThinking)
        {
            HasThinkingTokens = true;
            _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.ThinkingToken, _unyieldedText));
        }
        else if (!string.IsNullOrEmpty(_unyieldedText))
        {
            HasVisibleTokens = true;
            _events.Enqueue(new ChatStreamEvent(ChatStreamEventType.Token, _unyieldedText));
        }
        _unyieldedText = string.Empty;
    }

    /// <summary>
    /// Dequeues the next parsed event, or returns false when the queue is empty.
    /// </summary>
    public bool TryDequeue(out ChatStreamEvent evt)
    {
        if (_events.Count == 0)
        {
            evt = default!;
            return false;
        }
        evt = _events.Dequeue();
        return true;
    }

    /// <summary>
    /// Text the parser injected into the raw stream (currently the implicit <c>&lt;/think&gt;</c>
    /// when a qwen model opens a tool call inside its pre-opened think block). The caller must
    /// append this to its raw-stream accumulator so the sanitizer keeps the tool call intact.
    /// </summary>
    public string ConsumeInjectedRawText()
    {
        var text = _injectedRawText.ToString();
        _injectedRawText.Clear();
        return text;
    }

    private void FindEarliest(string[] tags, out int index, out int tagLen)
    {
        index = -1;
        tagLen = 0;
        foreach (var tag in tags)
        {
            int idx = _unyieldedText.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (index < 0 || idx < index || (idx == index && tag.Length > tagLen)))
            {
                // Earliest occurrence wins; at the SAME index the LONGEST tag wins so a
                // prefix-overlapping pair (<think> vs <thinking>) never leaves a fragment.
                index = idx;
                tagLen = tag.Length;
            }
        }
    }
}
