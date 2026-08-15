using System;

namespace Klydis.Core.Inference;

/// <summary>
/// Tool-call format the grammar gate understands. Grammar constraining is only sound when
/// the model's chat template emits a known, structural calling format — today that is the
/// qwen-native <c>&lt;tool_call&gt;&lt;function=...&gt;&lt;parameter=...&gt;</c> syntax.
/// Other formats stay free-form (the wrapper is a passthrough).
/// </summary>
public enum ToolCallGrammarFormat
{
    /// <summary>No grammar constraint; the wrapper is a transparent passthrough.</summary>
    None = 0,

    /// <summary>qwen35 / qwen35moe native format: <c>&lt;tool_call&gt;&lt;function=N&gt;&lt;parameter=K&gt;v&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;</c>.</summary>
    QwenNative = 1
}

/// <summary>
/// Pure-managed helpers for GBNF tool-call grammars: opener detection and grammar text.
/// Kept separate from the sampling pipeline so the trigger logic is unit-testable without
/// any native llama.cpp involvement.
/// </summary>
public static class ToolCallGrammar
{
    /// <summary>
    /// Openers that switch a generation into grammar-constrained mode. Must stay aligned
    /// with the formats <see cref="ParseToolCalls"/> in ChatEngine recognizes.
    /// </summary>
    public static readonly string[] QwenNativeOpeners =
    {
        "<tool_call",
        "<|tool_call|",
        "[TOOL_CALLS",
        "[TOOL_CALL"
    };

    /// <summary>
    /// True when the accumulated generation text has begun a tool-call block for the given
    /// format. Once the gate flips it stays flipped for the rest of the generation.
    /// </summary>
    public static bool IsToolCallOpener(string text, ToolCallGrammarFormat format)
    {
        if (string.IsNullOrEmpty(text) || format == ToolCallGrammarFormat.None) return false;

        foreach (var opener in QwenNativeOpeners)
        {
            if (text.Contains(opener, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the GBNF grammar for the qwen native tool-call format.
    ///
    /// Deliberately permissive: it constrains only the tag skeleton — the parts that make
    /// the call unparseable today (empty/malformed function names, missing tags, garbage
    /// mid-call). Values are free-form (<c>[^\x00]</c> single chars), the closing
    /// <c>&lt;/tool_call&gt;</c> is optional (matching what the parser already tolerates),
    /// and <c>rest</c> keeps any post-call free text unconstrained — so a model that emits
    /// a tool call followed by more prose is never dead-ended by the grammar.
    ///
    /// NOTE: GBNF matches byte-wise against the tokenizer's pieces; this grammar is applied
    /// from the moment the opener is seen. Validate against a real qwen model before
    /// enabling <see cref="InferenceEngine.EnableToolGrammarConstrainedDecoding"/>.
    /// </summary>
    public static string BuildQwenNativeGbnf() =>
        "root       ::= tool_calls rest\n" +
        "tool_calls ::= tool_call*\n" +
        "tool_call  ::= \"<tool_call>\" ws function ws ( \"</tool_call>\" ws )?\n" +
        "function   ::= \"<function=\" name \">\" parameter* \"</function>\"\n" +
        "parameter  ::= \"<parameter=\" name \">\" value* \"</parameter>\"\n" +
        "name       ::= [a-zA-Z0-9_.-]+\n" +
        "value      ::= [^\\x00]\n" +
        "ws         ::= [ \\t\\n]*\n" +
        "rest       ::= [^\\x00]*";
}
