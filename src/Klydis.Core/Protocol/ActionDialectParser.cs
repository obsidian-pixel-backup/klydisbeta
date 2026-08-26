using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klydis.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Protocol;

/// <summary>
/// The shared tolerant multi-dialect tool-call parser. P1.6: this is the entire legacy
/// ChatEngine.ParseToolCalls pipeline, moved verbatim into the protocol layer so that
/// protocol adapters and the legacy fallback path consume IDENTICAL parsing (behavioral
/// parity by construction — no drift between "what the adapter parses" and "what the legacy
/// path parses").
///
/// Dialects handled, in priority order:
///   0a. JSON action envelope  {"action":"tool_call"|"completion_claim"|"replan"|"plan"|...}
///   0.  Qwen-native           &lt;tool_call&gt;&lt;function=NAME&gt;&lt;parameter=K&gt;...&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;
///   0b. Anthropic antml       &lt;antml:invoke name="TOOL"&gt;&lt;antml:parameter name="ARG"&gt;...&lt;/antml:parameter&gt;&lt;/antml:invoke&gt;
///   1.  &lt;tool_call&gt; JSON blocks (singular/plural, nested braces, optional end tag)
///   2.  [TOOL_CALLS] bracketed JSON
///   3.  Markdown ```json blocks containing tool keys
///   4.  Raw untagged JSON objects with name/tool/action/function keys
///
/// Deliberately tolerant of the syntax real fine-tunes produce (attribute/'='/bare forms,
/// dropped '=', missing close tags, unescaped control characters) — a strict parser turned
/// well-intentioned calls into the "INCOMPLETE" feedback loop. Layer 5 (narrative
/// "tool_name / Input: {...}" prose) is intentionally ABSENT: it laundered fabricated
/// planning text into real executions.
/// </summary>
internal static class ActionDialectParser
{
    /// <summary>
    /// A parsed call with its TRUE canonical type and the DIALECT that actually produced it
    /// (independent of the model family — a Qwen model may emit antml, a generic model may
    /// emit a JSON envelope). P1.6b: this is the semantic core; the ToolCallRequest shape
    /// (which erases the ToolCall/CompletionClaim/Replan distinction) is only a projection.
    /// </summary>
    private readonly record struct ParsedCall(ToolCallRequest Request, CanonicalActionType Type, string Dialect);

    /// <summary>
    /// Parses think-stripped model output into executable tool-call requests. Never throws.
    /// Returns an empty list for plain text or unparseable output (the caller decides whether
    /// that is a message or an escalation).
    /// </summary>
    public static List<ToolCallRequest> ParseAll(string? response, ILogger? logger = null)
        => ParseCalls(response, logger).Select(c => c.Request).ToList();

    /// <summary>
    /// P1.6b: the canonical parse. Every dialect normalizes to a <see cref="CanonicalAction"/>
    /// carrying its TRUE type — ToolCall / CompletionClaim / Replan, never re-inferred from
    /// tool names — and the detected dialect as SourceProtocol (qwen-native / antml /
    /// json-envelope / tool-json). Text-only or unparseable output yields an empty list.
    /// </summary>
    public static List<CanonicalAction> ParseCanonical(string? response, ILogger? logger = null)
    {
        var parsed = ParseCalls(response, logger);
        var actions = new List<CanonicalAction>(parsed.Count);
        foreach (var call in parsed)
        {
            actions.Add(new CanonicalAction(
                call.Type,
                call.Request.Name,
                null, null, null,
                call.Dialect,
                call.Request.Arguments));
        }
        return actions;
    }

    private static List<ParsedCall> ParseCalls(string? response, ILogger? logger = null)
    {
        var results = new List<ParsedCall>();
        if (string.IsNullOrWhiteSpace(response)) return results;

        // Self-protecting: strip thinking blocks up front (thinking tags only — antml/qwen
        // tool calls survive). Reasoning routinely contains JSON shaped like {"name": ...} /
        // {"tool": ...} planning lines, and the loose JSON fallback layers below would
        // misread them as real tool calls (phantom executions that re-run the whole turn
        // loop). The production caller already passes a think-stripped response; stripping
        // here makes the parser safe for any caller.
        response = OutputSanitizer.StripThinkingBlocks(response);
        if (string.IsNullOrWhiteSpace(response)) return results;

        // 0a. JSON Action Envelope (model-independent protocol): a standalone JSON object with
        // an explicit "action" field —
        // {"action":"tool_call","name":"...","arguments":{...}},
        // {"action":"completion_claim","summary":"..."}, {"action":"replan","items":[...]}.
        // Parsed FIRST because it is the most explicit and validated protocol form; it also
        // maps completion_claim/replan to their tool equivalents (task_complete / plan), which
        // the generic JSON heuristics would otherwise misread as tools literally named
        // "completion_claim" or "replan".
        var envelopeCall = TryParseJsonActionEnvelope(response);
        if (envelopeCall != null)
        {
            results.Add(envelopeCall.Value);
            return results;
        }

        // 0. Qwen-native format (qwen35/qwen35moe models): <tool_call><function=NAME><parameter=K>
        // value</parameter>...</function></tool_call>. The embedded qwen template emits this exact
        // structure when tools are provided; it is NOT JSON, so it must be parsed before the
        // JSON heuristics below. Returns immediately when found — the native format is
        // unambiguous and the loose JSON fallbacks must never run on it.
        //
        // Parsing is deliberately TOLERANT of the syntax qwen fine-tunes actually produce
        // (observed in production chat exports): the function tag may be <function=NAME>,
        // <function name="NAME">, or the broken <function>NAME (missing '='); parameters may be
        // <parameter=K>value</parameter>, <parameter name="K">value</parameter>,
        // <parameter K>value</parameter> (missing '='), or bare <K>value</K> tags. A strict
        // regex turned well-intentioned calls into the "INCOMPLETE" feedback loop — the model
        // was told to fix a call the parser simply could not read.
        {
            // Qwen thinking models routinely omit the closing </tool_call> (observed in
            // production logs: <tool_call><function=search_web><parameter=query>...</function>
            // with no close tag). Treat the closing tag as optional so an unclosed native call
            // still parses and executes instead of derailing the whole turn.
            var nativeBlocks = Regex.Matches(response,
                @"<\|?tool_calls?\|?>(.*?)(?:</\|?tool_calls?\|?>|<\|/tool_calls?\|?>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match block in nativeBlocks)
            {
                var body = block.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(body)) continue;

                string? name = null;
                string innerBody = body;

                // 1. Explicit <function ...> tag: attribute form, '=' form, ':' form, or bare text
                if (body.IndexOf("<function", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var fnAttr = Regex.Match(body,
                        @"<function\s+name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))\s*>",
                        RegexOptions.IgnoreCase);
                    if (fnAttr.Success)
                    {
                        name = FirstNonEmpty(fnAttr, 1, 2, 3);
                    }
                    else
                    {
                        var fnEq = Regex.Match(body, @"<function\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))\s*>", RegexOptions.IgnoreCase);
                        if (fnEq.Success)
                        {
                            name = FirstNonEmpty(fnEq, 1, 2, 3);
                        }
                        else
                        {
                            var fnColon = Regex.Match(body, @"<function:\s*([a-zA-Z0-9_.\-]+)\s*>", RegexOptions.IgnoreCase);
                            if (fnColon.Success)
                            {
                                name = fnColon.Groups[1].Value;
                            }
                            else
                            {
                                var fnBare = Regex.Match(body, @"<function>\s*([a-zA-Z0-9_.\-]+)(?:\s*[><])", RegexOptions.IgnoreCase);
                                if (fnBare.Success)
                                {
                                    name = fnBare.Groups[1].Value;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 2. Tag where tool name is the XML element directly inside <tool_call>,
                    // e.g. <tool_call><crawl_url><url>...</url></crawl_url></tool_call>
                    // Only apply if body is NOT a JSON object/array (JSON is handled in layer 1).
                    var trimmedBody = body.Trim();
                    if (!trimmedBody.StartsWith("{", StringComparison.Ordinal) && !trimmedBody.StartsWith("[", StringComparison.Ordinal))
                    {
                        var toolTagMatch = Regex.Match(trimmedBody,
                            @"^<(?!\b(?:tool_calls?|think|thought|antml|parameter|param)\b)([a-zA-Z_][a-zA-Z0-9_.\-]*)(?:\s+[^>]*)?>([\s\S]*?)(?:</\1>|$)",
                            RegexOptions.Singleline | RegexOptions.IgnoreCase);
                        if (toolTagMatch.Success)
                        {
                            name = toolTagMatch.Groups[1].Value;
                            innerBody = toolTagMatch.Groups[2].Value;
                        }
                    }
                }

                if (string.IsNullOrEmpty(name)) continue;

                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                // <parameter ...>value</parameter> or <param ...>value</param> in any tolerated syntax:
                // =K, name="K", name=K, :K, or bare K, closed by </parameter>, </param>, matching </key_name>, next param, or </function>.
                foreach (Match pm in Regex.Matches(innerBody,
                    @"<(?:parameter|param)\s*(?:=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))|name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))|:\s*([a-zA-Z0-9_.\-]+)|([a-zA-Z0-9_.\-]+))\s*>([\s\S]*?)(?:</(?:parameter|param)>|</\1>|</\2>|</\3>|</\4>|</\5>|</\6>|</\7>|</\8>|(?=<(?:parameter|param)\b|</function>|</tool_call>|$))",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var key = FirstNonEmpty(pm, 1, 2, 3, 4, 5, 6, 7, 8);
                    if (string.IsNullOrEmpty(key)) continue;
                    var rawVal = WebUtility.HtmlDecode(pm.Groups[9].Value.Trim());
                    args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                }

                // Bare <K>value</K> tags as parameters (the model's <location>Cape Town</location>
                // style). Reserved tags never map to arguments.
                foreach (Match bm in Regex.Matches(innerBody,
                    @"<(?!function\b|parameter\b|param\b|tool_call\b|tool_calls\b|think\b|thought\b|antml\b|/)([a-zA-Z_][a-zA-Z0-9_.\-]*)\s*>([\s\S]*?)</\1>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var key = bm.Groups[1].Value;
                    if (string.IsNullOrEmpty(key) || args.ContainsKey(key)) continue;
                    var rawVal = WebUtility.HtmlDecode(bm.Groups[2].Value.Trim());
                    args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                }

                // Tolerant fallback for MISMATCHED bare-tag closures
                if (args.Count == 0)
                {
                    foreach (Match tm in Regex.Matches(innerBody,
                        @"<(?!\bfunction\b|\bparameter\b|\bparam\b|\btool_call\b|\btool_calls\b|\bthink\b|\bthought\b|\bantml\b|/)([a-zA-Z_][a-zA-Z0-9_.\-]*)\s*>([\s\S]*?)(?:</[a-zA-Z_][a-zA-Z0-9_.\-]*>|$)",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase))
                    {
                        var key = tm.Groups[1].Value;
                        if (string.IsNullOrEmpty(key) || args.ContainsKey(key)) continue;
                        var rawVal = WebUtility.HtmlDecode(tm.Groups[2].Value.Trim());
                        if (string.IsNullOrEmpty(rawVal)) continue;
                        if (Regex.IsMatch(rawVal, @"<[a-zA-Z_/]")) continue; // nested markup — skip
                        args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                    }
                }

                if (!string.IsNullOrEmpty(name))
                {
                    results.Add(new ParsedCall(new ToolCallRequest(name, args), CanonicalActionType.ToolCall, "qwen-native"));
                }
            }
            if (results.Count > 0) return results;
        }

        // 0b. Anthropic/Claude native format (antml): <antml:invoke name="TOOL"><antml:parameter
        // name="ARG">value</antml:parameter></antml:invoke>. Claude-fine-tuned models (e.g. the
        // qwen35 "Claude-4.6" hybrid seen in production) emit THIS shape, not the qwen native
        // tags above. Without a dedicated parser their calls fell through every layer and died
        // as "INCOMPLETE tool call" — a well-formed Claude call was told to fix itself.
        // Parsing is tolerant: the parameter name may be quoted or bare, multiple parameters
        // are supported, and the closing tag is optional.
        {
            var antmlBlocks = Regex.Matches(response,
                @"<antml:invoke\s+name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))[^>]*>(.*?)(?:</antml:invoke>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match block in antmlBlocks)
            {
                var name = FirstNonEmpty(block, 1, 2, 3);
                if (string.IsNullOrEmpty(name)) continue;

                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (Match pm in Regex.Matches(block.Groups[4].Value,
                    @"<antml:parameter\s+name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))[^>]*>([\s\S]*?)</antml:parameter>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var key = FirstNonEmpty(pm, 1, 2, 3);
                    if (string.IsNullOrEmpty(key)) continue;
                    var rawVal = WebUtility.HtmlDecode(pm.Groups[4].Value.Trim());
                    args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                }

                results.Add(new ParsedCall(new ToolCallRequest(name, args), CanonicalActionType.ToolCall, "antml"));
            }
            if (results.Count > 0) return results;
        }

        // 0c. DeepSeek native tool calls:
        // <｜tool calls begin｜><｜tool call begin｜>function<｜tool sep｜>NAME\n```json\n{...}\n```<｜tool call end｜><｜tool calls end｜>
        {
            var dsBlocks = Regex.Matches(response,
                @"(?:<｜tool call begin｜>|<tool_call_begin>)(?:function(?:<｜tool sep｜>|:))?([a-zA-Z0-9_.\-]+)[\r\n]+(?:```(?:json)?[\r\n]+)?([\s\S]*?)(?:```)?[\r\n]*(?:<｜tool call end｜>|<tool_call_end>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match m in dsBlocks)
            {
                var fnName = m.Groups[1].Value.Trim();
                var rawArgs = m.Groups[2].Value.Trim();
                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(rawArgs))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(SanitizeJsonControlCharacters(rawArgs));
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in doc.RootElement.EnumerateObject())
                            {
                                var val = ToolExecutor.UnwrapJsonElement(p.Value);
                                if (val != null) args[p.Name] = val;
                            }
                        }
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(fnName))
                {
                    results.Add(new ParsedCall(new ToolCallRequest(fnName, args), CanonicalActionType.ToolCall, "deepseek-native"));
                }
            }
            if (results.Count > 0) return results;
        }

        // 0d. Command-R action block:
        // <|START_OF_ACTION_TOKEN|>[{"tool_name":"...","parameters":{...}}]<|END_OF_ACTION_TOKEN|>
        {
            var cmdRBlocks = Regex.Matches(response,
                @"<\|START_OF_ACTION_TOKEN\|>([\s\S]*?)(?:<\|END_OF_ACTION_TOKEN\|>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match m in cmdRBlocks)
            {
                var rawJson = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(rawJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(SanitizeJsonControlCharacters(rawJson));
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in doc.RootElement.EnumerateArray())
                            {
                                string? name = null;
                                if (item.TryGetProperty("tool_name", out var tProp)) name = tProp.GetString();
                                else if (item.TryGetProperty("name", out var nProp)) name = nProp.GetString();

                                if (!string.IsNullOrEmpty(name))
                                {
                                    var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                    if (item.TryGetProperty("parameters", out var pProp) && pProp.ValueKind == JsonValueKind.Object)
                                    {
                                        foreach (var p in pProp.EnumerateObject())
                                        {
                                            var v = ToolExecutor.UnwrapJsonElement(p.Value);
                                            if (v != null) args[p.Name] = v;
                                        }
                                    }
                                    results.Add(new ParsedCall(new ToolCallRequest(name, args), CanonicalActionType.ToolCall, "command-r-native"));
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            if (results.Count > 0) return results;
        }

        // 0e. Llama 3 python tag format: <|python_tag|>tool_name(k="v") or <|python_tag|>{"name":"..."}
        {
            var pyBlocks = Regex.Matches(response,
                @"<\|python_tag\|>\s*([a-zA-Z0-9_.\-]+)\s*\(([\s\S]*?)\)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match m in pyBlocks)
            {
                var fnName = m.Groups[1].Value.Trim();
                var rawParamString = m.Groups[2].Value.Trim();
                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var kwMatches = Regex.Matches(rawParamString,
                    @"([a-zA-Z0-9_]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([a-zA-Z0-9_.-]+))",
                    RegexOptions.IgnoreCase);
                foreach (Match kw in kwMatches)
                {
                    var k = kw.Groups[1].Value;
                    var v = FirstNonEmpty(kw, 2, 3, 4);
                    args[k] = v;
                }
                if (!string.IsNullOrEmpty(fnName))
                {
                    results.Add(new ParsedCall(new ToolCallRequest(fnName, args), CanonicalActionType.ToolCall, "llama3-python"));
                }
            }
            if (results.Count > 0) return results;
        }

        var blocksToParse = new List<string>();

        // 1. Native <tool_call> / <tool_calls> JSON format (supports singular/plural, nested braces, and missing end tag)
        var matches = Regex.Matches(response, @"<\|?tool_calls?\|?>(.*?)(?:</\|?tool_calls?\|?>|<\|/tool_calls?\|?>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var rawContent = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(rawContent))
            {
                blocksToParse.Add(rawContent);
            }
        }

        // 2. Bracketed [TOOL_CALLS] or [TOOL_CALL] [...] format
        if (blocksToParse.Count == 0)
        {
            var toolCallsMatches = Regex.Matches(response, @"\[TOOL_CALLS?\]\s*(\[.*?\]|\{[\s\S]*?\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in toolCallsMatches)
            {
                var rawContent = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rawContent))
                {
                    blocksToParse.Add(rawContent);
                }
            }
        }

        // 3. Markdown ```json code blocks containing tool invocation keys
        if (blocksToParse.Count == 0)
        {
            var codeBlockMatches = Regex.Matches(response, @"```(?:json)?\s*(\{[\s\S]*?\}|\[[\s\S]*?\])\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in codeBlockMatches)
            {
                var block = match.Groups[1].Value.Trim();
                if (block.Contains("\"name\"", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"tool\"", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"action\"", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"function\"", StringComparison.OrdinalIgnoreCase))
                {
                    blocksToParse.Add(block);
                }
            }
        }

        // 4. Fallback: Raw un-tagged JSON objects containing name/tool/action/function
        //
        //    MASKED RESPONSE (loop fix, 2026-08): layer 4 runs on a copy with every NON-json
        //    fenced code block (```bash, ```powershell, ```python, bare ```...) blanked out.
        //    Without this, models that write *illustrative* snippets —
        //    ```bash
        //    tool call{"name":"system_processes","arguments":{"sort":"-memory"}} | jq ...
        //    ```
        //    — had those "here is how you would call it" examples laundered into REAL tool
        //    executions, which executed the same probe every round (mistral-7b export:
        //    system_processes fired 4x while re-listing the 110-prompt plan; each execution
        //    reset the stall clock). Explicit dialect forms (<tool_call>, [TOOL_CALLS],
        //    ```json blocks, antml, envelopes) still parse from the ORIGINAL response above,
        //    so legitimate calls are unaffected — only untagged JSON inside illustrative
        //    fences is no longer treated as an executable action.
        if (blocksToParse.Count == 0)
        {
            string fencedAware = MaskNonJsonFences(response);
            var rawJsonMatches = Regex.Matches(fencedAware, @"(\{[\s\S]*?""(?:name|tool|action|function)""\s*:\s*""[^""]+""[\s\S]*?\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in rawJsonMatches)
            {
                var block = match.Groups[1].Value.Trim();
                if (!blocksToParse.Contains(block))
                {
                    blocksToParse.Add(block);
                }
            }
        }

        foreach (var rawContent in blocksToParse)
        {
            try
            {
                // Decode HTML entities (e.g. &quot;, &lt;, &gt;)
                var content = WebUtility.HtmlDecode(rawContent).Trim();

                // Cleanup common markdown mistakes (e.g. ```json ... ```)
                if (content.StartsWith("```json", StringComparison.OrdinalIgnoreCase)) content = content.Substring(7);
                else if (content.StartsWith("```", StringComparison.OrdinalIgnoreCase)) content = content.Substring(3);
                if (content.EndsWith("```", StringComparison.OrdinalIgnoreCase)) content = content.Substring(0, content.Length - 3);
                content = content.Trim();

                int firstBrace = content.IndexOf('{');
                int firstBracket = content.IndexOf('[');

                if (firstBracket >= 0 && (firstBrace < 0 || firstBracket < firstBrace))
                {
                    int lastBracket = content.LastIndexOf(']');
                    if (lastBracket > firstBracket)
                    {
                        content = content.Substring(firstBracket, lastBracket - firstBracket + 1);
                    }
                    else
                    {
                        content = content.Substring(firstBracket) + "]";
                    }
                }
                else if (firstBrace >= 0)
                {
                    int lastBrace = content.LastIndexOf('}');
                    if (lastBrace > firstBrace)
                    {
                        content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
                    }
                    else
                    {
                        content = content.Substring(firstBrace) + "}";
                    }
                }

                if (string.IsNullOrWhiteSpace(content)) continue;

                // Sanitize raw unescaped newlines/tabs inside double-quoted string literals before JSON parsing
                var sanitizedContent = SanitizeJsonControlCharacters(content);

                int parsedCountBefore = results.Count;

                try
                {
                    using var doc = JsonDocument.Parse(sanitizedContent, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in doc.RootElement.EnumerateArray())
                        {
                            var req = ProcessToolCallJsonElement(element);
                            if (req != null) results.Add(new ParsedCall(req, CanonicalActionType.ToolCall, "tool-json"));
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var req = ProcessToolCallJsonElement(doc.RootElement);
                        if (req != null) results.Add(new ParsedCall(req, CanonicalActionType.ToolCall, "tool-json"));
                    }
                }
                catch (JsonException jsonEx)
                {
                    logger?.LogWarning(jsonEx, "JsonDocument parsing failed for <tool_call>; attempting fallback extraction.");
                }

                // Fallback loose regex extraction for name and arguments if JsonDocument parsing produced 0 new requests
                if (results.Count == parsedCountBefore)
                {
                    var nameMatch = Regex.Match(content, @"""(?:name|function|tool|action)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (nameMatch.Success)
                    {
                        var fallbackName = nameMatch.Groups[1].Value;
                        var fallbackArgs = new Dictionary<string, object>();

                        var argMatches = Regex.Matches(content, @"""([a-zA-Z0-9_]+)""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
                        foreach (Match m in argMatches)
                        {
                            var key = m.Groups[1].Value;
                            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("function", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("action", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("type", StringComparison.OrdinalIgnoreCase)) continue;
                            var val = m.Groups[2].Value;
                            fallbackArgs[key] = val;
                        }

                        if (!string.IsNullOrEmpty(fallbackName))
                        {
                            results.Add(new ParsedCall(new ToolCallRequest(fallbackName, fallbackArgs), CanonicalActionType.ToolCall, "tool-json"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to parse <tool_call> JSON");
            }
        }

        return results;
    }

    /// <summary>
    /// Parses the model-independent JSON action envelope — a standalone JSON object whose
    /// first field is "action":
    ///   {"action":"tool_call","name":"...","arguments":{...}}
    ///   {"action":"completion_claim","summary":"..."}
    ///   {"action":"replan","items":["..."]}
    ///   {"action":"plan","items":["..."]}   (legacy variant, normalized to the plan tool)
    /// Returns null when the response is not a valid envelope so callers fall through to the
    /// legacy format parsers. "message" actions are plain text and never become tool calls.
    /// P1.6b: the canonical TYPE is preserved (CompletionClaim / Replan) instead of being
    /// flattened into a generic tool call — the runtime must never re-infer semantics from
    /// tool names.
    /// </summary>
    private static ParsedCall? TryParseJsonActionEnvelope(string response)
    {
        var match = Regex.Match(response,
            @"\{\s*""action""\s*:\s*""(tool_call|completion_claim|replan|plan|message)""",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        string action = match.Groups[1].Value.ToLowerInvariant();
        if (action == "message") return null; // plain text, not a tool call

        try
        {
            // Extract the EXACT JSON object with a balanced-brace scan. The old regex matched
            // to the FIRST '}' — an envelope whose arguments contain nested objects
            // ({"action":"tool_call","arguments":{"path":"a.txt"}}) lost its closing brace,
            // failed to parse, and fell through to the loose JSON heuristics, which misread
            // the "action" key as the tool name (a phantom "tool_call" tool call).
            string? raw = ExtractBalancedJsonObject(response, match.Index);
            if (raw == null) return null;
            raw = SanitizeJsonControlCharacters(raw);
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (action == "tool_call")
            {
                // Reuses the shared JSON mapping (name/tool + arguments).
                var req = ProcessToolCallJsonElement(doc.RootElement);
                return req == null ? null : new ParsedCall(req, CanonicalActionType.ToolCall, "json-envelope");
            }

            // completion_claim → CompletionClaim (task_complete); replan AND its legacy
            // "plan" variant → Replan (plan tool, action=create), so
            // {"action":"plan","items":[...]} executes instead of falling through every
            // format parser and dying as a no-action repair.
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
            {
                args["summary"] = summary.GetString() ?? string.Empty;
            }
            if (action is "replan" or "plan")
            {
                args["action"] = "create";
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var it in items.EnumerateArray())
                    {
                        list.Add(it.ToString());
                    }
                    if (list.Count > 0) args["items"] = string.Join("\n", list);
                }
            }

            string toolName = action == "completion_claim" ? "task_complete" : "plan";
            var type = action == "completion_claim" ? CanonicalActionType.CompletionClaim : CanonicalActionType.Replan;
            return new ParsedCall(new ToolCallRequest(toolName, args), type, "json-envelope");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the exact JSON object that starts at <paramref name="startIndex"/> (the index
    /// of the opening '{'), tracking string literals and brace depth until the matching close.
    /// Null when the object is unterminated. Used by the envelope parser so nested objects in
    /// arguments never truncate the capture.
    /// </summary>
    private static string? ExtractBalancedJsonObject(string text, int startIndex)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else
            {
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{': depth++; break;
                    case '}':
                        depth--;
                        if (depth == 0) return text.Substring(startIndex, i - startIndex + 1);
                        break;
                }
            }
        }
        return null;
    }

    private static ToolCallRequest? ProcessToolCallJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        JsonElement targetElement = element;
        if (element.TryGetProperty("function", out var funcObj) && funcObj.ValueKind == JsonValueKind.Object)
        {
            targetElement = funcObj;
        }

        string? name = null;
        if (targetElement.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String) name = nameProp.GetString();
        else if (targetElement.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.String) name = fnProp.GetString();
        else if (targetElement.TryGetProperty("tool", out var toolProp) && toolProp.ValueKind == JsonValueKind.String) name = toolProp.GetString();
        else if (targetElement.TryGetProperty("action", out var actionProp) && actionProp.ValueKind == JsonValueKind.String) name = actionProp.GetString();

        if (string.IsNullOrEmpty(name)) return null;

        var args = new Dictionary<string, object>();
        JsonElement argsProp = default;
        bool foundArgsProp = false;

        foreach (var propName in new[] { "arguments", "parameters", "args", "params", "action_input" })
        {
            if (targetElement.TryGetProperty(propName, out argsProp))
            {
                foundArgsProp = true;
                break;
            }
        }

        if (foundArgsProp)
        {
            if (argsProp.ValueKind == JsonValueKind.Object)
            {
                var rawDict = JsonSerializer.Deserialize<Dictionary<string, object>>(argsProp.GetRawText());
                if (rawDict != null) args = UnwrapArgs(rawDict);
            }
            else if (argsProp.ValueKind == JsonValueKind.String)
            {
                var str = argsProp.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    var sanitizedStr = SanitizeJsonControlCharacters(str);
                    try
                    {
                        var rawDict = JsonSerializer.Deserialize<Dictionary<string, object>>(sanitizedStr);
                        if (rawDict != null) args = UnwrapArgs(rawDict);
                    }
                    catch
                    {
                        // Ignore string parse errors
                    }
                }
            }
        }
        else
        {
            foreach (var prop in targetElement.EnumerateObject())
            {
                var pName = prop.Name.ToLowerInvariant();
                if (pName == "name" || pName == "function" || pName == "tool" || pName == "action" || pName == "type")
                    continue;

                var val = ToolExecutor.UnwrapJsonElement(prop.Value);
                if (val != null)
                {
                    args[prop.Name] = val;
                }
            }
        }

        return new ToolCallRequest(name, args);
    }

    private static string SanitizeJsonControlCharacters(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (isEscaped)
                {
                    sb.Append(c);
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    sb.Append(c);
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    sb.Append(c);
                    inString = false;
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else if (c < 0x20)
                {
                    sb.Append($"\\u{(int)c:x4}");
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, object> UnwrapArgs(Dictionary<string, object> rawArgs)
    {
        var result = new Dictionary<string, object>();
        foreach (var kvp in rawArgs)
        {
            var val = ToolExecutor.UnwrapJsonElement(kvp.Value);
            if (val != null)
            {
                result[kvp.Key] = val;
            }
        }
        return result;
    }

    /// <summary>
    /// Parses a qwen native &lt;parameter&gt; value: JSON objects/arrays (rendered by the template
    /// with tojson) become parsed values; anything else stays a plain string.
    /// </summary>
    private static object? TryParseQwenNativeJsonValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return null; // scalar: keep as string
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions { AllowTrailingCommas = true });
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="text"/> with the CONTENTS of every fenced code block
    /// whose language label is not "json" replaced by spaces (content lengths preserved, so
    /// line structure is intact). Blocks labeled json — or unlabeled bare ``` fences, which
    /// some fine-tunes use for their tool JSON — are kept verbatim. Used ONLY by the layer-4
    /// raw-JSON fallback; every explicit dialect above it still parses the original response.
    /// </summary>
    private static string MaskNonJsonFences(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (true)
        {
            int open = text.IndexOf("```", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, open - i);

            int close = text.IndexOf("```", open + 3, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(text, open, text.Length - open);
                break;
            }

            string fence = text.Substring(open + 3, close - open - 3);
            int langEnd = 0;
            while (langEnd < fence.Length && !char.IsWhiteSpace(fence[langEnd])) langEnd++;
            string lang = fence.Substring(0, langEnd);

            bool isJson = lang.Length == 0 || lang.Equals("json", StringComparison.OrdinalIgnoreCase);
            sb.Append(isJson ? fence : new string(' ', fence.Length));
            i = close + 3;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the first non-empty captured group of <paramref name="m"/>, in group-number
    /// order. Used by the tolerant native-format regexes, where the same semantic value may be
    /// captured by any one of several alternatives (quoted / unquoted / '=' / bare).
    /// </summary>
    internal static string FirstNonEmpty(Match m, params int[] groups)
    {
        foreach (int g in groups)
        {
            if (m.Groups[g].Success && m.Groups[g].Value.Length > 0)
            {
                return m.Groups[g].Value.Trim();
            }
        }
        return string.Empty;
    }
}
