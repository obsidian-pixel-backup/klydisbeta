using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Klydis.Core.Chat;
using Klydis.Core.Inference.Telemetry;
using ChatMessage = Klydis.Core.Chat.ChatMessage;
using ChatRole = Klydis.Core.Chat.ChatRole;

namespace Klydis.Core.Inference.Providers;

/// <summary>
/// Identifies the runtime classification of an inference provider.
/// </summary>
public enum ProviderType
{
    InProcessGguf,
    OpenAi,
    Anthropic,
    DeepSeek,
    Gemini,
    OpenAiCompatibleLocal,
    Custom
}

/// <summary>
/// Tool calling mode preferences.
/// </summary>
public enum ToolChoiceMode
{
    Auto,
    None,
    Required,
    Specific
}

/// <summary>
/// Structured output response format type.
/// </summary>
public enum ResponseFormatType
{
    Text,
    JsonObject,
    JsonSchema
}

/// <summary>
/// Structured output response format specification.
/// </summary>
public sealed record ResponseFormatPreference(
    ResponseFormatType Type = ResponseFormatType.Text,
    string? SchemaName = null,
    string? JsonSchema = null,
    bool Strict = true
);

/// <summary>
/// Hardware, model, or cloud API capabilities exposed by a provider.
/// </summary>
public sealed record ProviderCapabilities(
    bool SupportsStreaming = true,
    bool SupportsTools = true,
    bool SupportsParallelToolCalls = true,
    bool SupportsStructuredOutputs = true,
    bool SupportsVision = false,
    bool SupportsThinkingBudget = false,
    bool SupportsPromptCaching = false,
    int MaxContextTokens = 128000,
    int MaxOutputTokens = 8192,
    decimal CostPerMillionInputTokens = 0m,
    decimal CostPerMillionOutputTokens = 0m,
    decimal CostPerMillionCachedInputTokens = 0m
);

/// <summary>
/// Metadata descriptor for a model hosted or accessible via a provider.
/// </summary>
public sealed record RemoteModelDescriptor(
    string ModelId,
    string DisplayName,
    string ProviderId,
    int ContextWindowTokens,
    int MaxOutputTokens,
    bool SupportsThinking,
    bool SupportsTools,
    bool SupportsVision,
    decimal InputPricePerMillion = 0m,
    decimal OutputPricePerMillion = 0m,
    IReadOnlyList<string>? SupportedModalities = null
);

/// <summary>
/// Fine-grained token usage metrics returned by providers.
/// </summary>
public sealed record TokenUsageMetrics(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int ReasoningTokens = 0,
    int CacheCreationInputTokens = 0,
    int CacheReadInputTokens = 0
)
{
    public static TokenUsageMetrics Empty => new(0, 0, 0);
}

/// <summary>
/// Streaming tool call delta fragment.
/// </summary>
public sealed record ToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string? ArgumentsDelta
);

/// <summary>
/// Unified streaming chunk emitted across all provider backends.
/// </summary>
public sealed record ChatChunk(
    string RequestId,
    string? ContentDelta = null,
    string? ReasoningDelta = null,
    IReadOnlyList<ToolCallDelta>? ToolCallDeltas = null,
    string? FinishReason = null,
    TokenUsageMetrics? CumulativeUsage = null,
    TimeSpan Elapsed = default,
    bool IsFirstToken = false
);

/// <summary>
/// Unified request model submitted to any inference provider.
/// </summary>
public sealed record ProviderInferenceRequest
{
    public required string ModelId { get; init; }
    public required IReadOnlyList<Klydis.Core.Chat.ChatMessage> Messages { get; init; }
    public string? SystemPrompt { get; init; }
    public int? MaxTokens { get; init; }
    public float Temperature { get; init; } = 0.7f;
    public float TopP { get; init; } = 0.9f;
    public int? ThinkingBudgetTokens { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public ToolChoiceMode ToolChoice { get; init; } = ToolChoiceMode.Auto;
    public string? SpecificToolName { get; init; }
    public ResponseFormatPreference ResponseFormat { get; init; } = new(ResponseFormatType.Text);
    public bool Stream { get; init; } = true;
    public string? RequestId { get; init; }
    public string? SessionId { get; init; }
    public IReadOnlyDictionary<string, object>? CustomParameters { get; init; }
}

/// <summary>
/// Unified response model returned from non-streaming generation or aggregated streams.
/// </summary>
public sealed record ProviderInferenceResponse(
    string ResponseId,
    string ModelId,
    string ProviderId,
    string TextContent,
    string? ReasoningContent,
    IReadOnlyList<ToolCallRequest>? ToolCalls,
    string FinishReason,
    TokenUsageMetrics Usage,
    InferenceTelemetry Telemetry,
    IReadOnlyDictionary<string, object>? RawMetadata = null
);

/// <summary>
/// Provider health state descriptor.
/// </summary>
public sealed record ProviderHealthStatus(
    bool IsHealthy,
    TimeSpan Latency,
    string? StatusMessage = null,
    DateTime CheckedAtUtc = default
);

/// <summary>
/// Strongly-typed provider configuration record.
/// </summary>
public sealed record ProviderConfig(
    string ProviderId,
    ProviderType Type,
    string? ApiKey = null,
    string? BaseUrl = null,
    string? OrganizationId = null,
    string? DefaultModelId = null,
    bool IsEnabled = true,
    int Priority = 0,
    int MaxRetries = 3
);

/// <summary>
/// Base exception for inference provider operations.
/// </summary>
public class ProviderException : Exception
{
    public string ProviderId { get; }

    public ProviderException(string providerId, string message, Exception? innerException = null)
        : base($"[{providerId}] {message}", innerException)
    {
        ProviderId = providerId;
    }
}

/// <summary>
/// Exception thrown when rate limits or quotas are exceeded on a provider backend.
/// </summary>
public sealed class ProviderRateLimitException : ProviderException
{
    public TimeSpan? RetryAfter { get; }

    public ProviderRateLimitException(string providerId, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(providerId, message, innerException)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>
/// Exception thrown when a provider returns a 5xx server error or upstream outage.
/// </summary>
public sealed class ProviderServerException : ProviderException
{
    public int? StatusCode { get; }

    public ProviderServerException(string providerId, int? statusCode, string message, Exception? innerException = null)
        : base(providerId, $"HTTP {statusCode}: {message}", innerException)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Exception thrown when provider credentials/API key are missing or invalid.
/// </summary>
public sealed class ProviderAuthenticationException : ProviderException
{
    public ProviderAuthenticationException(string providerId, string message, Exception? innerException = null)
        : base(providerId, message, innerException)
    {
    }
}
