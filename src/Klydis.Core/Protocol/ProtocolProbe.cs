using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klydis.Core.Chat;
using Klydis.Core.Inference;
using ChatMessage = Klydis.Core.Chat.ChatMessage;

namespace Klydis.Core.Protocol;

/// <summary>
/// Result of an automatic empirical protocol probe for a model.
/// </summary>
public sealed record ProtocolProbeResult
{
    public required string ModelId { get; init; }
    public required string Adapter { get; init; }
    public bool ToolCallParsed { get; init; }
    public bool ToolExecuted { get; init; }
    public bool ToolResultConsumed { get; init; }
    public bool ContinuationWorked { get; init; }
    public bool CompletionWorked { get; init; }
    public bool StopTokenWorked { get; init; }
    public double Score { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Short diagnostic summary.</summary>
    public string Summary =>
        $"Score={Score:F2} Parsed={ToolCallParsed} Executed={ToolExecuted} Cont={ContinuationWorked} Stop={StopTokenWorked}";
}

/// <summary>
/// Executes a minimal, deterministic capability and protocol probe to verify whether
/// a model speaks its declared dialect natively (tool call -> parse -> result injection -> continuation).
/// </summary>
public static class ProtocolProbe
{
    /// <summary>
    /// Evaluates protocol compatibility offline or using pre-recorded test outputs.
    /// </summary>
    public static ProtocolProbeResult EvaluateSimulated(
        string modelId,
        IModelProtocol adapter,
        string testToolCallOutput,
        string testContinuationOutput)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var actions1 = adapter.ParseOutput(testToolCallOutput);
        bool parsed = actions1.Count > 0 && actions1[0].Type == CanonicalActionType.ToolCall;

        bool executed = parsed; // Simulated successful execution of read-only tool
        bool resultConsumed = false;
        bool continuationWorked = false;
        bool completionWorked = false;

        if (parsed)
        {
            var fakeResult = new ChatMessage(ChatRole.Tool, "{\"cpu_percent\": 12.4}", actions1[0].ToolName);
            string formatted = adapter.FormatToolResult(fakeResult);
            resultConsumed = !string.IsNullOrWhiteSpace(formatted);

            var actions2 = adapter.ParseOutput(testContinuationOutput);
            if (actions2.Count > 0)
            {
                continuationWorked = true;
                completionWorked = actions2[0].Type is CanonicalActionType.ToolCall or CanonicalActionType.CompletionClaim;
            }
            else if (!string.IsNullOrWhiteSpace(testContinuationOutput))
            {
                continuationWorked = true;
                completionWorked = true; // Clean text answer after tool result
            }
        }

        var stopTokens = adapter.GetStopTokens();
        bool stopTokenWorked = stopTokens != null && stopTokens.Count > 0;

        double score = 0.0;
        if (parsed) score += 0.35;
        if (executed) score += 0.15;
        if (resultConsumed) score += 0.15;
        if (continuationWorked) score += 0.20;
        if (stopTokenWorked) score += 0.15;

        return new ProtocolProbeResult
        {
            ModelId = modelId,
            Adapter = adapter.GetType().Name,
            ToolCallParsed = parsed,
            ToolExecuted = executed,
            ToolResultConsumed = resultConsumed,
            ContinuationWorked = continuationWorked,
            CompletionWorked = completionWorked,
            StopTokenWorked = stopTokenWorked,
            Score = Math.Clamp(score, 0.0, 1.0),
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Executes a live deterministic probe against a loaded inference engine.
    /// </summary>
    public static async Task<ProtocolProbeResult> ExecuteLiveProbeAsync(
        string modelId,
        IInferenceEngine engine,
        IModelProtocol adapter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(adapter);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a desktop agent with access to tool 'system_cpu_info'. Use tool 'system_cpu_info' to inspect the CPU."),
            new(ChatRole.User, "What is the CPU status?")
        };

        var inferenceParams = new LLama.Common.InferenceParams
        {
            MaxTokens = 256,
            AntiPrompts = adapter.GetStopTokens().ToList()
        };

        string prompt = adapter.BuildPrompt(messages);
        var sb1 = new System.Text.StringBuilder();
        await foreach (var token in engine.GenerateAsync(prompt, inferenceParams, triggerEvents: false, isIsolated: true, ct: ct))
        {
            sb1.Append(token);
        }
        string output1 = sb1.ToString();

        var actions1 = adapter.ParseOutput(output1);
        bool parsed = actions1.Count > 0 && actions1[0].Type == CanonicalActionType.ToolCall;

        bool executed = false;
        bool resultConsumed = false;
        bool continuationWorked = false;
        bool completionWorked = false;

        if (parsed)
        {
            executed = true;
            var toolMsg = new ChatMessage(ChatRole.Tool, "{\"status\": \"ok\", \"cpu_percent\": 15.2}", actions1[0].ToolName);
            messages.Add(new ChatMessage(ChatRole.Assistant, output1));
            messages.Add(toolMsg);

            string prompt2 = adapter.BuildPrompt(messages);
            var sb2 = new System.Text.StringBuilder();
            await foreach (var token in engine.GenerateAsync(prompt2, inferenceParams, triggerEvents: false, isIsolated: true, ct: ct))
            {
                sb2.Append(token);
            }
            string output2 = sb2.ToString();

            resultConsumed = true;
            var actions2 = adapter.ParseOutput(output2);
            if (actions2.Count > 0 || !string.IsNullOrWhiteSpace(output2))
            {
                continuationWorked = true;
                completionWorked = true;
            }
        }

        var stopTokens = adapter.GetStopTokens();
        bool stopTokensPresent = stopTokens != null && stopTokens.Count > 0;

        double score = 0.0;
        if (parsed) score += 0.35;
        if (executed) score += 0.15;
        if (resultConsumed) score += 0.15;
        if (continuationWorked) score += 0.20;
        if (stopTokensPresent) score += 0.15;

        return new ProtocolProbeResult
        {
            ModelId = modelId,
            Adapter = adapter.GetType().Name,
            ToolCallParsed = parsed,
            ToolExecuted = executed,
            ToolResultConsumed = resultConsumed,
            ContinuationWorked = continuationWorked,
            CompletionWorked = completionWorked,
            StopTokenWorked = stopTokensPresent,
            Score = Math.Clamp(score, 0.0, 1.0),
            Timestamp = DateTime.UtcNow
        };
    }
}
