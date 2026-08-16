using System;
using System.Text.RegularExpressions;

namespace Klydis.Core.Chat;

/// <summary>
/// What a model generation actually produced, independent of HOW it expressed it. The
/// runtime decides this — the model never gets to choose what its output means. Autonomous
/// mode uses <see cref="NoAction"/> to enforce the invariant that a text-only response is
/// NOT a successful turn (the observed failure: the model greets/asks permission instead of
/// entering the tool protocol).
/// </summary>
public enum ToolActionKind
{
    /// <summary>A parseable tool invocation in any supported format (native tags, JSON envelope, &lt;tool_call&gt; JSON).</summary>
    ToolCall,

    /// <summary>A completion claim (task_complete / {"action":"completion_claim"}).</summary>
    CompletionClaim,

    /// <summary>A plan revision request (plan action=create / {"action":"replan"}).</summary>
    Replan,

    /// <summary>Natural language that performs no action — a protocol failure in Autonomous mode.</summary>
    NoAction
}

/// <summary>
/// Model-independent action classification (protocol seam). Classifies a think-stripped
/// model response into a <see cref="ToolActionKind"/> by detecting structured call syntax in
/// every format the harness accepts — the JSON action envelope, native tool tags, and the
/// &lt;tool_call&gt; JSON form. Everything else is <see cref="NoAction"/>.
/// </summary>
public static class ToolActionParser
{
    /// <summary>
    /// Classifies a model response into an action kind. The response should already be
    /// think-stripped (reasoning must never drive an action decision).
    /// </summary>
    public static ToolActionKind Classify(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return ToolActionKind.NoAction;

        // JSON action envelope — explicit, validated protocol first
        // ({"action":"tool_call"|"completion_claim"|"replan"|"message", ...}).
        var envelope = Regex.Match(response,
            @"\{\s*""action""\s*:\s*""(tool_call|completion_claim|replan|message)""",
            RegexOptions.IgnoreCase);
        if (envelope.Success)
        {
            return envelope.Groups[1].Value.ToLowerInvariant() switch
            {
                "completion_claim" => ToolActionKind.CompletionClaim,
                "replan" => ToolActionKind.Replan,
                "tool_call" => ToolActionKind.ToolCall,
                _ => ToolActionKind.NoAction // "message" = plain text
            };
        }

        // Structured tool tags: qwen native, anthropic antml, or <tool_call> JSON.
        if (Regex.IsMatch(response, @"<\|?tool_calls?\|?>", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(response, @"<antml:invoke", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(response, @"<function\b", RegexOptions.IgnoreCase))
        {
            if (IsToolNamed(response, "task_complete"))
            {
                return ToolActionKind.CompletionClaim;
            }
            if (IsToolNamed(response, "plan"))
            {
                return ToolActionKind.Replan;
            }
            return ToolActionKind.ToolCall;
        }

        // [TOOL_CALLS] bracket form and markdown ```json blocks with call keys.
        if (Regex.IsMatch(response, @"\[TOOL_CALLS?\]", RegexOptions.IgnoreCase) ||
            (Regex.IsMatch(response, @"```(?:json)?", RegexOptions.IgnoreCase) &&
             Regex.IsMatch(response, @"""(?:name|tool|function)"":", RegexOptions.IgnoreCase)))
        {
            return ToolActionKind.ToolCall;
        }

        return ToolActionKind.NoAction;
    }

    /// <summary>
    /// True when a text-only response is a REFUSAL or conversational filler instead of task
    /// progress — the exact failure pattern from the live export (greetings, permission
    /// seeking, "I am an internal text-only agent", "I cannot proceed beyond a generic
    /// greeting"). Autonomous mode repairs these; a long structured text response (a report,
    /// a doc) is a legitimate deliverable and is never treated as a refusal.
    /// </summary>
    public static bool IsActionRefusal(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        string r = response.ToLowerInvariant();
        string[] markers =
        {
            // Greetings / conversational resets.
            "good morning", "good afternoon", "good evening", "good night",
            "how are you", "how are you going", "how have you been",
            // Permission seeking / asking what to do next.
            "how can i help", "how may i help", "what would you like", "what do you want",
            "what can i do", "please tell me", "let me know what", "tell me what you",
            "let me know how", "do you want me to", "would you like me to",
            "do you expect me to", "how are you planning", "what are you planning",
            "how do you plan to", "what should i do", "please clarify",
            "could you clarify", "can you clarify", "what would you have me",
            // Self-description as incapable / text-only.
            "i am an internal", "i'm an internal", "i am only", "i'm only",
            "i am a text-only", "i'm a text-only", "text-only agent", "text only agent",
            "i am not able to", "i'm not able to", "i cannot", "i can't",
            "i am unable", "i'm unable", "cannot proceed", "can't proceed"
        };
        foreach (var m in markers)
        {
            if (r.Contains(m, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsToolNamed(string response, string toolName)
    {
        // <tool_call>{"name":"task_complete", ...}</tool_call> or native <function=task_complete>.
        return Regex.IsMatch(response, @"""name""\s*:\s*""" + Regex.Escape(toolName) + @"""", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(response, @"<function\s*(?:=|\s+name\s*=)\s*""" + Regex.Escape(toolName) + @"""", RegexOptions.IgnoreCase);
    }
}
