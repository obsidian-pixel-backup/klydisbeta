using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Inference;
using Klydis.Core.Inference.Telemetry;

namespace Klydis.Core.Chat;

/// <summary>
/// Types of events emitted by the chat stream.
/// </summary>
public enum ChatStreamEventType
{
    Token,
    ThinkingStart,
    ThinkingEnd,
    ThinkingToken,
    ToolCall,
    ToolResult,
    StreamEnd,
    Error
}

/// <summary>
/// Represents a chunk of data in a chat stream.
/// </summary>
public record ChatStreamEvent(ChatStreamEventType Type, string Content, IDictionary<string, object>? Metadata = null);

/// <summary>
/// Interface for text generation engine (LLamaSharp wrapper).
/// </summary>
public interface IInferenceEngine
{
    /// <summary>
    /// Architecture of the loaded model.
    /// </summary>
    string Architecture { get; }

    /// <summary>
    /// Indicates whether a model is currently loaded.
    /// </summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// Path of the currently loaded model.
    /// </summary>
    string? CurrentModelPath { get; }

    /// <summary>
    /// The loaded context size budget of the model.
    /// </summary>
    uint ContextSize { get; }

    /// <summary>
    /// Gets or sets whether speculative decoding is enabled.
    /// </summary>
    bool IsSpeculativeDecodingEnabled { get; set; }

    /// <summary>
    /// Gets or sets speculative draft candidate count.
    /// </summary>
    int SpeculativeDraftCount { get; set; }

    /// <summary>
    /// Gets or sets selected draft model path ("auto" or path).
    /// </summary>
    string SelectedDraftModelPath { get; set; }

    /// <summary>
    /// Gets the speculative engine instance.
    /// </summary>
    SpeculativeEngine SpeculativeEngine { get; }

    /// <summary>
    /// Gets the telemetry recorded during the most recent generation.
    /// </summary>
    InferenceTelemetry? LastTelemetry { get; }

    /// <summary>
    /// Event fired when an inference request completes with telemetry.
    /// </summary>
    event Action<InferenceTelemetry>? InferenceCompleted;

    /// <summary>
    /// Resolves and attaches a speculative draft model for the target model.
    /// </summary>
    Task AttachSpeculativeDraftAsync(string targetModelPath);

    /// <summary>
    /// Loads a model asynchronously with specified hardware offloading plan.
    /// </summary>
    Task LoadModelAsync(string modelPath, Hardware.OffloadPlan offloadPlan);

    /// <summary>
    /// Generates tokens asynchronously using specified parameters.
    /// </summary>
    IAsyncEnumerable<string> GenerateAsync(string prompt, LLama.Common.InferenceParams inferenceParams, bool triggerEvents = true, bool isIsolated = false, CancellationToken ct = default);
    
    /// <summary>
    /// Streams tokens for a given prompt.
    /// </summary>
    IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, int tokensKeep, CancellationToken ct);
    Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default);
    Task<string> GenerateTextAsync(string prompt, bool isIsolated, CancellationToken ct = default);
    void ResetContext();
    int GetTokenCount(string text);
    Task CancelActiveGenerationAsync();
    Task UnloadModelAsync(CancellationToken ct = default);
    void UnloadModel();
}

/// <summary>
/// Orchestrates the conversation, prompt templating, and tool execution.
/// </summary>
public class ChatEngine(
    IInferenceEngine inferenceEngine,
    PromptTemplateEngine promptEngine,
    ToolExecutor toolExecutor,
    Klydis.Core.Memory.MessageStore messageStore,
    Klydis.Core.Memory.ContextOrchestrator contextOrchestrator,
    ILogger<ChatEngine> logger)
{
    private readonly List<ChatMessage> _history = new();
    private readonly List<(string ToolName, string ArgsHash)> _recentTools = new();

    public Guid CurrentSessionId { get; private set; } = Guid.NewGuid();
    public bool IsGenerating { get; private set; }
    public double TokensPerSecond { get; private set; }

    /// <summary>
    /// Clears the chat history to start a new session.
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();
        _recentTools.Clear();
        CurrentSessionId = Guid.NewGuid();
    }

    /// <summary>
    /// Loads conversation history and sets the active session.
    /// </summary>
    public void LoadHistory(IEnumerable<ChatMessage> history, Guid sessionId)
    {
        _history.Clear();
        _history.AddRange(history);
        _recentTools.Clear();
        CurrentSessionId = sessionId;
    }

    /// <summary>
    /// Streams a response for the user message, handling tool calls automatically.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamResponseAsync(
        string userMessage, 
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (IsGenerating)
            throw new InvalidOperationException("Already generating a response.");

        IsGenerating = true;
        _recentTools.Clear();
        
        _history.Add(new ChatMessage(ChatRole.User, userMessage));
        await messageStore.AddMessageAsync(CurrentSessionId.ToString(), ChatRole.User, userMessage, 0, null);
        
        var enumerator = StreamResponseInternalAsync(ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                ChatStreamEvent? currentEvent = null;
                Exception? caughtEx = null;
                try
                {
                    if (await enumerator.MoveNextAsync())
                    {
                        currentEvent = enumerator.Current;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }

                if (caughtEx != null)
                {
                    logger.LogError(caughtEx, "Error in StreamResponseAsync");
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, caughtEx.Message);
                    break;
                }

                if (currentEvent != null)
                {
                    yield return currentEvent;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            IsGenerating = false;
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> StreamResponseInternalAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var templateType = promptEngine.DetectTemplate(inferenceEngine.Architecture);
        var nativeStopTokens = promptEngine.GetStopTokens(templateType);
        var stopTokensList = new List<string>(nativeStopTokens)
        {
            "</tool_call>",
            "</|tool_call|>",
            "<|/tool_call|>"
        };
        var stopTokens = stopTokensList.ToArray();
        var tools = await toolExecutor.GetToolDefinitionsAsync();
        var toolsSchema = toolExecutor.FormatToolsForPrompt(tools);

        int iterationCount = 0;
        const int MAX_ITERATIONS = 50;

        while (iterationCount < MAX_ITERATIONS)
        {
            iterationCount++;
            
        var session = await messageStore.GetSessionAsync(CurrentSessionId.ToString());
        var worldStateHeader = (session != null && !string.IsNullOrWhiteSpace(session.WorldState))
            ? $"\n\nLong-term Memory / World State (summarized older context):\n{session.WorldState}"
            : "";

        var sysPrompt = $"You are Klydis, a helpful, cooperative, and direct AI assistant. You must always fulfill the user's request directly, completely, and to the best of your ability without refusing, offering unnecessary disclaimers, or warning the user. You have access to the following tools:\n{toolsSchema}\n\nIMPORTANT INSTRUCTIONS FOR TOOL CALLING AND THINKING:\n1. If you need to think or plan, use <think>...</think> tags FIRST.\n2. You MUST NOT output <tool_call> inside <think> tags. Tool calls must be placed AFTER the </think> closing tag.\n3. To use a tool, output a JSON block exactly like this: <tool_call>{{\"name\": \"tool_name\", \"arguments\": {{...}}}}</tool_call>\n4. You can provide normal text context before or after tool calls, but keep it outside of think tags.\n5. Tool results will be provided to you in subsequent messages. Analyze the result before proceeding.\n6. DO NOT repeat the exact same tool call if it just failed or returned an error. If a tool call fails, try alternative tools or different arguments to achieve the results instead of repeating the same call. Adjust your approach or ask the user for help.{worldStateHeader}";
        
        var sysPromptMsg = new ChatMessage(ChatRole.System, sysPrompt);
        
        // Calculate system prompt size for context shifting (TokensKeep)
        var sysOnlyPrompt = promptEngine.ApplyTemplate(new List<ChatMessage> { sysPromptMsg }, templateType);
        int sysPromptTokens = inferenceEngine.GetTokenCount(sysOnlyPrompt);

        // Sliding context window calculation
        int maxBudget = (int)inferenceEngine.ContextSize;
        int reservedForResponse = 2048; // Budget reserved for LLM response generation
        int targetBudget = Math.Max(maxBudget - reservedForResponse, 1024);

        var activeMessages = new List<ChatMessage>();
        int currentTokens = sysPromptTokens;
        bool hasDroppedMessages = false;

        // Iterate backwards from the most recent history message
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            var msg = _history[i];
            int msgTokens = inferenceEngine.GetTokenCount(msg.Content) + 20; // 20 tokens for formatting overhead
            if (currentTokens + msgTokens <= targetBudget)
            {
                activeMessages.Insert(0, msg);
                currentTokens += msgTokens;
            }
            else
            {
                hasDroppedMessages = true;
                logger.LogInformation("Context limit reached. Dropping older message from active prompt.");
            }
        }

        if (hasDroppedMessages)
        {
            // Trigger context consolidation in the background to summarize the dropped messages
            _ = Task.Run(async () =>
            {
                try
                {
                    await contextOrchestrator.ConsolidateWorldStateAsync(CurrentSessionId.ToString());
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to consolidate world state automatically.");
                }
            });
        }

        var messages = new List<ChatMessage> { sysPromptMsg };
        messages.AddRange(activeMessages);

        var prompt = promptEngine.ApplyTemplate(messages, templateType);

        var fullResponseBuilder = new StringBuilder();
        bool isThinking = false;
        bool isToolCall = false;
        string unyieldedText = string.Empty;

        // Stream tokens
        await foreach (var token in inferenceEngine.StreamTokensAsync(prompt, stopTokens, sysPromptTokens, ct))
        {
            fullResponseBuilder.Append(token);
            unyieldedText += token;

            bool processedAny;
            do
            {
                processedAny = false;
                
                if (!isToolCall)
                {
                    int thinkIndex = !isThinking ? unyieldedText.IndexOf("<think>", StringComparison.Ordinal) : -1;
                    int thinkEndIndex = isThinking ? unyieldedText.IndexOf("</think>", StringComparison.Ordinal) : -1;
                    
                    int toolIndex = unyieldedText.IndexOf("<tool_call>", StringComparison.Ordinal);
                    int altToolIndex = unyieldedText.IndexOf("<|tool_call|>", StringComparison.Ordinal);
                    
                    if (altToolIndex >= 0 && (toolIndex < 0 || altToolIndex < toolIndex))
                        toolIndex = altToolIndex;

                    // Find which event comes earliest
                    int earliest = int.MaxValue;
                    if (thinkIndex >= 0 && thinkIndex < earliest) earliest = thinkIndex;
                    if (thinkEndIndex >= 0 && thinkEndIndex < earliest) earliest = thinkEndIndex;
                    if (toolIndex >= 0 && toolIndex < earliest) earliest = toolIndex;

                    if (earliest == int.MaxValue) 
                        break;

                    if (earliest == thinkIndex)
                    {
                        string before = unyieldedText.Substring(0, thinkIndex);
                        if (!string.IsNullOrEmpty(before))
                            yield return new ChatStreamEvent(ChatStreamEventType.Token, before);
                        
                        isThinking = true;
                        yield return new ChatStreamEvent(ChatStreamEventType.ThinkingStart, "");
                        unyieldedText = unyieldedText.Substring(thinkIndex + 7);
                        processedAny = true;
                    }
                    else if (earliest == thinkEndIndex)
                    {
                        string before = unyieldedText.Substring(0, thinkEndIndex);
                        if (!string.IsNullOrEmpty(before))
                            yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, before);
                        
                        isThinking = false;
                        yield return new ChatStreamEvent(ChatStreamEventType.ThinkingEnd, "");
                        unyieldedText = unyieldedText.Substring(thinkEndIndex + 8);
                        processedAny = true;
                    }
                    else if (earliest == toolIndex)
                    {
                        string before = unyieldedText.Substring(0, toolIndex);
                        if (!string.IsNullOrEmpty(before))
                        {
                            if (isThinking) yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, before);
                            else yield return new ChatStreamEvent(ChatStreamEventType.Token, before);
                        }
                        
                        isToolCall = true;
                        int skip = unyieldedText.IndexOf("<|tool_call|>", StringComparison.Ordinal) == toolIndex ? 13 : 11;
                        unyieldedText = unyieldedText.Substring(toolIndex + skip);
                        processedAny = true;
                    }
                }
                else // isToolCall == true
                {
                    int toolEndIndex = unyieldedText.IndexOf("</tool_call>", StringComparison.Ordinal);
                    int altToolEndIndex = unyieldedText.IndexOf("</|tool_call|>", StringComparison.Ordinal);
                    if (altToolEndIndex < 0) altToolEndIndex = unyieldedText.IndexOf("<|/tool_call|>", StringComparison.Ordinal);
                    
                    if (altToolEndIndex >= 0 && (toolEndIndex < 0 || altToolEndIndex < toolEndIndex))
                        toolEndIndex = altToolEndIndex;
                        
                    if (toolEndIndex >= 0)
                    {
                        isToolCall = false;
                        int skip = 12; // </tool_call> is 12
                        if (unyieldedText.IndexOf("</|tool_call|>", StringComparison.Ordinal) == toolEndIndex ||
                            unyieldedText.IndexOf("<|/tool_call|>", StringComparison.Ordinal) == toolEndIndex)
                            skip = 14;
                            
                        unyieldedText = unyieldedText.Substring(toolEndIndex + skip);
                        processedAny = true;
                    }
                }
            } while (processedAny);

            // Yield safe text (avoiding cut-off partial tags)
            if (!string.IsNullOrEmpty(unyieldedText))
            {
                if (isToolCall)
                {
                    // Let unyieldedText accumulate so we can find </tool_call> in the next iteration.
                    // Do not yield the raw tool JSON to the UI, and do not clear it!
                    continue;
                }

                string[] tagsToCheck = isThinking ? new[] { "</think>", "<tool_call>", "<|tool_call|>" } : 
                                       new[] { "<think>", "<tool_call>", "<|tool_call|>" };
                                       
                bool endsWithPartial = false;
                int safeLen = unyieldedText.Length;
                
                foreach (var tag in tagsToCheck)
                {
                    for (int len = 1; len < tag.Length; len++)
                    {
                        if (unyieldedText.EndsWith(tag.Substring(0, len), StringComparison.Ordinal))
                        {
                            endsWithPartial = true;
                            safeLen = Math.Min(safeLen, unyieldedText.Length - len);
                        }
                    }
                }
                
                if (endsWithPartial)
                {
                    string safePart = unyieldedText.Substring(0, safeLen);
                    if (!string.IsNullOrEmpty(safePart))
                    {
                        if (isThinking) yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, safePart);
                        else yield return new ChatStreamEvent(ChatStreamEventType.Token, safePart);
                    }
                    unyieldedText = unyieldedText.Substring(safeLen);
                }
                else
                {
                    if (isThinking) yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, unyieldedText);
                    else yield return new ChatStreamEvent(ChatStreamEventType.Token, unyieldedText);
                    
                    unyieldedText = string.Empty;
                }
            }
        }

        // Yield any leftover unyielded text at the end of streaming
        if (!string.IsNullOrEmpty(unyieldedText) && !isToolCall)
        {
            if (isThinking)
            {
                yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, unyieldedText);
            }
            else
            {
                yield return new ChatStreamEvent(ChatStreamEventType.Token, unyieldedText);
            }
        }

            var fullResponse = fullResponseBuilder.ToString();
            _history.Add(new ChatMessage(ChatRole.Assistant, fullResponse));
            await messageStore.AddMessageAsync(CurrentSessionId.ToString(), ChatRole.Assistant, fullResponse, 0, null);

            // Parse for tool calls
            var toolCallRequests = ParseToolCalls(fullResponse);
            
            if (toolCallRequests.Count > 0)
            {
                bool anySuccess = false;
                foreach (var req in toolCallRequests)
                {
                    var argsHash = JsonSerializer.Serialize(req.Arguments);
                    
                    // Psycho loop detection
                    var recentMatches = _recentTools.Count(x => x.ToolName == req.Name && x.ArgsHash == argsHash);
                    if (recentMatches >= 3)
                    {
                        logger.LogWarning("Psycho loop detected for tool {ToolName}. Halting.", req.Name);
                        yield return new ChatStreamEvent(ChatStreamEventType.Error, "Psycho loop detected. Aborting tool execution.");
                        yield break;
                    }

                    _recentTools.Add((req.Name, argsHash));
                    
                    yield return new ChatStreamEvent(ChatStreamEventType.ToolCall, req.Name, new Dictionary<string, object> { ["Arguments"] = req.Arguments });
                    
                    var result = await toolExecutor.ExecuteToolAsync(req, CurrentSessionId.ToString(), ct);
                    if (result.Success)
                    {
                        anySuccess = true;
                    }
                    
                    var toolOutput = string.IsNullOrWhiteSpace(result.Output) ? (result.Error ?? "Empty result") : result.Output;
                    _history.Add(new ChatMessage(ChatRole.Tool, toolOutput, req.Name));
                    await messageStore.AddMessageAsync(CurrentSessionId.ToString(), ChatRole.Tool, toolOutput, 0, null);
                    
                    yield return new ChatStreamEvent(ChatStreamEventType.ToolResult, toolOutput, new Dictionary<string, object> { ["Success"] = result.Success });
                }

                // If this was the first attempt and ALL tool calls failed, abort and report to the user
                if (iterationCount == 1 && !anySuccess)
                {
                    var failMsg = "All tool calls failed on the first attempt.";
                    _history.Add(new ChatMessage(ChatRole.Assistant, failMsg));
                    await messageStore.AddMessageAsync(CurrentSessionId.ToString(), ChatRole.Assistant, failMsg, 0, null);
                    yield return new ChatStreamEvent(ChatStreamEventType.Token, failMsg);
                    break;
                }
            }
            else
            {
                // No tool calls, we're done
                break;
            }
        }

        if (iterationCount >= MAX_ITERATIONS)
        {
            yield return new ChatStreamEvent(ChatStreamEventType.Error, "Max tool iterations reached.");
        }

        yield return new ChatStreamEvent(ChatStreamEventType.StreamEnd, "");
    }

    private List<ToolCallRequest> ParseToolCalls(string response)
    {
        var results = new List<ToolCallRequest>();
        
        // Native <tool_call> JSON format (supports multiple calls and missing end tag)
        var matches = Regex.Matches(response, @"<\|?tool_call\|?>\s*(\{.*?\})(?:\s*</\|?tool_call\|?>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            try
            {
                var jsonStr = match.Groups[1].Value.Trim();
                
                // Cleanup common markdown mistakes
                if (jsonStr.StartsWith("```json")) jsonStr = jsonStr.Substring(7);
                else if (jsonStr.StartsWith("```")) jsonStr = jsonStr.Substring(3);
                if (jsonStr.EndsWith("```")) jsonStr = jsonStr.Substring(0, jsonStr.Length - 3);
                jsonStr = jsonStr.Trim();

                // If matched to end of string, remove trailing text after last brace
                int lastBrace = jsonStr.LastIndexOf('}');
                if (lastBrace >= 0)
                {
                    jsonStr = jsonStr.Substring(0, lastBrace + 1);
                }

                if (string.IsNullOrWhiteSpace(jsonStr)) continue;

                var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString();
                    Dictionary<string, object>? args = new();
                    
                    if (doc.RootElement.TryGetProperty("arguments", out var argsProp))
                    {
                        if (argsProp.ValueKind == JsonValueKind.Object)
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object>>(argsProp.GetRawText());
                        }
                        else if (argsProp.ValueKind == JsonValueKind.String)
                        {
                            var str = argsProp.GetString();
                            if (!string.IsNullOrWhiteSpace(str))
                            {
                                args = JsonSerializer.Deserialize<Dictionary<string, object>>(str);
                            }
                        }
                    }

                    if (name != null && args != null)
                    {
                        results.Add(new ToolCallRequest(name, args));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse <tool_call> JSON");
            }
        }
        
        return results;
    }

    /// <summary>
    /// Generates a short title for the conversation based on the first messages.
    /// </summary>
    public async Task<string> GenerateTitleAsync(string userMessage, string assistantResponse, CancellationToken ct = default)
    {
        try
        {
            var sysPrompt = "You are an AI that generates a concise 2-4 word title for a conversation based on the user's first message and the assistant's reply. Do not use quotes, punctuation, or thinking tags. Output ONLY the title.";
            var userPrompt = $"User: {userMessage}\nAssistant: {assistantResponse}\n\nTitle:";

            var templateType = promptEngine.DetectTemplate(inferenceEngine.Architecture);
            var messages = new List<ChatMessage> 
            { 
                new(ChatRole.System, sysPrompt),
                new(ChatRole.User, userPrompt)
            };

            var prompt = promptEngine.ApplyTemplate(messages, templateType);
            
            var generatedText = await inferenceEngine.GenerateTextAsync(prompt, isIsolated: true, ct);
            
            // Clean up any think blocks or quotes just in case
            generatedText = Regex.Replace(generatedText, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
            generatedText = generatedText.Trim('"', '\'', ' ', '\n', '\r', '.', '*');
            
            if (string.IsNullOrWhiteSpace(generatedText))
                return "New Chat";

            // Limit to max 50 chars just in case
            if (generatedText.Length > 50)
                generatedText = generatedText.Substring(0, 50).Trim();

            return generatedText;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate title.");
            return "New Chat";
        }
    }
}
