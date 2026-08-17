using System.Collections.Generic;
using Klydis.Core.Chat;

namespace Klydis.Core.Protocol;

/// <summary>
/// The model-independent action types. Every model dialect — qwen-native tags, JSON
/// envelopes, antml invokes, &lt;tool_call&gt; JSON — normalizes into one of these, so the
/// agent runtime never needs to know which dialect a model spoke.
/// </summary>
public enum CanonicalActionType
{
    /// <summary>Plain text: a message, not an action.</summary>
    Message,

    /// <summary>A tool invocation (plan mutations are ToolCall with tool="plan").</summary>
    ToolCall,

    /// <summary>A plan revision request.</summary>
    Replan,

    /// <summary>A completion claim (task_complete).</summary>
    CompletionClaim,

    /// <summary>The model reports it cannot proceed.</summary>
    Blocked
}

/// <summary>
/// A normalized model action. This is the seam of the P1 protocol layer: model-specific
/// parsers produce these; the runtime consumes only these.
/// </summary>
public sealed record CanonicalAction(
    CanonicalActionType Type,
    string? ToolName,
    string? ArgumentsJson,
    string? Text,
    string? Reason,
    string SourceProtocol,
    IDictionary<string, object>? Arguments = null)
{
    /// <summary>
    /// True when the action is a tool invocation of the given tool — including completion
    /// claims, which are semantically a task_complete call.
    /// </summary>
    public bool IsTool(string toolName)
        => (Type == CanonicalActionType.ToolCall || Type == CanonicalActionType.CompletionClaim) &&
           string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Normalizes raw model output into a <see cref="CanonicalAction"/> by reusing the existing
/// deterministic classifier (<see cref="ToolActionParser.Classify"/>) — the same source the
/// no-action gate uses, so classification and normalization can never disagree. The
/// per-model dialect parsing (Qwen/JSON/antml) remains in ChatEngine.ParseToolCalls; P1
/// moves those behind IModelProtocol adapters and this normalizer becomes their common
/// output shape.
/// </summary>
public static class CanonicalActionNormalizer
{
    /// <summary>
    /// Classifies think-stripped model output into a canonical action. Returns a Message
    /// action when the output is text only (never throws).
    /// </summary>
    public static CanonicalAction Normalize(string? rawOutput, string sourceProtocol = "generic")
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return new CanonicalAction(CanonicalActionType.Message, null, null, string.Empty, null, sourceProtocol);
        }

        return ToolActionParser.Classify(rawOutput) switch
        {
            ToolActionKind.ToolCall => new CanonicalAction(
                CanonicalActionType.ToolCall,
                ExtractToolName(rawOutput),
                ExtractArgumentsJson(rawOutput),
                null, null, sourceProtocol),
            ToolActionKind.CompletionClaim => new CanonicalAction(
                CanonicalActionType.CompletionClaim,
                "task_complete", null, null, null, sourceProtocol),
            ToolActionKind.Replan => new CanonicalAction(
                CanonicalActionType.Replan,
                "plan", null, null, null, sourceProtocol),
            _ => new CanonicalAction(CanonicalActionType.Message, null, null, rawOutput, null, sourceProtocol)
        };
    }

    private static string? ExtractToolName(string response)
    {
        // The classifier already established a structured call is present; find the tool
        // name with the same tolerant patterns the execution parser uses.
        var m = System.Text.RegularExpressions.Regex.Match(response,
            @"""name""\s*:\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(response,
            @"<function\s*(?:=|\s+name\s*=)\s*""?([a-zA-Z0-9_.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(response,
            @"""tool""\s*:\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractArgumentsJson(string response)
    {
        var m = System.Text.RegularExpressions.Regex.Match(response,
            @"""arguments""\s*:\s*(\{[^{}]*\})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
