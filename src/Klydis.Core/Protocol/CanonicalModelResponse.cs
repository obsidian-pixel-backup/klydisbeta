using System;
using System.Collections.Generic;
using System.Linq;
using Klydis.Core.Chat;

namespace Klydis.Core.Protocol;

/// <summary>
/// The primary classification of a raw model generation.
/// Downstream orchestration reasons over this single classification instead of
/// guessing intent from unconstrained text.
/// </summary>
public enum ResponseClassification
{
    /// <summary>Plain conversational response containing no tool invocations or completion claims.</summary>
    TextOnly,

    /// <summary>A single executable tool invocation.</summary>
    ToolCall,

    /// <summary>Multiple tool invocations in a single turn (for models supporting parallel execution).</summary>
    ToolCallBatch,

    /// <summary>A definitive final answer or task conclusion.</summary>
    FinalAnswer,

    /// <summary>A clarifying question or request for user input.</summary>
    ClarificationRequest,

    /// <summary>An unparseable or protocol-violating response (e.g. malformed JSON, pseudo-code pretending to be tools).</summary>
    Invalid,

    /// <summary>A partial generation cut short by token budget that requires runtime continuation.</summary>
    ContinuationRequired
}

/// <summary>
/// Fine-grained usage metrics for a generation step.
/// </summary>
public sealed record GenerationUsageMetrics(
    int PromptTokens,
    int CompletionTokens,
    int ThinkingTokens,
    int TotalTokens,
    TimeSpan ElapsedTime)
{
    public static GenerationUsageMetrics Empty => new(0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// The canonical model response produced by all protocol adapters.
/// Decouples downstream execution from Qwen-, Anthropic-, or OpenAI-specific raw formats.
/// </summary>
public sealed record CanonicalModelResponse(
    string? Text,
    string? ThinkingContent,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    ResponseClassification Classification,
    string FinishReason,
    GenerationUsageMetrics Usage,
    bool IsValid)
{
    /// <summary>True when this response contains at least one executable tool call.</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>
    /// Classifies a parsed model output into a single canonical ResponseClassification.
    /// </summary>
    public static ResponseClassification Classify(
        string? text,
        IReadOnlyList<ToolCallRequest>? toolCalls,
        bool hitMaxTokens,
        bool isInvalidProtocol = false)
    {
        if (hitMaxTokens)
        {
            return ResponseClassification.ContinuationRequired;
        }

        if (isInvalidProtocol)
        {
            return ResponseClassification.Invalid;
        }

        if (toolCalls != null && toolCalls.Count > 0)
        {
            return toolCalls.Count == 1 ? ResponseClassification.ToolCall : ResponseClassification.ToolCallBatch;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return ResponseClassification.Invalid;
        }

        var trimmed = text.Trim();
        // Check for clarification indicators
        if (trimmed.EndsWith('?') && (trimmed.StartsWith("Would you like", StringComparison.OrdinalIgnoreCase) ||
                                      trimmed.StartsWith("Could you please", StringComparison.OrdinalIgnoreCase) ||
                                      trimmed.StartsWith("Should I", StringComparison.OrdinalIgnoreCase) ||
                                      trimmed.StartsWith("Please specify", StringComparison.OrdinalIgnoreCase)))
        {
            return ResponseClassification.ClarificationRequest;
        }

        return ResponseClassification.TextOnly;
    }
}
