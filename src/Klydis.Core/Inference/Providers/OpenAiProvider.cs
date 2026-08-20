using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Klydis.Core.Chat;
using Klydis.Core.Inference.Telemetry;
using Microsoft.Extensions.Logging;
using ChatMessage = Klydis.Core.Chat.ChatMessage;
using ChatRole = Klydis.Core.Chat.ChatRole;

namespace Klydis.Core.Inference.Providers;

/// <summary>
/// Native OpenAI API inference provider (GPT-4o, o1, o3-mini) with structured output and tool streaming.
/// </summary>
public sealed class OpenAiProvider : HttpInferenceProviderBase
{
    private readonly string _baseUrl;

    public override string DisplayName => "OpenAI Official";
    public override ProviderType ProviderType => ProviderType.OpenAi;

    public override ProviderCapabilities Capabilities { get; } = new(
        SupportsStreaming: true,
        SupportsTools: true,
        SupportsParallelToolCalls: true,
        SupportsStructuredOutputs: true,
        SupportsVision: true,
        SupportsThinkingBudget: true,
        SupportsPromptCaching: true,
        MaxContextTokens: 128000,
        MaxOutputTokens: 16384,
        CostPerMillionInputTokens: 2.50m,
        CostPerMillionOutputTokens: 10.00m,
        CostPerMillionCachedInputTokens: 1.25m
    );

    public OpenAiProvider(
        HttpClient httpClient,
        ProviderConfig config,
        ILogger<OpenAiProvider> logger)
        : base(httpClient, config, logger)
    {
        _baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.openai.com/v1";
    }

    public override async Task<ProviderInferenceResponse> GenerateAsync(
        ProviderInferenceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        using var httpRequest = CreateHttpRequest(request, isStreaming: false);

        using var response = await HttpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        EnsureSuccessStatusCode(response, responseBody, ProviderId);
        sw.Stop();

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        string id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
        string model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? request.ModelId : request.ModelId;

        string finishReason = "stop";
        string contentText = string.Empty;
        string? reasoningContent = null;
        List<ToolCallRequest>? toolCalls = null;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("finish_reason", out var fr))
            {
                finishReason = fr.GetString() ?? "stop";
            }

            if (firstChoice.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    contentText = c.GetString() ?? string.Empty;
                }

                if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                {
                    reasoningContent = rc.GetString();
                }

                if (msg.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
                {
                    toolCalls = new List<ToolCallRequest>();
                    foreach (var tc in tcArray.EnumerateArray())
                    {
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            string fnName = fn.TryGetProperty("name", out var fnNameProp) ? fnNameProp.GetString() ?? string.Empty : string.Empty;
                            string fnArgs = fn.TryGetProperty("arguments", out var fnArgsProp) ? fnArgsProp.GetString() ?? "{}" : "{}";
                            
                            var parsedArgs = ParseToolArguments(fnArgs);
                            toolCalls.Add(new ToolCallRequest(fnName, parsedArgs));
                        }
                    }
                }
            }
        }

        var usage = ExtractTokenUsage(root);

        var telemetry = new InferenceTelemetry(
            RequestId: request.RequestId ?? id,
            TargetModelPath: model,
            PromptTokenCount: usage.PromptTokens,
            GeneratedTokenCount: usage.CompletionTokens,
            TimeToFirstTokenMs: sw.Elapsed.TotalMilliseconds,
            GenerationDurationMs: sw.Elapsed.TotalMilliseconds,
            TotalElapsedMs: sw.Elapsed.TotalMilliseconds,
            GenerationTokensPerSecond: usage.CompletionTokens > 0 ? usage.CompletionTokens / Math.Max(0.001, sw.Elapsed.TotalSeconds) : 0,
            PromptPrefillTokensPerSecond: usage.PromptTokens > 0 ? usage.PromptTokens / Math.Max(0.001, sw.Elapsed.TotalSeconds) : 0
        );

        return new ProviderInferenceResponse(
            ResponseId: id,
            ModelId: model,
            ProviderId: ProviderId,
            TextContent: contentText,
            ReasoningContent: reasoningContent,
            ToolCalls: toolCalls,
            FinishReason: finishReason,
            Usage: usage,
            Telemetry: telemetry
        );
    }

    public override async IAsyncEnumerable<ChatChunk> GenerateStreamAsync(
        ProviderInferenceRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = CreateHttpRequest(request, isStreaming: true);
        using var response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureSuccessStatusCode(response, err, ProviderId);
        }

        var sw = Stopwatch.StartNew();
        bool isFirstToken = true;
        string requestId = request.RequestId ?? Guid.NewGuid().ToString();

        // Dictionary to accumulate streaming tool call arguments by index
        var streamingToolCalls = new Dictionary<int, ToolCallAccumulator>();

        await foreach (var line in ReadSseLinesAsync(response, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp))
                {
                    requestId = idProp.GetString() ?? requestId;
                }

                TokenUsageMetrics? usage = null;
                if (root.TryGetProperty("usage", out var usageProp))
                {
                    usage = ExtractTokenUsage(root);
                }

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    string? finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

                    string? contentDelta = null;
                    string? reasoningDelta = null;
                    List<ToolCallDelta>? toolDeltas = null;

                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        {
                            contentDelta = c.GetString();
                        }

                        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        {
                            reasoningDelta = rc.GetString();
                        }

                        if (delta.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
                        {
                            toolDeltas = new List<ToolCallDelta>();
                            foreach (var tc in tcArray.EnumerateArray())
                            {
                                int idx = tc.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : 0;
                                string? tcId = tc.TryGetProperty("id", out var tcIdProp) ? tcIdProp.GetString() : null;
                                string? fnName = null;
                                string? argsDelta = null;

                                if (tc.TryGetProperty("function", out var fn))
                                {
                                    if (fn.TryGetProperty("name", out var nameProp))
                                        fnName = nameProp.GetString();
                                    if (fn.TryGetProperty("arguments", out var argsProp))
                                        argsDelta = argsProp.GetString();
                                }

                                if (!streamingToolCalls.TryGetValue(idx, out var acc))
                                {
                                    acc = new ToolCallAccumulator { Index = idx, Id = tcId, Name = fnName };
                                    streamingToolCalls[idx] = acc;
                                }

                                if (!string.IsNullOrEmpty(tcId)) acc.Id = tcId;
                                if (!string.IsNullOrEmpty(fnName)) acc.Name = fnName;
                                if (!string.IsNullOrEmpty(argsDelta)) acc.ArgumentsBuilder.Append(argsDelta);

                                toolDeltas.Add(new ToolCallDelta(idx, tcId, fnName, argsDelta));
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(contentDelta) || !string.IsNullOrEmpty(reasoningDelta) || toolDeltas?.Count > 0 || finishReason != null || usage != null)
                    {
                        yield return new ChatChunk(
                            RequestId: requestId,
                            ContentDelta: contentDelta,
                            ReasoningDelta: reasoningDelta,
                            ToolCallDeltas: toolDeltas,
                            FinishReason: finishReason,
                            CumulativeUsage: usage,
                            Elapsed: sw.Elapsed,
                            IsFirstToken: isFirstToken
                        );

                        if (isFirstToken && (!string.IsNullOrEmpty(contentDelta) || !string.IsNullOrEmpty(reasoningDelta)))
                        {
                            isFirstToken = false;
                        }
                    }
                }
                else if (usage != null)
                {
                    yield return new ChatChunk(
                        RequestId: requestId,
                        CumulativeUsage: usage,
                        Elapsed: sw.Elapsed,
                        IsFirstToken: false
                    );
                }
            }
        }
    }

    public override async Task<IReadOnlyList<RemoteModelDescriptor>> ListAvailableModelsAsync(CancellationToken ct = default)
    {
        string url = $"{_baseUrl}/models";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthenticationHeaders(req);

        using var response = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccessStatusCode(response, body, ProviderId);

        var list = new List<RemoteModelDescriptor>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                bool isReasoning = id.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || id.StartsWith("o3", StringComparison.OrdinalIgnoreCase);
                bool isGpt4 = id.Contains("gpt-4", StringComparison.OrdinalIgnoreCase);

                list.Add(new RemoteModelDescriptor(
                    ModelId: id,
                    DisplayName: id,
                    ProviderId: ProviderId,
                    ContextWindowTokens: isReasoning ? 200000 : 128000,
                    MaxOutputTokens: isReasoning ? 100000 : 16384,
                    SupportsThinking: isReasoning,
                    SupportsTools: true,
                    SupportsVision: isGpt4 || isReasoning,
                    InputPricePerMillion: isReasoning ? 15.0m : 2.50m,
                    OutputPricePerMillion: isReasoning ? 60.0m : 10.0m
                ));
            }
        }

        return list;
    }

    private HttpRequestMessage CreateHttpRequest(ProviderInferenceRequest request, bool isStreaming)
    {
        string url = $"{_baseUrl}/chat/completions";
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuthenticationHeaders(httpRequest);

        bool isReasoningModel = request.ModelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                                request.ModelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = FormatMessages(request, isReasoningModel),
            ["stream"] = isStreaming
        };

        if (isStreaming)
        {
            payload["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };
        }

        if (isReasoningModel)
        {
            if (request.MaxTokens.HasValue)
            {
                payload["max_completion_tokens"] = request.MaxTokens.Value;
            }

            if (request.ThinkingBudgetTokens.HasValue)
            {
                payload["reasoning_effort"] = request.ThinkingBudgetTokens.Value switch
                {
                    < 4000 => "low",
                    <= 16000 => "medium",
                    _ => "high"
                };
            }
        }
        else
        {
            payload["temperature"] = request.Temperature;
            payload["top_p"] = request.TopP;
            if (request.MaxTokens.HasValue)
            {
                payload["max_tokens"] = request.MaxTokens.Value;
            }
        }

        if (request.StopSequences != null && request.StopSequences.Count > 0)
        {
            payload["stop"] = request.StopSequences;
        }

        // Tools
        if (request.Tools != null && request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = FormatToolParameters(t.Parameters),
                    strict = request.ResponseFormat.Strict
                }
            }).ToList();

            payload["tool_choice"] = request.ToolChoice switch
            {
                ToolChoiceMode.None => "none",
                ToolChoiceMode.Required => "required",
                ToolChoiceMode.Specific when !string.IsNullOrEmpty(request.SpecificToolName) =>
                    new { type = "function", function = new { name = request.SpecificToolName } },
                _ => "auto"
            };
        }

        // Structured Outputs
        if (request.ResponseFormat.Type == ResponseFormatType.JsonSchema && !string.IsNullOrEmpty(request.ResponseFormat.JsonSchema))
        {
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = request.ResponseFormat.SchemaName ?? "structured_output",
                    strict = request.ResponseFormat.Strict,
                    schema = JsonDocument.Parse(request.ResponseFormat.JsonSchema).RootElement
                }
            };
        }
        else if (request.ResponseFormat.Type == ResponseFormatType.JsonObject)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        if (request.CustomParameters != null)
        {
            foreach (var kvp in request.CustomParameters)
            {
                payload[kvp.Key] = kvp.Value;
            }
        }

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return httpRequest;
    }

    private void AddAuthenticationHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        }

        if (!string.IsNullOrEmpty(Config.OrganizationId))
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Organization", Config.OrganizationId);
        }
    }

    private static List<object> FormatMessages(ProviderInferenceRequest request, bool isReasoningModel)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new
            {
                role = isReasoningModel ? "developer" : "system",
                content = request.SystemPrompt
            });
        }

        foreach (var msg in request.Messages)
        {
            string role = msg.Role switch
            {
                Klydis.Core.Chat.ChatRole.System => isReasoningModel ? "developer" : "system",
                Klydis.Core.Chat.ChatRole.User => "user",
                Klydis.Core.Chat.ChatRole.Assistant => "assistant",
                Klydis.Core.Chat.ChatRole.Tool => "tool",
                Klydis.Core.Chat.ChatRole.Runtime => "system",
                _ => "user"
            };

            if (msg.Role == Klydis.Core.Chat.ChatRole.Tool && !string.IsNullOrEmpty(msg.Name))
            {
                messages.Add(new
                {
                    role = "tool",
                    tool_call_id = msg.Name,
                    content = msg.Content
                });
            }
            else
            {
                messages.Add(new
                {
                    role = role,
                    content = msg.Content
                });
            }
        }

        return messages;
    }

    private static object FormatToolParameters(IList<ToolParameter> parameters)
    {
        var properties = new Dictionary<string, object>();
        var requiredList = new List<string>();

        foreach (var p in parameters)
        {
            var propObj = new Dictionary<string, object>
            {
                ["type"] = p.Type.ToLowerInvariant(),
                ["description"] = p.Description
            };

            if (p.Enum != null && p.Enum.Length > 0)
            {
                propObj["enum"] = p.Enum;
            }

            properties[p.Name] = propObj;
            if (p.Required)
            {
                requiredList.Add(p.Name);
            }
        }

        return new
        {
            type = "object",
            properties = properties,
            required = requiredList,
            additionalProperties = false
        };
    }

    private static IDictionary<string, object> ParseToolArguments(string argsJson)
    {
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(argsJson, JsonOptions);
            return dict ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object> { ["raw"] = argsJson };
        }
    }

    private static TokenUsageMetrics ExtractTokenUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage))
        {
            int prompt = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            int comp = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
            int total = usage.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : prompt + comp;
            int reasoning = 0;
            int cached = 0;

            if (usage.TryGetProperty("completion_tokens_details", out var ctd))
            {
                if (ctd.TryGetProperty("reasoning_tokens", out var rt))
                    reasoning = rt.GetInt32();
            }

            if (usage.TryGetProperty("prompt_tokens_details", out var ptd))
            {
                if (ptd.TryGetProperty("cached_tokens", out var cpt))
                    cached = cpt.GetInt32();
            }

            return new TokenUsageMetrics(
                PromptTokens: prompt,
                CompletionTokens: comp,
                TotalTokens: total,
                ReasoningTokens: reasoning,
                CacheReadInputTokens: cached
            );
        }

        return TokenUsageMetrics.Empty;
    }

    private sealed class ToolCallAccumulator
    {
        public int Index { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder ArgumentsBuilder { get; } = new();
    }
}
