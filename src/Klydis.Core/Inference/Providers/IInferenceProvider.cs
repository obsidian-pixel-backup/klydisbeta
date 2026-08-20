using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Klydis.Core.Inference.Providers;

/// <summary>
/// Core contract for any inference backend (cloud API, local server, or in-process engine).
/// </summary>
public interface IInferenceProvider : IDisposable, IAsyncDisposable
{
    /// <summary>Unique identifier of the provider instance (e.g. "openai-main", "anthropic", "ollama-local").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable display name (e.g. "OpenAI Official", "Claude 3.7", "Local Ollama Instance").</summary>
    string DisplayName { get; }

    /// <summary>Provider backend classification.</summary>
    ProviderType ProviderType { get; }

    /// <summary>Static and dynamic capabilities of the provider backend.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Executes a non-streaming chat completion request.
    /// </summary>
    Task<ProviderInferenceResponse> GenerateAsync(
        ProviderInferenceRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Executes a real-time streaming chat completion request, yielding token, thinking, and tool deltas.
    /// </summary>
    IAsyncEnumerable<ChatChunk> GenerateStreamAsync(
        ProviderInferenceRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Estimates the token count of a given string using provider-native or heuristic tokenizers.
    /// </summary>
    Task<int> EstimateTokensAsync(
        string text,
        string? modelId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validates provider credentials and endpoint connectivity.
    /// </summary>
    Task<bool> ValidateCredentialsAsync(CancellationToken ct = default);

    /// <summary>
    /// Discovers and lists all available models on this provider backend.
    /// </summary>
    Task<IReadOnlyList<RemoteModelDescriptor>> ListAvailableModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs an active health check against the provider endpoint.
    /// </summary>
    Task<ProviderHealthStatus> CheckHealthAsync(CancellationToken ct = default);
}
