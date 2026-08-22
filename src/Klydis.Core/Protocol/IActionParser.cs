using System;
using System.Collections.Generic;
using Klydis.Core.Chat;

namespace Klydis.Core.Protocol;

/// <summary>
/// Status outcome of parsing model output for executable actions.
/// </summary>
public enum ActionParseStatus
{
    /// <summary>Output contains one or more valid, executable tool calls.</summary>
    ValidToolCall,

    /// <summary>Output attempted an unknown, hallucinated, or malformed tool call.</summary>
    InvalidToolCall,

    /// <summary>Output contains an explicit completion claim (e.g. task_complete or completion envelope).</summary>
    CompletionClaim,

    /// <summary>Output contains a plan revision action (e.g. plan tool or replan envelope).</summary>
    Replan,

    /// <summary>Output contains natural language text with no tool calls or action tags.</summary>
    NoAction,

    /// <summary>Output attempted a tool call tag but was structurally malformed and unparseable.</summary>
    Malformed
}

/// <summary>
/// Result of action parsing on model output.
/// </summary>
public sealed record ActionParseResult(
    ActionParseStatus Status,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    IReadOnlyList<CanonicalAction> CanonicalActions,
    string? RejectionReason = null,
    string? SuggestedRepair = null)
{
    public bool HasValidActions => Status is ActionParseStatus.ValidToolCall or ActionParseStatus.CompletionClaim or ActionParseStatus.Replan;
}

/// <summary>
/// Authoritative interface for parsing, normalizing, and validating model actions.
/// </summary>
public interface IActionParser
{
    /// <summary>
    /// Parses think-stripped model output into a validated <see cref="ActionParseResult"/>.
    /// </summary>
    ActionParseResult Parse(string? response, IReadOnlyList<ToolDefinition>? availableTools = null);
}
