using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference.Providers;

/// <summary>
/// Base class providing shared HTTP communication, SSE parsing, and resilience hooks for REST-based providers.
/// </summary>
public abstract class HttpInferenceProviderBase : IInferenceProvider
{
    protected readonly HttpClient HttpClient;
    protected readonly ILogger Logger;
    protected readonly ProviderConfig Config;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    protected HttpInferenceProviderBase(
        HttpClient httpClient,
        ProviderConfig config,
        ILogger logger)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public virtual string ProviderId => Config.ProviderId;
    public abstract string DisplayName { get; }
    public abstract ProviderType ProviderType { get; }
    public abstract ProviderCapabilities Capabilities { get; }

    public abstract Task<ProviderInferenceResponse> GenerateAsync(
        ProviderInferenceRequest request,
        CancellationToken ct = default);

    public abstract IAsyncEnumerable<ChatChunk> GenerateStreamAsync(
        ProviderInferenceRequest request,
        CancellationToken ct = default);

    public virtual Task<int> EstimateTokensAsync(
        string text,
        string? modelId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(0);

        // Standard subword heuristic: ~3.7 characters per token for English / code
        int estimated = Math.Max(1, (int)Math.Ceiling(text.Length / 3.7));
        return Task.FromResult(estimated);
    }

    public virtual async Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await CheckHealthAsync(ct).ConfigureAwait(false);
            return status.IsHealthy;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{ProviderId}] Credential validation failed", ProviderId);
            return false;
        }
    }

    public abstract Task<IReadOnlyList<RemoteModelDescriptor>> ListAvailableModelsAsync(CancellationToken ct = default);

    public virtual async Task<ProviderHealthStatus> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var models = await ListAvailableModelsAsync(ct).ConfigureAwait(false);
            sw.Stop();
            return new ProviderHealthStatus(
                IsHealthy: true,
                Latency: sw.Elapsed,
                StatusMessage: $"Healthy. {models.Count} models available.",
                CheckedAtUtc: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderHealthStatus(
                IsHealthy: false,
                Latency: sw.Elapsed,
                StatusMessage: ex.Message,
                CheckedAtUtc: DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Reads and parses an HTTP response stream emitting raw SSE data lines.
    /// </summary>
    protected async IAsyncEnumerable<string> ReadSseLinesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;

            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(':'))
            {
                // SSE heartbeat comment or empty keepalive line
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                string payload = line.Length > 5 ? line.Substring(5).Trim() : string.Empty;
                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!string.IsNullOrEmpty(payload))
                {
                    yield return payload;
                }
            }
            else if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                // Event type line, optionally used by Anthropic
                yield return line;
            }
        }
    }

    /// <summary>
    /// Checks for standard HTTP status code errors and throws typed ProviderException.
    /// </summary>
    protected static void EnsureSuccessStatusCode(HttpResponseMessage response, string responseBody, string providerId)
    {
        if (response.IsSuccessStatusCode)
            return;

        int statusCode = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                    retryAfter = response.Headers.RetryAfter.Delta.Value;
                else if (response.Headers.RetryAfter.Date.HasValue)
                    retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            }

            throw new ProviderRateLimitException(
                providerId,
                $"Rate limit exceeded (HTTP 429). Response: {responseBody}",
                retryAfter);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthenticationException(
                providerId,
                $"Authentication failed (HTTP {statusCode}). Verify API key / credentials. Response: {responseBody}");
        }

        if (statusCode >= 500)
        {
            throw new ProviderServerException(
                providerId,
                statusCode,
                $"Server error from provider endpoint (HTTP {statusCode}). Response: {responseBody}");
        }

        throw new ProviderException(
            providerId,
            $"HTTP request failed with status code {statusCode}: {responseBody}");
    }

    public virtual void Dispose()
    {
        // Don't dispose injected HttpClient if managed by IHttpClientFactory, but base handles singletons
    }

    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
