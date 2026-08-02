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
    Error,
    MemorySummarizing
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
    /// Raw GGUF chat template string if present.
    /// </summary>
    string? RawChatTemplate { get; }

    /// <summary>
    /// Fine-tune name if present.
    /// </summary>
    string? FineTuneName { get; }

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
    /// Gets or sets target KV cache quantization precision.
    /// </summary>
    KvCacheQuantizationType TargetKvQuantization { get; set; }

    /// <summary>
    /// Gets current KV cache memory estimate and architecture metrics.
    /// </summary>
    KvCacheMemoryEstimate? CurrentKvCacheEstimate { get; }

    /// <summary>
    /// Saves native KV context state to disk snapshot file.
    /// </summary>
    Task SaveStateAsync(string filePath);

    /// <summary>
    /// Loads native KV context state from disk snapshot file.
    /// </summary>
    Task LoadStateAsync(string filePath);

    /// <summary>
    /// Streams tokens for a given prompt.
    /// </summary>
    IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, int tokensKeep, CancellationToken ct);
    Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default);
    Task<string> GenerateTextAsync(string prompt, bool isIsolated, CancellationToken ct = default);
    Task<string> GenerateTextAsync(string prompt, bool isIsolated, int maxTokens, CancellationToken ct = default);
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
    ILogger<ChatEngine> logger,
    ModelMessageQueue? messageQueue = null,
    Klydis.Core.RAG.VectorStore? vectorStore = null)
{
    private readonly List<ChatMessage> _history = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<ChatMessage>> _sessionHistories = new();
    private readonly List<(string ToolName, string ArgsHash, string PriorResult)> _recentTools = new();
    private int _consecutiveBlockedToolCalls = 0;

    public Klydis.Core.RAG.VectorStore? VectorStore { get; set; } = vectorStore;
    
    /// <summary>
    /// Calculates the rolling compression threshold as 75% of the model's total context size.
    /// </summary>
    private int GetRollingCompressionThreshold()
    {
        int contextSize = (int)inferenceEngine.ContextSize;
        return Math.Clamp((int)(contextSize * 0.75), 2048, 1000000);
    }

    public ModelMessageQueue? MessageQueue { get; set; } = messageQueue;
    public string SelectedPersonality { get; set; } = "Default";
    public bool IsGoalMode { get; set; } = false;
    public string CurrentSessionId { get; private set; } = Guid.NewGuid().ToString();
    public bool IsGenerating { get; private set; }
    public double TokensPerSecond { get; private set; }
    public IReadOnlyList<ChatMessage> History => _history.AsReadOnly();

    /// <summary>
    /// Gets or sets target KV cache quantization precision on the underlying inference engine.
    /// </summary>
    public KvCacheQuantizationType TargetKvQuantization
    {
        get => inferenceEngine.TargetKvQuantization;
        set => inferenceEngine.TargetKvQuantization = value;
    }

    /// <summary>
    /// Gets the current KV cache memory estimate from the underlying inference engine.
    /// </summary>
    public KvCacheMemoryEstimate? CurrentKvCacheEstimate => inferenceEngine.CurrentKvCacheEstimate;

    /// <summary>
    /// Clears the chat history to start a new session.
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();
        _recentTools.Clear();
        _consecutiveBlockedToolCalls = 0;
        CurrentSessionId = Guid.NewGuid().ToString();
        _sessionHistories[CurrentSessionId] = _history;
    }

    /// <summary>
    /// Loads conversation history and sets the active session.
    /// </summary>
    public void LoadHistory(IEnumerable<ChatMessage> history, string sessionId)
    {
        var targetId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId;
        var histList = history.ToList();
        _sessionHistories[targetId] = histList;

        _history.Clear();
        _recentTools.Clear();
        _consecutiveBlockedToolCalls = 0;
        _history.AddRange(histList);
        CurrentSessionId = targetId;
    }

    /// <summary>
    /// Cancels any active generation in the underlying inference engine.
    /// </summary>
    public async Task CancelActiveGenerationAsync()
    {
        await inferenceEngine.CancelActiveGenerationAsync();
    }

    /// <summary>
    /// Streams a response for the user message, handling tool calls automatically.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamResponseAsync(
        string userMessage, 
        [EnumeratorCancellation] CancellationToken ct,
        string? skillContext = null,
        bool? isGoalMode = null)
    {
        IsGenerating = true;
        _recentTools.Clear();
        bool activeGoalMode = isGoalMode ?? IsGoalMode;
        
        string generatingSessionId = CurrentSessionId;
        if (!_sessionHistories.TryGetValue(generatingSessionId, out var activeHistory))
        {
            activeHistory = new List<ChatMessage>(_history);
            _sessionHistories[generatingSessionId] = activeHistory;
        }

        var userMsgObj = new ChatMessage(ChatRole.User, userMessage);
        activeHistory.Add(userMsgObj);
        if (CurrentSessionId == generatingSessionId && !_history.Contains(userMsgObj))
        {
            _history.Add(userMsgObj);
        }

        await messageStore.AddMessageAsync(generatingSessionId, ChatRole.User, userMessage, 0, null);
        
        var enumerator = StreamResponseInternalAsync(generatingSessionId, activeHistory, ct, skillContext, activeGoalMode).GetAsyncEnumerator(ct);
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
        string generatingSessionId,
        List<ChatMessage> activeHistory,
        [EnumeratorCancellation] CancellationToken ct,
        string? skillContext = null,
        bool isGoalMode = false)
    {
        var templateType = promptEngine.DetectTemplate(
            inferenceEngine.Architecture, 
            inferenceEngine.CurrentModelPath, 
            inferenceEngine.RawChatTemplate, 
            inferenceEngine.FineTuneName);
        var nativeStopTokens = promptEngine.GetStopTokens(templateType);
        var stopTokensList = new List<string>(nativeStopTokens);
        var stopTokens = stopTokensList.ToArray();
        var tools = await toolExecutor.GetToolDefinitionsAsync();
        var toolsSchema = toolExecutor.FormatToolsForPrompt(tools);

        int iterationCount = 0;
        const int MAX_ITERATIONS = 100;

        while (iterationCount < MAX_ITERATIONS)
        {
            iterationCount++;
            
            // Execute automated rolling compression when history tokens reach 75% of context window
            int rollingThreshold = GetRollingCompressionThreshold();
            int estimatedHistoryTokens = activeHistory.Sum(m => (inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(m.Content) : contextOrchestrator.EstimateTokens(m.Content)) + 25);
            if (estimatedHistoryTokens >= rollingThreshold)
            {
                yield return new ChatStreamEvent(ChatStreamEventType.MemorySummarizing, "🧠 Summarizing conversation context and saving to memory...");
                int keepRecent = Math.Clamp((int)(inferenceEngine.ContextSize * 0.25), 2048, 262144);
                logger.LogInformation("Active history tokens ({Tokens}) reached rolling threshold ({Threshold}, 75% of {Ctx} context). Compressing older context into WorldState. Keeping {KeepRecent} recent tokens.",
                    estimatedHistoryTokens, rollingThreshold, (int)inferenceEngine.ContextSize, keepRecent);
                await contextOrchestrator.PerformRollingCompressionAsync(activeHistory, generatingSessionId, rollingThreshold, keepRecent);
            }

            var session = await messageStore.GetSessionAsync(generatingSessionId);
        var worldStateHeader = (session != null && !string.IsNullOrWhiteSpace(session.WorldState))
            ? $"\n\nLong-term Memory / World State (summarized older context):\n{session.WorldState}"
            : "";

        if (MessageQueue != null && toolExecutor.MessageQueue == null)
        {
            toolExecutor.MessageQueue = MessageQueue;
        }

        var pendingQueue = MessageQueue?.GetPending(generatingSessionId);
        var queueNotice = (pendingQueue != null && pendingQueue.Count > 0)
            ? "\n\n[PENDING QUEUED USER MESSAGES AVAILABLE]\n" +
              "You have pending queued message(s) from the user waiting in the queue:\n" +
              string.Join("\n", pendingQueue.Select(m => $"- Queue ID: {m.Id} | Mode: {m.Mode} | Content: \"{m.Content}\"")) +
              "\nWhen you reach an optimal point during your reasoning or execution task to incorporate a queued message, call tool 'incorporate_queued_message' with argument {{\"queue_id\": \"<ID>\"}} to retrieve and steer using that message."
            : "";

        string ragNotice = "";
        if (VectorStore != null)
        {
            try
            {
                var collections = await VectorStore.GetCollectionsAsync();
                if (collections != null && collections.Count > 0)
                {
                    var colList = string.Join("\n", collections.Select(c => $"- Collection ID: `{c.Id}` | Name: \"{c.Name}\" | Path: {c.FolderPath}"));
                    ragNotice = $"\n\nINDEXED RAG WORKSPACE COLLECTIONS:\n" +
                        $"The following project folders have been indexed in your RAG vector store:\n{colList}\n" +
                        $"When answering questions about code, documentation, or architecture from these indexed folders, decide for yourself whether to invoke tool 'search_rag' to retrieve relevant context chunks.";
                }
            }
            catch { }
        }

        var skillHeader = !string.IsNullOrWhiteSpace(skillContext) ? $"\n\n{skillContext}" : "";

        var sysPromptManager = new SystemPromptManager();
        var sysPrompt = sysPromptManager.BuildCombinedPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
        
        var sysPromptMsg = new ChatMessage(ChatRole.System, sysPrompt);
        
        // Calculate system prompt size
        var sysOnlyPrompt = promptEngine.ApplyTemplate(new List<ChatMessage> { sysPromptMsg }, templateType);
        int sysPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(sysOnlyPrompt) : contextOrchestrator.EstimateTokens(sysOnlyPrompt);

        // Dynamic sliding context window calculation reserving response headroom and safety margin.
        int totalContext = (int)inferenceEngine.ContextSize;
        int reservedForResponse = totalContext switch
        {
            <= 4096 => 1024,
            <= 16384 => Math.Min(totalContext / 4, 3072),
            <= 65536 => Math.Min(totalContext / 4, 6144),
            _ => Math.Min(totalContext / 4, 12288)
        };
        int safetyMargin = 256;
        
        // ABSOLUTE upper bound for total prompt tokens (system + user history)
        int maxTotalPromptTokens = Math.Max(2048, totalContext - reservedForResponse - safetyMargin);
        
        // Target user budget for conversation history after accounting for system prompt (minimum 4096 tokens floor)
        int targetUserBudget = Math.Max(4096, maxTotalPromptTokens - sysPromptTokens);

        var activeMessages = new List<ChatMessage>();
        int currentTokens = 0; // System prompt is excluded from user history budget
        bool hasDroppedMessages = false;

        ChatMessage? initialUserMsg = activeHistory.Count > 0 ? activeHistory[0] : null;
        int initialUserTokens = initialUserMsg != null ? ((inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(initialUserMsg.Content) : contextOrchestrator.EstimateTokens(initialUserMsg.Content)) + 25) : 0;
        
        // Reserve budget up front for the user's initial prompt goal (activeHistory[0])
        currentTokens += initialUserTokens;

        // Iterate backwards from the most recent history message down to index 1 (skipping initial goal)
        for (int i = activeHistory.Count - 1; i >= 1; i--)
        {
            var msg = activeHistory[i];
            int msgTokens = (inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(msg.Content) : contextOrchestrator.EstimateTokens(msg.Content)) + 25; // 25 tokens for template formatting overhead
            
            // If an individual tool result message is excessively long (> 3000 chars), create a budget-trimmed variant rather than dropping turn
            if (msg.Role == ChatRole.User && msg.Content.Length > 3000 && msg.Content.Contains("[Tool Output"))
            {
                string trimmedContent = msg.Content.Substring(0, 2500) + "\n...[Tool output truncated to preserve active context budget]...";
                msg = new ChatMessage(msg.Role, trimmedContent);
                msgTokens = (inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(trimmedContent) : contextOrchestrator.EstimateTokens(trimmedContent)) + 25;
            }

            if (currentTokens + msgTokens <= targetUserBudget)
            {
                activeMessages.Insert(0, msg);
                currentTokens += msgTokens;
            }
            else
            {
                hasDroppedMessages = true;
                logger.LogInformation("Context limit reached. Compressing/truncating intermediate message for active prompt.");
            }
        }

        // Always preserve the user's initial prompt goal (activeHistory[0]) at index 0 of active messages
        if (initialUserMsg != null)
        {
            activeMessages.Insert(0, initialUserMsg);
        }

        if (hasDroppedMessages)
        {
            // Trigger context consolidation in the background to summarize the dropped messages
            _ = Task.Run(async () =>
            {
                try
                {
                    await contextOrchestrator.ConsolidateWorldStateAsync(generatingSessionId);
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
        int finalPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(prompt) : contextOrchestrator.EstimateTokens(prompt);

        // Strict safety truncation loop: Ensure total prompt tokens strictly fit inside maxTotalPromptTokens
        while (finalPromptTokens > maxTotalPromptTokens && activeMessages.Count > 1)
        {
            // Remove message at index 1 (after initial user prompt) to protect activeHistory[0]
            activeMessages.RemoveAt(1);
            hasDroppedMessages = true;
            messages = new List<ChatMessage> { sysPromptMsg };
            messages.AddRange(activeMessages);
            prompt = promptEngine.ApplyTemplate(messages, templateType);
            finalPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(prompt) : contextOrchestrator.EstimateTokens(prompt);
        }

        var fullResponseBuilder = new StringBuilder();
        bool isThinking = false;
        bool isToolCall = false;
        string unyieldedText = string.Empty;

        logger.LogWarning("[DIAG] StreamResponseInternalAsync: finalPromptTokens={FinalPromptTokens}, targetUserBudget={TargetUserBudget}, contextSize={ContextSize}, activeMessages.Count={ActiveMsgCount}, prompt.Length={PromptLen}",
            finalPromptTokens, targetUserBudget, (int)inferenceEngine.ContextSize, activeMessages.Count, prompt.Length);
        logger.LogWarning("[DIAG] Prompt (first 500 chars): {PromptStart}", prompt.Length > 500 ? prompt.Substring(prompt.Length - 500) : prompt);

        int _tokenStreamCount = 0;
        // Stream tokens
        await foreach (var token in inferenceEngine.StreamTokensAsync(prompt, stopTokens, sysPromptTokens, ct))
        {
            _tokenStreamCount++;
            if (_tokenStreamCount <= 3)
            {
                logger.LogWarning("[DIAG] Token #{Num}: '{Token}'", _tokenStreamCount, token);
            }
            fullResponseBuilder.Append(token);
            unyieldedText += token;

            bool processedAny;
            do
            {
                processedAny = false;
                
                if (!isToolCall)
                {
                    int thinkIndex = -1;
                    int thinkTagLen = 0;
                    if (!isThinking)
                    {
                        string[] thinkStartTags = new[] { "<think>", "<|think|>", "<thought>", "<|thought|>", "[THINK]", "[THOUGHT]", "{antml:thinking_mode}", "<antml:thinking_mode>", "{thinking_mode}", "<thinking_mode>" };
                        foreach (var tag in thinkStartTags)
                        {
                            int idx = unyieldedText.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0 && (thinkIndex < 0 || idx < thinkIndex))
                            {
                                thinkIndex = idx;
                                thinkTagLen = tag.Length;
                            }
                        }
                    }

                    int thinkEndIndex = -1;
                    int thinkEndTagLen = 0;
                    if (isThinking)
                    {
                        string[] thinkEndTags = new[] { "</think>", "</|think|>", "<|/think|>", "</thought>", "</|thought|>", "<|/thought|>", "[/THINK]", "[/THOUGHT]", "{/antml:thinking_mode}", "</antml:thinking_mode>", "{/thinking_mode}", "</thinking_mode>" };
                        foreach (var tag in thinkEndTags)
                        {
                            int idx = unyieldedText.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0 && (thinkEndIndex < 0 || idx < thinkEndIndex))
                            {
                                thinkEndIndex = idx;
                                thinkEndTagLen = tag.Length;
                            }
                        }
                    }
                    
                    int toolIndex = -1;
                    int toolTagLen = 0;
                    string[] toolStartTags = new[] { "<tool_call>", "<|tool_call|>", "<tool_calls>", "<|tool_calls|>", "[TOOL_CALLS]", "[TOOL_CALL]", "<TOOL_CALL>", "<TOOL_CALLS>" };
                    foreach (var tag in toolStartTags)
                    {
                        int idx = unyieldedText.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0 && (toolIndex < 0 || idx < toolIndex))
                        {
                            toolIndex = idx;
                            toolTagLen = tag.Length;
                        }
                    }

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
                        unyieldedText = unyieldedText.Substring(thinkIndex + thinkTagLen);
                        processedAny = true;
                    }
                    else if (earliest == thinkEndIndex)
                    {
                        string before = unyieldedText.Substring(0, thinkEndIndex);
                        if (!string.IsNullOrEmpty(before))
                            yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, before);
                        
                        isThinking = false;
                        yield return new ChatStreamEvent(ChatStreamEventType.ThinkingEnd, "");
                        unyieldedText = unyieldedText.Substring(thinkEndIndex + thinkEndTagLen);
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
                        unyieldedText = unyieldedText.Substring(toolIndex + toolTagLen);
                        processedAny = true;
                    }
                }
                else // isToolCall == true
                {
                    int toolEndIndex = -1;
                    int toolEndTagLen = 0;
                    string[] toolEndTags = new[] { "</tool_call>", "</|tool_call|>", "<|/tool_call|>", "</tool_calls>", "</|tool_calls|>", "<|/tool_calls|>", "[/TOOL_CALLS]", "[/TOOL_CALL]", "</TOOL_CALL>", "</TOOL_CALLS>" };
                    foreach (var tag in toolEndTags)
                    {
                        int idx = unyieldedText.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0 && (toolEndIndex < 0 || idx < toolEndIndex))
                        {
                            toolEndIndex = idx;
                            toolEndTagLen = tag.Length;
                        }
                    }
                        
                    if (toolEndIndex >= 0)
                    {
                        isToolCall = false;
                        unyieldedText = unyieldedText.Substring(toolEndIndex + toolEndTagLen);
                        processedAny = true;
                    }
                }
            } while (processedAny);

            // Yield safe text (avoiding cut-off partial tags)
            if (!string.IsNullOrEmpty(unyieldedText))
            {
                if (isToolCall)
                {
                    // Let unyieldedText accumulate so we can find closing tool tag in the next iteration.
                    // Do not yield the raw tool JSON to the UI, and do not clear it!
                    continue;
                }

                string[] tagsToCheck = isThinking ? 
                    new[] { "</think>", "</|think|>", "<|/think|>", "</thought>", "</|thought|>", "<|/thought|>", "[/THINK]", "[/THOUGHT]", "<tool_call>", "<|tool_call|>", "[TOOL_CALLS]", "[TOOL_CALL]" } : 
                    new[] { "<think>", "<|think|>", "<thought>", "<|thought|>", "[THINK]", "[THOUGHT]", "<tool_call>", "<|tool_call|>", "[TOOL_CALLS]", "[TOOL_CALL]" };
                                       
                bool endsWithPartial = false;
                int maxPartialLen = 0;
                
                foreach (var tag in tagsToCheck)
                {
                    for (int len = 1; len < tag.Length; len++)
                    {
                        var prefix = tag.Substring(0, len);
                        if (unyieldedText.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            endsWithPartial = true;
                            if (len > maxPartialLen)
                            {
                                maxPartialLen = len;
                            }
                        }
                    }
                }
                
                if (endsWithPartial && maxPartialLen > 0)
                {
                    int safeLen = unyieldedText.Length - maxPartialLen;
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
        if (!string.IsNullOrEmpty(unyieldedText))
        {
            if (isToolCall)
            {
                // Stream ended inside a tool call block; suppress yielding raw JSON as plain text to UI
                unyieldedText = string.Empty;
            }
            else if (isThinking)
            {
                yield return new ChatStreamEvent(ChatStreamEventType.ThinkingToken, unyieldedText);
            }
            else
            {
                yield return new ChatStreamEvent(ChatStreamEventType.Token, unyieldedText);
            }
            unyieldedText = string.Empty;
        }

        logger.LogWarning("[DIAG] Token streaming complete. Total tokens received: {Count}, fullResponse.Length={Len}", _tokenStreamCount, fullResponseBuilder.Length);

            var fullResponse = fullResponseBuilder.ToString();
            logger.LogWarning("[DIAG] fullResponse (first 200 chars): {Resp}", fullResponse.Length > 200 ? fullResponse.Substring(0, 200) : fullResponse);

            // Strip raw thinking tags and antml system tags so history stored in context does not poison future turns
            var cleanHistoryResponse = OutputSanitizer.CleanHistoryResponse(fullResponse);
            if (string.IsNullOrWhiteSpace(cleanHistoryResponse))
            {
                cleanHistoryResponse = OutputSanitizer.SanitizeText(fullResponse);
            }

            var assistantMsgObj = new ChatMessage(ChatRole.Assistant, cleanHistoryResponse);
            activeHistory.Add(assistantMsgObj);
            if (CurrentSessionId == generatingSessionId && !_history.Contains(assistantMsgObj))
            {
                _history.Add(assistantMsgObj);
            }
            await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Assistant, fullResponse, 0, null);

            // Parse for tool calls
            var toolCallRequests = ParseToolCalls(fullResponse);
            
            if (toolCallRequests.Count > 0)
            {
                bool forceTurnTermination = false;

                foreach (var req in toolCallRequests)
                {
                    var argsHash = GetCanonicalArgsHash(req.Arguments);
                    var matches = _recentTools.Where(x => x.ToolName == req.Name && x.ArgsHash == argsHash).ToList();
                    var matchCount = matches.Count;

                    // Differentiate thresholds for chunking/read tools vs state-modifying tools
                    bool isReadTool = req.Name.Equals("read_file", StringComparison.OrdinalIgnoreCase) ||
                                     req.Name.Equals("view_file", StringComparison.OrdinalIgnoreCase) ||
                                     req.Name.Equals("list_directory", StringComparison.OrdinalIgnoreCase) ||
                                     req.Name.Equals("list_dir", StringComparison.OrdinalIgnoreCase) ||
                                     req.Name.Equals("grep_search", StringComparison.OrdinalIgnoreCase) ||
                                     req.Name.Equals("search_files", StringComparison.OrdinalIgnoreCase);

                    int tier1Threshold = isReadTool ? 5 : 2;   // Warning on 6th call for read tools, 3rd for others
                    int tier2Threshold = isReadTool ? 8 : 3;   // Hard block on 9th call for read tools, 4th for others
                    int tier3Threshold = isReadTool ? 12 : 5;  // Override on 13th call for read tools, 6th for others

                    if (matchCount >= tier1Threshold)
                    {
                        _consecutiveBlockedToolCalls++;
                        logger.LogWarning("Repeated tool call loop detected for {ToolName} (attempt {AttemptCount}).", req.Name, matchCount + 1);

                        string guardrailMsg;
                        var cachedResult = matches.LastOrDefault().PriorResult ?? "No prior result cached.";
                        
                        // Truncate cached result if excessively long to save context budget
                        if (cachedResult.Length > 2000)
                        {
                            cachedResult = cachedResult.Substring(0, 2000) + "\n...[cached output truncated for length]...";
                        }

                        if (matchCount < tier2Threshold)
                        {
                            // Tier 1: Soft Warning + Cached Output Injection
                            guardrailMsg = $"[System Warning: Duplicate tool call detected for '{req.Name}' (attempt {matchCount + 1}). Below is the cached result from your previous call. Use this cached data to complete your plan or adjust offset/line arguments to read the next section.\n\n--- CACHED TOOL RESULT ---\n{cachedResult}]";
                        }
                        else if (matchCount >= tier2Threshold && matchCount < tier3Threshold)
                        {
                            // Tier 2: Hard Block + Suggested Alternatives
                            guardrailMsg = $"[System ERROR: BLOCKED — Tool '{req.Name}' with identical arguments was attempted {matchCount + 1} times. Execution has been blocked. You MUST NOT call this tool again with these exact parameters.\n\nSuggested Next Steps:\n1. If chunking a large file, update your StartLine/EndLine or ContentOffset parameters to read a DIFFERENT line range or section.\n2. Use the cached result provided in previous messages.\n3. Complete your turn and report findings to the user.\n\n--- CACHED TOOL RESULT ---\n{cachedResult}]";
                        }
                        else
                        {
                            // Tier 3: Turn Termination — Force loop exit & turn response
                            guardrailMsg = $"[SYSTEM OVERRIDE: Persistent loop detected on '{req.Name}' after {matchCount + 1} attempts. Tool calling is SUSPENDED for this turn. You MUST now synthesize your final response to the user using your existing context.]";
                            forceTurnTermination = true;
                        }

                        var guardrailMsgObj = new ChatMessage(ChatRole.Tool, guardrailMsg, req.Name);
                        activeHistory.Add(guardrailMsgObj);
                        if (CurrentSessionId == generatingSessionId) _history.Add(guardrailMsgObj);
                        await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Tool, guardrailMsg, 0, null);
                        yield return new ChatStreamEvent(ChatStreamEventType.Error, guardrailMsg);

                        if (forceTurnTermination || _consecutiveBlockedToolCalls >= (isReadTool ? 10 : 5))
                        {
                            logger.LogError("Consecutive tool loop blocks exceeded threshold. Terminating turn loop immediately.");
                            break;
                        }
                        continue;
                    }

                    // Reset consecutive blocked counter on genuine non-duplicate execution
                    _consecutiveBlockedToolCalls = 0;

                    yield return new ChatStreamEvent(ChatStreamEventType.ToolCall, req.Name, new Dictionary<string, object> { ["Arguments"] = req.Arguments });
                    
                    var result = await toolExecutor.ExecuteToolAsync(req, generatingSessionId, ct);
                    var toolOutput = string.IsNullOrWhiteSpace(result.Output) ? (result.Error ?? "Empty result") : result.Output;
                    
                    var currentPendingQueue = MessageQueue?.GetPending(generatingSessionId);
                    if (currentPendingQueue != null && currentPendingQueue.Count > 0)
                    {
                        var queueSummaries = string.Join("; ", currentPendingQueue.Select(m => $"[ID: {m.Id}, Mode: {m.Mode}]: \"{m.Content}\""));
                        toolOutput += $"\n\n[SYSTEM NOTIFICATION: You have {currentPendingQueue.Count} pending queued message(s) waiting: {queueSummaries}. Call tool 'incorporate_queued_message' with argument {{\"queue_id\": \"<ID>\"}} to inspect and incorporate them into your workflow.]";
                    }

                    _recentTools.Add((req.Name, argsHash, toolOutput));

                    var toolOutputObj = new ChatMessage(ChatRole.Tool, toolOutput, req.Name);
                    activeHistory.Add(toolOutputObj);
                    if (CurrentSessionId == generatingSessionId) _history.Add(toolOutputObj);
                    await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Tool, toolOutput, 0, null);
                    
                    yield return new ChatStreamEvent(ChatStreamEventType.ToolResult, toolOutput, new Dictionary<string, object> { ["Success"] = result.Success });
                }

                if (forceTurnTermination || _consecutiveBlockedToolCalls >= 5)
                {
                    break;
                }
            }
            else if (Regex.IsMatch(fullResponse, @"<\|?tool_call\|?>", RegexOptions.IgnoreCase))
            {
                // Assistant attempted a tool call tag but parsing produced 0 valid requests.
                // Do not exit loop prematurely; provide feedback so the model can self-correct on the next iteration.
                logger.LogWarning("Assistant emitted <tool_call> tag but JSON parsing failed.");
                var parseErrorMsg = "[Tool Error: Failed to parse <tool_call> JSON. Please ensure arguments are valid JSON with 'name' and 'arguments'.]";
                var parseErrMsgObj = new ChatMessage(ChatRole.Tool, parseErrorMsg);
                activeHistory.Add(parseErrMsgObj);
                if (CurrentSessionId == generatingSessionId) _history.Add(parseErrMsgObj);
                await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Tool, parseErrorMsg, 0, null);
                yield return new ChatStreamEvent(ChatStreamEventType.Error, parseErrorMsg);
            }
            else
            {
                // Check if generation ended prematurely mid-response (truncated before completing output)
                bool isTruncatedMidGeneration = IsTruncatedMidGeneration(fullResponse);
                if (isTruncatedMidGeneration && iterationCount < MAX_ITERATIONS)
                {
                    logger.LogInformation("Output generation cut off mid-sentence/section. Triggering auto-continuation iteration.");
                    var continuationInstruction = "[System Instruction: Your previous output was truncated mid-generation due to output token constraints. Continue immediately from the exact point of truncation without repeating any previously written text.]";
                    var continuationMsgObj = new ChatMessage(ChatRole.User, continuationInstruction);
                    activeHistory.Add(continuationMsgObj);
                    if (CurrentSessionId == generatingSessionId) _history.Add(continuationMsgObj);
                    await messageStore.AddMessageAsync(generatingSessionId, ChatRole.User, continuationInstruction, 0, null);
                }
                else
                {
                    // No tool calls, no tool call tag attempted, and response is complete
                    break;
                }
            }
        }

        if (iterationCount >= MAX_ITERATIONS)
        {
            yield return new ChatStreamEvent(ChatStreamEventType.Error, "Max tool iterations reached.");
        }

        yield return new ChatStreamEvent(ChatStreamEventType.StreamEnd, "");
    }

    private static bool IsTruncatedMidGeneration(string fullResponse)
    {
        if (string.IsNullOrWhiteSpace(fullResponse)) return false;

        var trimmed = fullResponse.TrimEnd();
        if (trimmed.Length == 0) return false;

        // 1. Incomplete/truncated tool call tag (e.g. model started emitting <tool_call... but got cut off before tag closed)
        if ((trimmed.Contains("<tool_call") && !trimmed.Contains("</tool_call>") && !trimmed.Contains("/>")) ||
            (trimmed.Contains("<|tool_call") && !trimmed.Contains("</|tool_call|>") && !trimmed.Contains("<|/tool_call|>")))
        {
            return true;
        }

        // 2. Interrupted markdown section header or table divider cut off mid-structure
        if (trimmed.EndsWith("----------------------------------------") ||
            trimmed.EndsWith("========================================"))
        {
            return true;
        }

        return false;
    }

    private string GetCanonicalArgsHash(IDictionary<string, object>? args)
    {
        if (args == null || args.Count == 0) return "";
        var sortedDict = new SortedDictionary<string, string>();
        foreach (var kvp in args)
        {
            var valStr = ToolExecutor.UnwrapJsonElement(kvp.Value)?.ToString() ?? "";
            sortedDict[kvp.Key] = valStr.Trim();
        }
        return JsonSerializer.Serialize(sortedDict);
    }

    private static string SanitizeJsonControlCharacters(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        var sb = new StringBuilder(json.Length + 16);
        bool inString = false;
        bool isEscaped = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                if (isEscaped)
                {
                    sb.Append(c);
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    sb.Append(c);
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    sb.Append(c);
                    inString = false;
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else if (c == '\r')
                {
                    sb.Append("\\r");
                }
                else if (c == '\t')
                {
                    sb.Append("\\t");
                }
                else if (c < 0x20)
                {
                    sb.Append($"\\u{(int)c:x4}");
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inString = true;
                }
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, object> UnwrapArgs(Dictionary<string, object> rawArgs)
    {
        var result = new Dictionary<string, object>();
        foreach (var kvp in rawArgs)
        {
            var val = ToolExecutor.UnwrapJsonElement(kvp.Value);
            if (val != null)
            {
                result[kvp.Key] = val;
            }
        }
        return result;
    }

    private List<ToolCallRequest> ParseToolCalls(string response)
    {
        var results = new List<ToolCallRequest>();
        if (string.IsNullOrWhiteSpace(response)) return results;

        var blocksToParse = new List<string>();

        // 1. Native <tool_call> / <tool_calls> JSON format (supports singular/plural, nested braces, and missing end tag)
        var matches = Regex.Matches(response, @"<\|?tool_calls?\|?>(.*?)(?:</\|?tool_calls?\|?>|<\|/tool_calls?\|?>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var rawContent = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(rawContent))
            {
                blocksToParse.Add(rawContent);
            }
        }

        // 2. Bracketed [TOOL_CALLS] or [TOOL_CALL] [...] format
        if (blocksToParse.Count == 0)
        {
            var toolCallsMatches = Regex.Matches(response, @"\[TOOL_CALLS?\]\s*(\[.*?\]|\{[\s\S]*?\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in toolCallsMatches)
            {
                var rawContent = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(rawContent))
                {
                    blocksToParse.Add(rawContent);
                }
            }
        }

        // 3. Markdown ```json code blocks containing tool invocation keys
        if (blocksToParse.Count == 0)
        {
            var codeBlockMatches = Regex.Matches(response, @"```(?:json)?\s*(\{[\s\S]*?\}|\[[\s\S]*?\])\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in codeBlockMatches)
            {
                var block = match.Groups[1].Value.Trim();
                if (block.Contains("\"name\"", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"tool\"", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"action\"", StringComparison.OrdinalIgnoreCase) ||
                    block.Contains("\"function\"", StringComparison.OrdinalIgnoreCase))
                {
                    blocksToParse.Add(block);
                }
            }
        }

        // 4. Fallback: Raw un-tagged JSON objects containing name/tool/action/function
        if (blocksToParse.Count == 0)
        {
            var rawJsonMatches = Regex.Matches(response, @"(\{[\s\S]*?""(?:name|tool|action|function)""\s*:\s*""[^""]+""[\s\S]*?\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match match in rawJsonMatches)
            {
                var block = match.Groups[1].Value.Trim();
                if (!blocksToParse.Contains(block))
                {
                    blocksToParse.Add(block);
                }
            }
        }

        foreach (var rawContent in blocksToParse)
        {
            try
            {
                // Decode HTML entities (e.g. &quot;, &lt;, &gt;)
                var content = System.Net.WebUtility.HtmlDecode(rawContent).Trim();

                // Cleanup common markdown mistakes (e.g. ```json ... ```)
                if (content.StartsWith("```json", StringComparison.OrdinalIgnoreCase)) content = content.Substring(7);
                else if (content.StartsWith("```", StringComparison.OrdinalIgnoreCase)) content = content.Substring(3);
                if (content.EndsWith("```", StringComparison.OrdinalIgnoreCase)) content = content.Substring(0, content.Length - 3);
                content = content.Trim();

                int firstBrace = content.IndexOf('{');
                int firstBracket = content.IndexOf('[');

                if (firstBracket >= 0 && (firstBrace < 0 || firstBracket < firstBrace))
                {
                    int lastBracket = content.LastIndexOf(']');
                    if (lastBracket > firstBracket)
                    {
                        content = content.Substring(firstBracket, lastBracket - firstBracket + 1);
                    }
                    else
                    {
                        content = content.Substring(firstBracket) + "]";
                    }
                }
                else if (firstBrace >= 0)
                {
                    int lastBrace = content.LastIndexOf('}');
                    if (lastBrace > firstBrace)
                    {
                        content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
                    }
                    else
                    {
                        content = content.Substring(firstBrace) + "}";
                    }
                }

                if (string.IsNullOrWhiteSpace(content)) continue;

                // Sanitize raw unescaped newlines/tabs inside double-quoted string literals before JSON parsing
                var sanitizedContent = SanitizeJsonControlCharacters(content);

                int parsedCountBefore = results.Count;

                try
                {
                    using var doc = JsonDocument.Parse(sanitizedContent, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in doc.RootElement.EnumerateArray())
                        {
                            var req = ProcessToolCallJsonElement(element);
                            if (req != null) results.Add(req);
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var req = ProcessToolCallJsonElement(doc.RootElement);
                        if (req != null) results.Add(req);
                    }
                }
                catch (JsonException jsonEx)
                {
                    logger.LogWarning(jsonEx, "JsonDocument parsing failed for <tool_call>; attempting fallback extraction.");
                }

                // Fallback loose regex extraction for name and arguments if JsonDocument parsing produced 0 new requests
                if (results.Count == parsedCountBefore)
                {
                    var nameMatch = Regex.Match(content, @"""(?:name|function|tool|action)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (nameMatch.Success)
                    {
                        var fallbackName = nameMatch.Groups[1].Value;
                        var fallbackArgs = new Dictionary<string, object>();

                        var argMatches = Regex.Matches(content, @"""([a-zA-Z0-9_]+)""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
                        foreach (Match m in argMatches)
                        {
                            var key = m.Groups[1].Value;
                            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("function", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("action", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("type", StringComparison.OrdinalIgnoreCase)) continue;
                            var val = m.Groups[2].Value;
                            fallbackArgs[key] = val;
                        }

                        if (!string.IsNullOrEmpty(fallbackName))
                        {
                            results.Add(new ToolCallRequest(fallbackName, fallbackArgs));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to parse <tool_call> JSON");
            }
        }

        // 5. Final Fallback: Extract narrative simulated tool calls like "- tool_name \n - Input: { ... }"
        if (results.Count == 0 && !string.IsNullOrWhiteSpace(response))
        {
            var simulatedInputMatches = Regex.Matches(response, @"(?:^|\n|\r)\s*(?:-\s*|\*\*\s*|tool:\s*)?([a-zA-Z0-9_.-]+)\s*[\r\n\s]*(?:-\s*)?(?:Input|arguments|params)\s*:\s*(\{[\s\S]*?\})", RegexOptions.IgnoreCase);
            foreach (Match m in simulatedInputMatches)
            {
                var toolName = m.Groups[1].Value.Trim();
                var rawArgsJson = m.Groups[2].Value.Trim();
                try
                {
                    var rawDict = JsonSerializer.Deserialize<Dictionary<string, object>>(rawArgsJson);
                    if (rawDict != null)
                    {
                        var args = UnwrapArgs(rawDict);
                        results.Add(new ToolCallRequest(toolName, args));
                    }
                }
                catch
                {
                    // Ignore fallback parse failures
                }
            }
        }

        return results;
    }

    private static ToolCallRequest? ProcessToolCallJsonElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        JsonElement targetElement = element;
        if (element.TryGetProperty("function", out var funcObj) && funcObj.ValueKind == JsonValueKind.Object)
        {
            targetElement = funcObj;
        }

        string? name = null;
        if (targetElement.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String) name = nameProp.GetString();
        else if (targetElement.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.String) name = fnProp.GetString();
        else if (targetElement.TryGetProperty("tool", out var toolProp) && toolProp.ValueKind == JsonValueKind.String) name = toolProp.GetString();
        else if (targetElement.TryGetProperty("action", out var actionProp) && actionProp.ValueKind == JsonValueKind.String) name = actionProp.GetString();

        if (string.IsNullOrEmpty(name)) return null;

        var args = new Dictionary<string, object>();
        JsonElement argsProp = default;
        bool foundArgsProp = false;

        foreach (var propName in new[] { "arguments", "parameters", "args", "params", "action_input" })
        {
            if (targetElement.TryGetProperty(propName, out argsProp))
            {
                foundArgsProp = true;
                break;
            }
        }

        if (foundArgsProp)
        {
            if (argsProp.ValueKind == JsonValueKind.Object)
            {
                var rawDict = JsonSerializer.Deserialize<Dictionary<string, object>>(argsProp.GetRawText());
                if (rawDict != null) args = UnwrapArgs(rawDict);
            }
            else if (argsProp.ValueKind == JsonValueKind.String)
            {
                var str = argsProp.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    var sanitizedStr = SanitizeJsonControlCharacters(str);
                    try
                    {
                        var rawDict = JsonSerializer.Deserialize<Dictionary<string, object>>(sanitizedStr);
                        if (rawDict != null) args = UnwrapArgs(rawDict);
                    }
                    catch
                    {
                        // Ignore string parse errors
                    }
                }
            }
        }
        else
        {
            foreach (var prop in targetElement.EnumerateObject())
            {
                var pName = prop.Name.ToLowerInvariant();
                if (pName == "name" || pName == "function" || pName == "tool" || pName == "action" || pName == "type")
                    continue;

                var val = ToolExecutor.UnwrapJsonElement(prop.Value);
                if (val != null)
                {
                    args[prop.Name] = val;
                }
            }
        }

        return new ToolCallRequest(name, args);
    }

    /// <summary>
    /// Generates a short title for the conversation based on the first messages.
    /// </summary>
    public async Task<string> GenerateTitleAsync(string userMessage, string assistantResponse, CancellationToken ct = default)
    {
        try
        {
            if (IsGenerating)
            {
                logger.LogInformation("ChatEngine is generating; deriving title from user message to prevent lock contention.");
                return TitleSanitizer.DeriveTitleFromMessage(userMessage);
            }

            var cleanUser = TitleSanitizer.PrepareTextForPrompt(userMessage);
            var cleanAssistant = TitleSanitizer.PrepareTextForPrompt(assistantResponse);

            if (string.IsNullOrWhiteSpace(cleanUser))
            {
                return "New Chat";
            }

            var sysPrompt = "You are an AI assistant that creates clean, concise 2-5 word titles summarizing user conversations. Do NOT use quotes, markdown, bullet points, special characters, or prefixes (like 'Title:'). Output ONLY the raw title text.";
            var userPrompt = $"User: {cleanUser}\nAssistant: {cleanAssistant}\n\nTitle:";

            var templateType = promptEngine.DetectTemplate(
                inferenceEngine.Architecture, 
                inferenceEngine.CurrentModelPath, 
                inferenceEngine.RawChatTemplate, 
                inferenceEngine.FineTuneName);
            var messages = new List<ChatMessage> 
            { 
                new(ChatRole.System, sysPrompt),
                new(ChatRole.User, userPrompt)
            };

            var prompt = promptEngine.ApplyTemplate(messages, templateType);
            
            var generatedText = await inferenceEngine.GenerateTextAsync(prompt, isIsolated: true, ct);
            
            var sanitizedTitle = TitleSanitizer.SanitizeTitle(generatedText);
            
            if (string.IsNullOrWhiteSpace(sanitizedTitle) || sanitizedTitle == "New Chat")
                return TitleSanitizer.DeriveTitleFromMessage(userMessage);

            return sanitizedTitle;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate title.");
            return TitleSanitizer.DeriveTitleFromMessage(userMessage);
        }
    }

    private static string DeriveTitleFromMessage(string userMessage)
    {
        return TitleSanitizer.DeriveTitleFromMessage(userMessage);
    }
}
