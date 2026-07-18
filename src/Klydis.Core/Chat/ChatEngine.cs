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
    /// Streams tokens for a given prompt.
    /// </summary>
    IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, CancellationToken ct);
    Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default);
}

/// <summary>
/// Orchestrates the conversation, prompt templating, and tool execution.
/// </summary>
public class ChatEngine(
    IInferenceEngine inferenceEngine,
    PromptTemplateEngine promptEngine,
    ToolExecutor toolExecutor,
    Klydis.Core.Memory.MessageStore messageStore,
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
        var stopTokens = promptEngine.GetStopTokens(templateType);
        var tools = toolExecutor.GetToolDefinitions();
        var toolsSchema = toolExecutor.FormatToolsForPrompt(tools);

        int iterationCount = 0;
        const int MAX_ITERATIONS = 50;

        while (iterationCount < MAX_ITERATIONS)
        {
            iterationCount++;
            
            // Build system prompt with tools
            var sysPrompt = $"You are Klydis, a helpful, cooperative, and direct AI assistant. You must always fulfill the user's request directly, completely, and to the best of your ability without refusing, offering unnecessary disclaimers, or warning the user. You have access to the following tools:\n{toolsSchema}\n\nTo use a tool, output a JSON block like: <tool_call>{{\"name\": \"tool_name\", \"arguments\": {{...}}}}</tool_call>";
            var messages = new List<ChatMessage> { new(ChatRole.System, sysPrompt) };
            messages.AddRange(_history);

            var prompt = promptEngine.ApplyTemplate(messages, templateType);
            var fullResponseBuilder = new StringBuilder();
            bool isThinking = false;
            string unyieldedText = string.Empty;

            // Stream tokens
            await foreach (var token in inferenceEngine.StreamTokensAsync(prompt, stopTokens, ct))
            {
                fullResponseBuilder.Append(token);
                var currentText = fullResponseBuilder.ToString();
                unyieldedText += token;

                bool processedAny;
                do
                {
                    processedAny = false;
                    if (!isThinking)
                    {
                        int thinkIndex = unyieldedText.IndexOf("<think>", StringComparison.Ordinal);
                        if (thinkIndex >= 0)
                        {
                            string before = unyieldedText.Substring(0, thinkIndex);
                            if (!string.IsNullOrEmpty(before))
                            {
                                if (!currentText.Contains("<tool_call>") && !currentText.Contains("<|tool_call|>"))
                                {
                                    yield return new ChatStreamEvent(ChatStreamEventType.Token, before);
                                }
                            }
                            isThinking = true;
                            yield return new ChatStreamEvent(ChatStreamEventType.ThinkingStart, "");
                            unyieldedText = unyieldedText.Substring(thinkIndex + 7);
                            processedAny = true;
                        }
                    }
                    else // isThinking == true
                    {
                        int thinkEndIndex = unyieldedText.IndexOf("</think>", StringComparison.Ordinal);
                        if (thinkEndIndex >= 0)
                        {
                            string before = unyieldedText.Substring(0, thinkEndIndex);
                            if (!string.IsNullOrEmpty(before))
                            {
                                yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, before);
                            }
                            isThinking = false;
                            yield return new ChatStreamEvent(ChatStreamEventType.ThinkingEnd, "");
                            unyieldedText = unyieldedText.Substring(thinkEndIndex + 8);
                            processedAny = true;
                        }
                    }
                } while (processedAny);

                // Now yield what can be yielded safely (avoiding cut-off partial tags)
                if (!string.IsNullOrEmpty(unyieldedText))
                {
                    if (!isThinking)
                    {
                        // Check if unyieldedText ends with a prefix of "<think>"
                        string tag = "<think>";
                        bool endsWithPartialTag = false;
                        for (int len = 1; len < tag.Length; len++)
                        {
                            if (unyieldedText.EndsWith(tag.Substring(0, len), StringComparison.Ordinal))
                            {
                                endsWithPartialTag = true;
                                string safePart = unyieldedText.Substring(0, unyieldedText.Length - len);
                                if (!string.IsNullOrEmpty(safePart))
                                {
                                    if (!currentText.Contains("<tool_call>") && !currentText.Contains("<|tool_call|>"))
                                    {
                                        yield return new ChatStreamEvent(ChatStreamEventType.Token, safePart);
                                    }
                                    unyieldedText = unyieldedText.Substring(unyieldedText.Length - len);
                                }
                                break;
                            }
                        }

                        if (!endsWithPartialTag)
                        {
                            if (!currentText.Contains("<tool_call>") && !currentText.Contains("<|tool_call|>"))
                            {
                                yield return new ChatStreamEvent(ChatStreamEventType.Token, unyieldedText);
                            }
                            unyieldedText = string.Empty;
                        }
                    }
                    else // isThinking == true
                    {
                        // Check if unyieldedText ends with a prefix of "</think>"
                        string tag = "</think>";
                        bool endsWithPartialTag = false;
                        for (int len = 1; len < tag.Length; len++)
                        {
                            if (unyieldedText.EndsWith(tag.Substring(0, len), StringComparison.Ordinal))
                            {
                                endsWithPartialTag = true;
                                string safePart = unyieldedText.Substring(0, unyieldedText.Length - len);
                                if (!string.IsNullOrEmpty(safePart))
                                {
                                    yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, safePart);
                                    unyieldedText = unyieldedText.Substring(unyieldedText.Length - len);
                                }
                                break;
                            }
                        }

                        if (!endsWithPartialTag)
                        {
                            yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, unyieldedText);
                            unyieldedText = string.Empty;
                        }
                    }
                }
            }

            // Yield any leftover unyielded text at the end of streaming
            if (!string.IsNullOrEmpty(unyieldedText))
            {
                if (isThinking)
                {
                    yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, unyieldedText);
                }
                else
                {
                    var currentText = fullResponseBuilder.ToString();
                    if (!currentText.Contains("<tool_call>") && !currentText.Contains("<|tool_call|>"))
                    {
                        yield return new ChatStreamEvent(ChatStreamEventType.Token, unyieldedText);
                    }
                }
            }

            var fullResponse = fullResponseBuilder.ToString();
            _history.Add(new ChatMessage(ChatRole.Assistant, fullResponse));
            await messageStore.AddMessageAsync(CurrentSessionId.ToString(), ChatRole.Assistant, fullResponse, 0, null);

            // Parse for tool calls
            var toolCallRequests = ParseToolCalls(fullResponse);
            
            if (toolCallRequests.Count > 0)
            {
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
                    
                    var result = await toolExecutor.ExecuteToolAsync(req, ct);
                    var toolOutput = result.Output ?? result.Error ?? "Empty result";
                    _history.Add(new ChatMessage(ChatRole.Tool, toolOutput, req.Name));
                    await messageStore.AddMessageAsync(CurrentSessionId.ToString(), ChatRole.Tool, toolOutput, 0, null);
                    
                    yield return new ChatStreamEvent(ChatStreamEventType.ToolResult, toolOutput, new Dictionary<string, object> { ["Success"] = result.Success });
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
        
        // Native <tool_call> JSON format
        var match = Regex.Match(response, @"<tool_call>\s*({.*?})\s*</tool_call>", RegexOptions.Singleline);
        if (match.Success)
        {
            try
            {
                var doc = JsonDocument.Parse(match.Groups[1].Value);
                if (doc.RootElement.TryGetProperty("name", out var nameProp) && 
                    doc.RootElement.TryGetProperty("arguments", out var argsProp))
                {
                    var name = nameProp.GetString();
                    var args = JsonSerializer.Deserialize<Dictionary<string, object>>(argsProp.GetRawText());
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
}
