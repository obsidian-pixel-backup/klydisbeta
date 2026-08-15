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

    /// <summary>
    /// True when the loaded model uses a Mixture-of-Experts architecture (qwen35moe, mixtral,
    /// deepseek-v2/v3, ...), which gets the stricter MoE sampling profile and stability
    /// directives in the system prompt.
    /// </summary>
    bool IsMixtureOfExperts { get; }

    /// <summary>
    /// Set when the most recent chat-path generation was stopped because the degenerate-loop
    /// detector fired; null when the last generation was clean. Read after the token stream ends.
    /// </summary>
    GenerationLoopInfo? LastGenerationLoopInfo { get; }

    /// <summary>
    /// True when the most recent chat-path generation was cut off because it exhausted its
    /// MaxTokens output budget (vs. ending on a stop token or being cancelled). Read after the
    /// token stream ends to decide whether an auto-continuation is warranted.
    /// </summary>
    bool LastGenerationHitMaxTokens { get; }
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _pendingConsolidations = new();
    private int _consecutiveBlockedToolCalls = 0;

    public Klydis.Core.RAG.VectorStore? VectorStore { get; set; } = vectorStore;
    
    /// <summary>
    /// Calculates the rolling compression threshold as 60% of the model's total context size.
    /// Reduced from 75% to catch overflow earlier, since the system prompt alone can be 30-40%
    /// of context when skills are loaded.
    /// </summary>
    private int GetRollingCompressionThreshold()
    {
        int contextSize = (int)inferenceEngine.ContextSize;
        return Math.Clamp((int)(contextSize * 0.60), 2048, 1000000);
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
    /// <summary>
    /// True for engine-injected feedback messages (self-corrections, continuation notices)
    /// that older builds persisted as regular User messages. They are ephemeral guidance for
    /// one iteration and must never govern later turns, so they are filtered out whenever
    /// session history is loaded. Without this, a stale "answer in one short sentence"
    /// correction kept overriding the user's actual request for days (observed across
    /// sessions in production logs).
    /// </summary>
    public static bool IsEngineInjectedMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.StartsWith("[System Self-Correction:", StringComparison.Ordinal) ||
               content.StartsWith("[System Instruction:", StringComparison.Ordinal);
    }

    public void LoadHistory(IEnumerable<ChatMessage> history, string sessionId)
    {
        var targetId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId;
        var histList = history
            .Where(m => !IsEngineInjectedMessage(m.Content) &&
                        !(m.Role == ChatRole.Assistant && string.IsNullOrWhiteSpace(m.Content)))
            .ToList();
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

        // C6: Complete any deferred WorldState consolidation from a previous turn BEFORE this
        // turn reads WorldState, so prompt builds never race with consolidation writes.
        // (Fixes a long-horizon race where turn N+1's system prompt was built from a
        // half-written WorldState.)
        if (_pendingConsolidations.TryRemove(generatingSessionId, out var priorConsolidation))
        {
            try { await priorConsolidation.ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning(ex, "Prior world-state consolidation failed."); }
        }

        if (!_sessionHistories.TryGetValue(generatingSessionId, out var activeHistory))
        {
            activeHistory = new List<ChatMessage>(_history);
            _sessionHistories[generatingSessionId] = activeHistory;
        }

        var userMsgObj = new ChatMessage(ChatRole.User, userMessage);
        activeHistory.Add(userMsgObj);
        // ChatMessage is a value-equality record, so Contains() would skip legitimate duplicate
        // content (the user re-asking the same question, two identical assistant replies). The
        // mirror must stay in sync with activeHistory — add unconditionally.
        if (CurrentSessionId == generatingSessionId)
        {
            _history.Add(userMsgObj);
        }

        await messageStore.AddMessageAsync(generatingSessionId, ChatRole.User, userMessage, 0, null);
        
        var enumerator = StreamResponseInternalAsync(generatingSessionId, activeHistory, userMessage, ct, skillContext, activeGoalMode).GetAsyncEnumerator(ct);
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
        string currentUserMessage,
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

        // Qwen3.5/Qwen3.6 thinking models (qwen35 / qwen35moe architectures): their embedded chat
        // template expects (a) tools presented as an OpenAI-style JSON schema inside a <tools>
        // block with the native <tool_call><function=...><parameter=...> calling instructions, and
        // (b) the generation prompt to END with an OPEN <think> block (the model continues it,
        // then closes it — without the opener it degenerates into spamming <think>). Applying
        // both makes these models think and call tools correctly.
        bool isQwenThinkingModel = templateType == ChatTemplate.Qwen &&
                                   inferenceEngine.Architecture?.Contains("qwen35", StringComparison.OrdinalIgnoreCase) == true &&
                                   !string.IsNullOrWhiteSpace(inferenceEngine.RawChatTemplate) &&
                                   inferenceEngine.RawChatTemplate.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

        int iterationCount = 0;
        const int MAX_ITERATIONS = 100;

        // Auto-continuation ("your output was truncated — continue") budget: long-form
        // generations (stories, reports) are resumed across chunks, so the instruction may be
        // injected multiple times per user turn — but bounded, because each continuation
        // rebuilds and re-prefills the whole prompt and a model that keeps ending mid-sentence
        // would otherwise crawl. See the truncation branch below.
        int continuationsThisTurn = 0;
        const int MaxContinuationsPerTurn = 16;

        // Self-correction budget: MoE / thinking models can fall into degenerate loops
        // (think-tag spam, token stutter, n-gram cycles). When GenerationLoopDetector fires,
        // the looped tail is discarded, a corrective instruction is injected, and the turn is
        // regenerated — but only a bounded number of times per user turn, so a pathological
        // model cannot spin forever (each correction rebuilds and re-prefills the whole prompt).
        int selfCorrectionsThisTurn = 0;
        const int MaxSelfCorrectionsPerTurn = 3;

        // Rescue mode: after escalating corrections are exhausted and the model is STILL
        // degenerate, one final generation strips the tools and the pre-opened think block and
        // demands a plain direct answer — so the user gets a coherent response instead of an
        // empty failed turn. Exactly one rescue attempt per turn (rescueTriggered latches it).
        bool rescueTriggered = false;
        bool rescueRequested = false;
        var rescueSysMsg = new ChatMessage(ChatRole.System,
            "You are Klydis. Answer the user's latest message directly and concisely in plain text. " +
            "Do not use tools, thinking blocks, tags, or formatting. Just give a clear, short answer.");

        // Token-count cache: the per-iteration budget math below tokenizes the whole active
        // history over and over (rolling-compression check, backward budget pass, truncation
        // loop). With a large context model and a 100-iteration tool loop that is
        // O(iterations x history) tokenizer work per user message, and it becomes the dominant
        // cost once a session grows to hundreds of thousands of tokens. Cache per-message
        // counts keyed by reference so each message is tokenized at most once per user message.
        // ChatMessage is a value-equality record, so reference identity is required to avoid
        // collapsing distinct messages that happen to share identical content.
        var tokenCache = new Dictionary<ChatMessage, int>(ReferenceEqualityComparer.Instance);
        int TokensOf(ChatMessage m)
        {
            if (tokenCache.TryGetValue(m, out var cached)) return cached;
            int t = (inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(m.Content) : contextOrchestrator.EstimateTokens(m.Content)) + 25; // 25 tokens for template formatting overhead
            tokenCache[m] = t;
            return t;
        }

        while (iterationCount < MAX_ITERATIONS)
        {
            iterationCount++;
            
            // Execute automated rolling compression when history tokens reach the threshold.
            int rollingThreshold = GetRollingCompressionThreshold();
            int estimatedHistoryTokens = activeHistory.Sum(TokensOf);
            if (estimatedHistoryTokens >= rollingThreshold)
            {
                yield return new ChatStreamEvent(ChatStreamEventType.MemorySummarizing, "🧠 Summarizing conversation context and saving to memory...");
                int keepRecent = Math.Clamp((int)(inferenceEngine.ContextSize * 0.25), 2048, 262144);
                logger.LogInformation("Active history tokens ({Tokens}) reached rolling threshold ({Threshold}, 60% of {Ctx} context). Compressing older context into WorldState. Keeping {KeepRecent} recent tokens.",
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

        // Guaranteed floor for conversation history in the prompt. When the system prompt cannot
        // fit while leaving this much room, the compact system prompt is used instead of the full
        // master prompt — otherwise the ~40K-token combined prompt overflows small contexts, the
        // truncation loop strips ALL user history, and the model answers the session's first
        // message on every turn instead of the current one ("every message is a new beginning").
        int minUserBudget = Math.Clamp(maxTotalPromptTokens / 4, 512, 2048);

        // MoE models (qwen3.6-14B-A3B / qwen35moe, mixtral, deepseek-v2/v3, ...) are prone to
        // repetition attractors and tangential drift — and the full 20K-char master prompt (43KB
        // with the tool schema) consumes most of a small context window and measurably pushes
        // these fragile models into loops and empty responses. They get a COMPACT system prompt
        // that keeps the persona, personality, tool rules, and stability directives in a fraction
        // of the space (verified live: compact ~2/3 clean vs full ~1/3 on qwen3.6). Dense models
        // keep the full master prompt UNLESS it cannot fit alongside a usable conversation
        // window (see the budget check below), in which case they also fall back to the compact
        // prompt so session history is never starved out of the context window.
        string sysPrompt;
        if (isQwenThinkingModel && !string.IsNullOrWhiteSpace(toolsSchema))
        {
            // Qwen thinking models get their native tools prelude PREPENDED. The prelude embeds
            // the full ~17KB tools schema and teaches the model's native
            // <tool_call><function=...><parameter=...> format, so the base prompt must (a) NOT
            // re-embed the schema — duplicating it bloats the system prompt to ~37KB and pushes
            // fragile MoE models (qwen3.6-14B-A3B) into hard repetition loops (verified live:
            // dedup 3/3 clean vs doubled 1/3) — and (b) NOT teach the conflicting JSON
            // <tool_call>{"name":...} format, which makes the model flip-flop between the two
            // calling styles and destabilize. The compact base is lean and conflict-free, so it
            // is used for BOTH dense and MoE qwen thinking models.
            var compactBase = sysPromptManager.BuildCompactSystemPrompt("", worldStateHeader, queueNotice, ragNotice, skillHeader, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
            sysPrompt = promptEngine.BuildQwenToolsPrelude(toolsSchema) + "\n\n" + compactBase;
        }
        else if (inferenceEngine.IsMixtureOfExperts)
        {
            sysPrompt = sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
        }
        else
        {
            var fullPrompt = sysPromptManager.BuildCombinedPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
            int fullPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(fullPrompt) : contextOrchestrator.EstimateTokens(fullPrompt);
            sysPrompt = fullPromptTokens > maxTotalPromptTokens - minUserBudget
                ? sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, personalityMode: SelectedPersonality, isGoalMode: isGoalMode)
                : fullPrompt;
        }

        var sysPromptMsg = new ChatMessage(ChatRole.System, sysPrompt);
        
        // Calculate system prompt size
        var sysOnlyPrompt = promptEngine.ApplyTemplate(new List<ChatMessage> { sysPromptMsg }, templateType, qwenThinking: isQwenThinkingModel);
        int sysPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(sysOnlyPrompt) : contextOrchestrator.EstimateTokens(sysOnlyPrompt);

        // Target user budget for conversation history after accounting for the system prompt.
        // Never claims more than the context can actually hold — the old Math.Max(4096, ...)
        // floor demanded 4K of user history even when the system prompt left almost no room,
        // which forced the strict truncation loop below to strip history (including, worst of
        // all, the current user message).
        int targetUserBudget = Math.Clamp(maxTotalPromptTokens - sysPromptTokens, minUserBudget, maxTotalPromptTokens);

        var activeMessages = new List<ChatMessage>();
        int currentTokens = 0; // System prompt is excluded from user history budget
        bool hasDroppedMessages = false;

        ChatMessage? initialUserMsg = activeHistory.Count > 0 ? activeHistory[0] : null;
        int initialUserTokens = initialUserMsg != null ? ((inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(initialUserMsg.Content) : contextOrchestrator.EstimateTokens(initialUserMsg.Content)) + 25) : 0;
        
        // Reserve budget up front for the user's initial prompt goal (activeHistory[0])
        currentTokens += initialUserTokens;

        // Also reserve the CURRENT user message up front — the message this turn must answer,
        // identified by its exact text (it is added to the history by StreamResponseAsync, so the
        // last matching User message is always the current turn's message, even after the rolling
        // compression rewrites the list or tool continuations append after it). Without this, the
        // backward budget pass (and the strict truncation loop below) could evict it, leaving the
        // model to answer the session's first message on every turn (the "model treats every
        // message as a new beginning" bug).
        int currentUserIndex = -1;
        for (int i = activeHistory.Count - 1; i >= 1; i--)
        {
            if (activeHistory[i].Role == ChatRole.User && activeHistory[i].Content == currentUserMessage)
            {
                currentUserIndex = i;
                break;
            }
        }
        ChatMessage? currentUserMsg = currentUserIndex >= 0 ? activeHistory[currentUserIndex] : null;
        int currentUserTokens = currentUserMsg != null ? TokensOf(currentUserMsg) : 0;
        currentTokens += currentUserTokens;

        // Iterate backwards from the most recent history message down to index 1, skipping the
        // initial goal (index 0) and the already-reserved current user message. Older messages
        // are dropped first when the budget runs out, so the tail (tool continuations after the
        // user's message) and the current message itself always survive.
        int messagesBeforeCurrent = 0;
        for (int i = activeHistory.Count - 1; i >= 1; i--)
        {
            if (i == currentUserIndex) continue;
            var msg = activeHistory[i];
            int msgTokens = TokensOf(msg);
            
            // If an individual tool result message is excessively long (> 3000 chars), create a
            // budget-trimmed variant rather than dropping the whole turn. Tool results are stored
            // with ChatRole.Tool; legacy sessions may have them as ChatRole.User with a
            // "[Tool Output" prefix. Without this, one huge tool result (large file read, big
            // directory listing) silently evicts ALL older messages from the active window, which
            // destroys long-horizon coherence.
            bool isToolOutputMessage = msg.Role == ChatRole.Tool ||
                                       (msg.Role == ChatRole.User && msg.Content.Contains("[Tool Output", StringComparison.OrdinalIgnoreCase));
            if (isToolOutputMessage && msg.Content.Length > 3000)
            {
                string trimmedContent = msg.Content.Substring(0, 2500) + "\n...[Tool output truncated to preserve active context budget]...";
                msg = new ChatMessage(msg.Role, trimmedContent, msg.Name);
                msgTokens = TokensOf(msg);
            }

            if (currentTokens + msgTokens <= targetUserBudget)
            {
                activeMessages.Insert(0, msg);
                currentTokens += msgTokens;
                if (i < currentUserIndex) messagesBeforeCurrent++;
            }
            else
            {
                hasDroppedMessages = true;
                logger.LogInformation("Context limit reached. Compressing/truncating intermediate message for active prompt.");
            }
        }

        // Always preserve the user's initial prompt goal (activeHistory[0]) at index 0 of active
        // messages, and the current user message at its chronological position (right before any
        // retained tool-continuation messages that followed it).
        if (initialUserMsg != null)
        {
            activeMessages.Insert(0, initialUserMsg);
        }
        if (currentUserMsg != null)
        {
            activeMessages.Insert(1 + messagesBeforeCurrent, currentUserMsg);
        }

        if (hasDroppedMessages)
        {
            // C6: Deferred WorldState consolidation. The task is stored session-keyed and awaited
            // at the top of the next StreamResponseAsync for this session, so WorldState is never
            // read while a consolidation write is still in flight (previously this was fire-and-
            // forget, which let turn N+1 build its prompt from a half-written WorldState).
            _pendingConsolidations[generatingSessionId] = Task.Run(async () =>
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

        var prompt = promptEngine.ApplyTemplate(messages, templateType, qwenThinking: isQwenThinkingModel);
        int finalPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(prompt) : contextOrchestrator.EstimateTokens(prompt);

        // Strict safety truncation loop: Ensure total prompt tokens strictly fit inside
        // maxTotalPromptTokens. The previous implementation re-tokenized the ENTIRE (potentially
        // ~1M token) prompt after every single removal, making the trim O(removals x prompt).
        // Instead, subtract each removed message's cached token cost (the +25 formatting
        // allowance approximates the template separators), then do one exact re-tokenize after
        // the loop to guarantee the strict fit.
        //
        // Messages are removed oldest-first (index 1 upward), but the loop must stop with at
        // least the initial goal message (index 0) AND the current user message intact. The old
        // guard (Count > 1) stripped everything down to the initial message, silently dropping
        // the message the user just sent once the system prompt overflowed the budget — the
        // model then answered the session's first message on every turn.
        //
        // The current user message is inserted at 1 + messagesBeforeCurrent (right before any
        // retained tool continuations), so it is NOT necessarily the last message. The removal
        // loop must never evict it: once the oldest remaining message would be the current user
        // message, fall back to dropping the OLDEST tool continuation instead (keeping the
        // newest tool result, which the model needs for its next step).
        int currentUserPos = currentUserMsg != null ? 1 + messagesBeforeCurrent : -1;
        bool truncationTrimmed = false;
        while (finalPromptTokens > maxTotalPromptTokens && activeMessages.Count > 2)
        {
            int removeIndex = 1;
            if (removeIndex == currentUserPos)
            {
                // Oldest remaining message is the current user message — drop the oldest tool
                // continuation after it instead. Never removes the newest tool result.
                removeIndex = currentUserPos + 1;
            }
            if (removeIndex >= activeMessages.Count) break;

            var removedMsg = activeMessages[removeIndex];
            activeMessages.RemoveAt(removeIndex);
            // Removing a message BEFORE the current user message shifts it left by one.
            if (removeIndex < currentUserPos) currentUserPos--;
            hasDroppedMessages = true;
            truncationTrimmed = true;
            finalPromptTokens -= TokensOf(removedMsg);
        }
        if (truncationTrimmed)
        {
            messages = new List<ChatMessage> { sysPromptMsg };
            messages.AddRange(activeMessages);
            prompt = promptEngine.ApplyTemplate(messages, templateType, qwenThinking: isQwenThinkingModel);
            finalPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(prompt) : contextOrchestrator.EstimateTokens(prompt);

            // Rare fallback: if the subtract-based estimate still leaves the prompt over budget
            // (separator tokens heavier than the +25 allowance), keep trimming with exact
            // re-tokenization so the strict fit contract is never violated. Same invariant as the
            // main loop: never evict the initial goal or the current user message (see the
            // currentUserPos logic above — located by identity here, since value equality would
            // match an earlier identical message when the user repeats themselves).
            currentUserPos = -1;
            if (currentUserMsg != null)
            {
                for (int i = 0; i < activeMessages.Count; i++)
                {
                    if (ReferenceEquals(activeMessages[i], currentUserMsg))
                    {
                        currentUserPos = i;
                        break;
                    }
                }
            }
            while (finalPromptTokens > maxTotalPromptTokens && activeMessages.Count > 2)
            {
                int removeIndex = 1;
                if (removeIndex == currentUserPos)
                {
                    removeIndex = currentUserPos + 1;
                }
                if (removeIndex >= activeMessages.Count) break;

                activeMessages.RemoveAt(removeIndex);
                // Removing a message BEFORE the current user message shifts it left by one.
                if (removeIndex < currentUserPos) currentUserPos--;
                hasDroppedMessages = true;
                messages = new List<ChatMessage> { sysPromptMsg };
                messages.AddRange(activeMessages);
                prompt = promptEngine.ApplyTemplate(messages, templateType, qwenThinking: isQwenThinkingModel);
                finalPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(prompt) : contextOrchestrator.EstimateTokens(prompt);
            }
        }

        var fullResponseBuilder = new StringBuilder();
        // Accumulates ONLY the text actually shown to the user (non-thinking, non-tool-call).
        // Used to detect empty/degenerate responses: post-hoc tag-stripping cannot distinguish
        // reasoning inside the pre-opened think block from real content (the opener tag lives in
        // the prompt, not the stream), but the state machine knows exactly which tokens are
        // visible.
        var visibleTextBuilder = new StringBuilder();

        // The streaming tag state machine (thinking blocks, tool-call blocks, stray close-tag
        // suppression, partial-tag withholding) lives in ChatStreamParser — feed it tokens and
        // drain its events. For qwen thinking models the generation prompt already ends with an
        // OPEN <think> block, so the parser starts INSIDE that block.
        var streamParser = new ChatStreamParser(isQwenThinkingModel);

        // Stream tokens
        await foreach (var token in inferenceEngine.StreamTokensAsync(prompt, stopTokens, sysPromptTokens, ct))
        {
            fullResponseBuilder.Append(token);
            streamParser.Append(token);
            // The parser may have injected a </think> (qwen tool call inside the pre-opened
            // think block); keep the raw accumulator in sync so the sanitizer sees the close.
            fullResponseBuilder.Append(streamParser.ConsumeInjectedRawText());
            while (streamParser.TryDequeue(out var evt))
            {
                if (evt.Type == ChatStreamEventType.Token)
                {
                    visibleTextBuilder.Append(evt.Content);
                }
                yield return evt;
            }

        }

        // Flush whatever remained buffered at the end of the stream (discarding partial tool
        // JSON and stray think-close tags).
        streamParser.EndStream();
        fullResponseBuilder.Append(streamParser.ConsumeInjectedRawText());
        while (streamParser.TryDequeue(out var evt))
        {
            if (evt.Type == ChatStreamEventType.Token)
            {
                visibleTextBuilder.Append(evt.Content);
            }
            yield return evt;
        }

        var fullResponse = fullResponseBuilder.ToString();

            // ── Self-correction: degenerate-loop recovery (esp. MoE / thinking models) ──
            // MoE models (qwen3.6-14B-A3B / qwen35moe, mixtral, deepseek-v2/v3, ...) can fall
            // into repetition attractors: think-tag spam, token stutter, or n-gram loops.
            // InferenceEngine stops the token stream when GenerationLoopDetector fires and
            // exposes LastGenerationLoopInfo. Here we truncate the looped tail from the
            // response, inject a corrective instruction into history, and regenerate — bounded
            // by MaxSelfCorrectionsPerTurn so a pathological model cannot spin forever.
            var loopInfo = inferenceEngine.LastGenerationLoopInfo;
            string? loopCorrection = null;
            if (loopInfo != null)
            {
                logger.LogWarning("Degenerate generation loop detected ({Reason}): loop starts at char {LoopStart} of {Len} generated chars. Truncating looped tail.",
                    loopInfo.Reason, loopInfo.LoopStartChar, fullResponse.Length);
                if (loopInfo.LoopStartChar > 0 && loopInfo.LoopStartChar < fullResponse.Length)
                {
                    fullResponse = fullResponse.Substring(0, loopInfo.LoopStartChar);
                }
                else if (loopInfo.LoopStartChar <= 0)
                {
                    fullResponse = string.Empty; // the entire output was degenerate
                }
                // else: the loop begins at/after the last delivered char (the triggering token is
                // never streamed), so the delivered response is clean and is kept as-is.

                if (selfCorrectionsThisTurn < MaxSelfCorrectionsPerTurn)
                {
                    selfCorrectionsThisTurn++;
                    // Corrections escalate: the first is a gentle redirect, the second demands a
                    // short direct answer, the third is a final hard bound — a model that keeps
                    // re-entering the same attractor needs progressively stronger instructions.
                    loopCorrection = BuildSelfCorrectionInstruction(loopInfo.Reason, selfCorrectionsThisTurn);
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, $"⚠ {DescribeLoop(loopInfo.Reason)} detected — discarding looped output and self-correcting…");
                }
                else if (!rescueTriggered)
                {
                    // Corrections exhausted and the model is still looping — mark that a rescue
                    // generation is wanted (activated after the truncated response is stored).
                    rescueRequested = true;
                }
            }

            // Strip raw thinking tags and antml system tags so history stored in context does not poison future turns
            var cleanHistoryResponse = OutputSanitizer.CleanHistoryResponse(fullResponse);
            if (string.IsNullOrWhiteSpace(cleanHistoryResponse))
            {
                cleanHistoryResponse = OutputSanitizer.SanitizeText(fullResponse);
            }

            // CONTEXT-INJECTION FIX: for qwen thinking models the stored assistant message must be
            // the model's raw output VERBATIM (prefixed with the pre-opened <think> block). The
            // generation prompt ends with "<|im_start|>assistant\n<think>\n" and the model
            // CONTINUES that block, so storing it clean strips the reasoning and the next
            // iteration's prompt diverges from what the KV cache already evaluated -> the engine
            // takes the partial-prefix path, the M-RoPE rewind fails (InvalidInputBatch), and the
            // ENTIRE prompt (system + world state + full history) gets re-prefilled after every
            // tool call and think cycle. With verbatim storage the next prompt rebuilds EXACTLY
            // the evaluated text, the engine hits the exact-prefix path, and only the delta
            // (tool result / next message) is evaluated. The UI already renders stored messages
            // via SplitThinkingContent, which expects the raw <think> tags.
            var assistantContent = isQwenThinkingModel && !string.IsNullOrWhiteSpace(fullResponse)
                ? "<think>\n" + StripLeadingThinkOpener(fullResponse)
                : cleanHistoryResponse;

            var assistantMsgObj = new ChatMessage(ChatRole.Assistant, assistantContent);
            if (string.IsNullOrWhiteSpace(fullResponse))
            {
                // The entire output was degenerate/empty. Storing an empty assistant message
                // (or a bare "<think>\n" prefix with no content) pollutes history and confuses
                // later turns — it is skipped. The self-correction / rescue path below carries
                // the turn forward. (The old guard only skipped when loopTruncated was set, so
                // plain empty generations leaked empty assistant rows into the session.)
            }
            else
            {
                activeHistory.Add(assistantMsgObj);
                if (CurrentSessionId == generatingSessionId)
                {
                    _history.Add(assistantMsgObj);
                }
                // H7: Strip tool call blocks from the stored message so that re-summarization of
                // older context does not inject raw tool JSON into the WorldState.
                var storedResponse = isQwenThinkingModel && !string.IsNullOrWhiteSpace(fullResponse)
                    ? "<think>\n" + StripLeadingThinkOpener(fullResponse)
                    : OutputSanitizer.CleanHistoryResponse(fullResponse);
                await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Assistant, storedResponse, 0, null);
            }

            // If a degenerate loop was corrected, inject the self-correction instruction and
            // regenerate. The truncated (garbage-free) assistant message is already in history,
            // so the rebuild prompt shows the model what it wrote before it started looping.
            if (loopCorrection != null)
            {
                logger.LogWarning("Injecting self-correction instruction and regenerating (correction {Count} of {Max} this turn).", selfCorrectionsThisTurn, MaxSelfCorrectionsPerTurn);
                var correctionMsg = new ChatMessage(ChatRole.User, loopCorrection);
                activeHistory.Add(correctionMsg);
                if (CurrentSessionId == generatingSessionId)
                {
                    _history.Add(correctionMsg);
                }
                // Correction instructions are engine-internal feedback for the NEXT iteration
                // only. They must NOT be persisted to the session store: stale instructions
                // (e.g. "answer in one short sentence") kept governing the model for days and
                // poisoned every later turn (observed across sessions). See IsEngineInjectedMessage.
                continue;
            }

            if (rescueRequested)
            {
                // Activate rescue mode: strip the tools + think opener for the next generation.
                // isQwenThinkingModel gates both the qwen tools prelude and the pre-opened
                // <think> in ApplyTemplate, so flipping it off yields a plain direct answer.
                rescueTriggered = true;
                isQwenThinkingModel = false;
                sysPromptMsg = rescueSysMsg;
                logger.LogWarning("Loop corrections exhausted ({Max}) and output is still degenerate. Switching to rescue mode: plain direct answer without tools or thinking blocks.", MaxSelfCorrectionsPerTurn);
                yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ Model keeps looping — one final attempt with a plain direct answer…");
                continue;
            }

            // Parse tool calls from the think-stripped response ONLY. Reasoning inside <think>
            // blocks routinely contains JSON shaped like {"name": ...} / {"tool": ...} or
            // "Input: {...}" planning lines, which ParseToolCalls' loose fallback regexes
            // misread as real tool calls. Each phantom match executes a tool and re-runs the
            // whole turn loop (full prompt rebuild + re-inference) — the "model gets injected
            // every time it uses think tags" slowdown. Real <tool_call> tags are emitted
            // outside think blocks and survive CleanHistoryResponse untouched.
            // Visible (think-stripped) response — used for BOTH tool-call parsing and the
            // truncation heuristics, so reasoning content can never drive either decision.
            var visibleResponse = OutputSanitizer.CleanHistoryResponse(fullResponse);

            // Qwen thinking models: the generation prompt ends with an OPEN <think> block (the
            // opener lives in the PROMPT, not the stream). If the model never emits </think>,
            // the parser classifies its entire output as thinking — the thought bubble already
            // shows it, so the turn is delivered. Firing the empty-response cascade would
            // discard that output and burn up to 4 regenerations on self-corrections.
            bool qwenNeverClosedThink = isQwenThinkingModel &&
                !string.IsNullOrWhiteSpace(fullResponse) &&
                !fullResponse.Contains("</think>", StringComparison.OrdinalIgnoreCase) &&
                !fullResponse.Contains("<|/think|>", StringComparison.OrdinalIgnoreCase) &&
                !fullResponse.Contains("</thought>", StringComparison.OrdinalIgnoreCase);
            var toolCallRequests = ParseToolCalls(visibleResponse);
            
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
            else if (Regex.IsMatch(visibleResponse, @"<\|?tool_call\|?>", RegexOptions.IgnoreCase))
            {
                // Assistant attempted a tool call tag but parsing produced 0 valid requests.
                // Do not exit loop prematurely; provide feedback so the model can self-correct on the next iteration.
                logger.LogWarning("Assistant emitted <tool_call> tag but parsing failed.");
                // For qwen thinking models the tool format is NATIVE (not JSON): the model opens
                // <tool_call> and must continue with <function=NAME><parameter=K>value</parameter>
                // </function></tool_call>. Telling it to fix "JSON with 'name' and 'arguments'"
                // misleads it into the wrong format (a common failure: it emits a bare
                // <tool_call> and stops). Give format-specific feedback for each template family.
                var parseErrorMsg = isQwenThinkingModel
                    ? "[Tool Error: Your tool call is INCOMPLETE — you opened <tool_call> but did not finish it. Complete it using the native format: <tool_call><function=TOOL_NAME><parameter=ARG_NAME>value</parameter></function></tool_call>. Required parameters must be included. Do NOT use JSON inside the tags.]"
                    : "[Tool Error: Failed to parse <tool_call> JSON. Please ensure arguments are valid JSON with 'name' and 'arguments'.]";
                var parseErrMsgObj = new ChatMessage(ChatRole.Tool, parseErrorMsg);
                activeHistory.Add(parseErrMsgObj);
                if (CurrentSessionId == generatingSessionId) _history.Add(parseErrMsgObj);
                // Engine-internal parse feedback: keep it in the in-memory history for the next
                // iteration, but never persist it (see IsEngineInjectedMessage).
                yield return new ChatStreamEvent(ChatStreamEventType.Error, parseErrorMsg);
            }
            else
            {
                // Check if generation ended prematurely mid-response (truncated before completing
                // output). Two independent signals: (a) the engine exhausted its MaxTokens budget
                // (the stream was cut at the output cap — even a response that ends cleanly with a
                // period is truncated), or (b) the visible text ends mid-sentence/structure. The
                // instruction may be injected up to MaxContinuationsPerTurn per user turn so long
                // generations resume across chunks, but the budget prevents the old pathological
                // cascade (each re-injection rebuilds and re-prefills the whole prompt).
                bool hitOutputCap = inferenceEngine.LastGenerationHitMaxTokens;
                bool isTruncatedMidGeneration = IsTruncatedMidGeneration(fullResponse, visibleResponse);
                if ((isTruncatedMidGeneration || hitOutputCap) && iterationCount < MAX_ITERATIONS && continuationsThisTurn < MaxContinuationsPerTurn)
                {
                    continuationsThisTurn++;
                    logger.LogInformation("Output generation cut off (hitMaxTokens={HitCap}, midSentence={MidSentence}). Triggering auto-continuation iteration {Count}/{Max}.",
                        hitOutputCap, isTruncatedMidGeneration, continuationsThisTurn, MaxContinuationsPerTurn);
                    var continuationInstruction = "[System Instruction: Your previous output was truncated mid-generation due to output token constraints. Continue immediately from the exact point of truncation without repeating any previously written text.]";
                    var continuationMsgObj = new ChatMessage(ChatRole.User, continuationInstruction);
                    activeHistory.Add(continuationMsgObj);
                    if (CurrentSessionId == generatingSessionId) _history.Add(continuationMsgObj);
                    // Engine-internal continuation notice: in-memory only (see IsEngineInjectedMessage).
                }
                else if (string.IsNullOrWhiteSpace(visibleTextBuilder.ToString()) && !qwenNeverClosedThink && selfCorrectionsThisTurn < MaxSelfCorrectionsPerTurn)
                {
                    // Empty/degenerate response: the model closed its think block and produced no
                    // actual visible content (reasoning alone does not count — the user sees
                    // nothing). Ending the turn here would silently deliver nothing — treat it
                    // like a loop and self-correct instead.
                    selfCorrectionsThisTurn++;
                    logger.LogWarning("Model produced an empty visible response. Injecting empty-response self-correction (correction {Count} of {Max} this turn).",
                        selfCorrectionsThisTurn, MaxSelfCorrectionsPerTurn);
                    var emptyCorrection = "[System Self-Correction: Your previous response was EMPTY — you produced no actual content. Re-read the user's message carefully and respond DIRECTLY with a real answer. Do not just close tags or emit whitespace.]";
                    var emptyMsgObj = new ChatMessage(ChatRole.User, emptyCorrection);
                    activeHistory.Add(emptyMsgObj);
                    if (CurrentSessionId == generatingSessionId) _history.Add(emptyMsgObj);
                    // Engine-internal correction: in-memory only (see IsEngineInjectedMessage).
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ Model produced an empty response — self-correcting…");
                }
                else if (string.IsNullOrWhiteSpace(visibleTextBuilder.ToString()) && !qwenNeverClosedThink && !rescueTriggered)
                {
                    // Empty responses exhausted the correction budget — rescue mode: plain
                    // direct answer without tools or thinking blocks (see the rescue activation
                    // above). One final attempt so the user is never left with nothing.
                    rescueTriggered = true;
                    isQwenThinkingModel = false;
                    sysPromptMsg = rescueSysMsg;
                    logger.LogWarning("Empty-response corrections exhausted ({Max}). Switching to rescue mode: plain direct answer without tools or thinking blocks.", MaxSelfCorrectionsPerTurn);
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ Model keeps producing empty responses — one final attempt with a plain direct answer…");
                    continue;
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

    /// <summary>
    /// Builds the corrective instruction injected into history when a degenerate loop is
    /// detected. The message is reason-specific so the model knows exactly what it did wrong
    /// (tag spam vs token stutter vs phrase cycling) and how to recover.
    /// </summary>
    private static string BuildSelfCorrectionInstruction(string reason, int attempt) => attempt >= 3
        ? "[System Self-Correction: FINAL WARNING — you are STILL repeating the same output in a loop. STOP repeating immediately. Re-read the user's latest message and fulfill it with NEW content that does not repeat anything you already wrote. No thinking tags, no tool calls, no filler.]"
        : attempt == 2
            ? "[System Self-Correction: You are STILL looping on the same output. STOP generating repetitive text immediately. Re-read the user's original message and continue the task with NEW, non-repeating content — no thinking tags, no tool calls, no preamble. Your previous looped text has been discarded.]"
            : reason switch
            {
                "ToolCallSpam" => "[System Self-Correction: You began emitting tool-call tags without completing a call. Stop. Re-read the user's request, choose at most ONE tool from the available schema, and emit exactly one complete, well-formed tool call — or answer directly. Do not repeat any tags you already emitted.]",
                "TagSpam" => "[System Self-Correction: You began repeating an opening tag (e.g. <think>) without reasoning. Stop emitting tags. Re-read the user's message, reason ONCE inside a single thinking block if your format requires it, then continue the task with new content. Do not repeat any tags or text you already generated.]",
                "RepetitionStutter" => "[System Self-Correction: You began repeating the same word/token in a loop (degenerate repetition). Stop immediately. Re-read the user's message and continue with fresh, non-repeating content. Do not repeat anything you already wrote.]",
                "NGramLoop" => "[System Self-Correction: You entered a repetitive loop, cycling the same phrase over and over. Stop immediately, re-read the user's latest message, and produce fresh, non-repeating content that fulfills the user's request.]",
                "SemanticLoop" => "[System Self-Correction: You began repeating the same content over and over in different words. Stop immediately. Re-read the user's message and produce new content that fulfills the user's request without re-stating anything you already wrote.]",
                "PaddingLoop" => "[System Self-Correction: You began emitting filler/whitespace in a loop. Stop immediately and continue the task with substantive new content.]",
                "JunkOutput" => "[System Self-Correction: You began emitting garbage/random token fragments. Stop immediately. Re-read the user's latest message and continue with coherent, meaningful content that fulfills the user's request.]",
                _ => "[System Self-Correction: You began looping on the same output. Stop immediately, re-read the user's message, and continue with new, non-repeating content that fulfills the user's request.]"
            };

    /// <summary>
    /// Short human-readable description of a loop reason, used for the user-visible notice.
    /// </summary>
    private static string DescribeLoop(string reason) => reason switch
    {
        "ToolCallSpam" => "repeated tool-call tags",
        "TagSpam" => "repeated tag spam",
        "RepetitionStutter" => "token repetition",
        "NGramLoop" => "a repetitive phrase loop",
        "SemanticLoop" => "repeated content (paraphrased loop)",
        "PaddingLoop" => "filler/whitespace repetition",
        "JunkOutput" => "garbage/random token output",
        _ => "a degenerate output loop"
    };

    /// <summary>
    /// Strips a re-emitted think opener from the start of a qwen thinking model's raw stream
    /// before it is stored into history. The generation prompt already ends with an OPEN
    /// <c>&lt;think&gt;</c> block that the model continues — a known qwen quirk is re-emitting
    /// the opener anyway. Storing it verbatim would produce a double <c>&lt;think&gt;</c> prefix
    /// (<c>&lt;think&gt;\n&lt;think&gt;...&lt;/think&gt;</c>) that diverges from the evaluated
    /// prompt, breaks exact KV-prefix reuse, and confuses the parser on the next turn.
    /// </summary>
    private static string StripLeadingThinkOpener(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string trimmed = text.TrimStart();
        foreach (var opener in new[] { "<think>", "<|think|>", "<thought>", "<|thought|>" })
        {
            if (trimmed.StartsWith(opener, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(opener.Length).TrimStart();
            }
        }
        return text;
    }

    /// <summary>
    /// Decides whether a finished generation was cut off prematurely and should be continued.
    /// Truncation heuristics run against the VISIBLE (think-stripped) text so that reasoning
    /// content inside &lt;think&gt; blocks can never trigger a continuation, while an unclosed
    /// thinking block on the raw stream is itself treated as truncation (the model was cut
    /// mid-reasoning). The caller bounds the continuation to once per turn.
    /// </summary>
    private static bool IsTruncatedMidGeneration(string fullResponse, string cleanResponse)
    {
        if (string.IsNullOrWhiteSpace(fullResponse)) return false;

        var trimmed = fullResponse.TrimEnd();
        if (trimmed.Length == 0) return false;

        // 0. Unclosed thinking block: the model was cut off mid-reasoning. Even if the visible
        //    text is complete (or empty), the turn did not end naturally, so continue once.
        if (IsUnclosedThinkBlock(trimmed))
        {
            return true;
        }

        var cleanTrimmed = cleanResponse.TrimEnd();
        if (cleanTrimmed.Length == 0) return false;

        // 1. Incomplete/truncated tool call tag (e.g. model started emitting <tool_call... but got cut off before tag closed)
        if ((cleanTrimmed.Contains("<tool_call") && !cleanTrimmed.Contains("</tool_call>") && !cleanTrimmed.Contains("/>")) ||
            (cleanTrimmed.Contains("<|tool_call") && !cleanTrimmed.Contains("</|tool_call|>") && !cleanTrimmed.Contains("<|/tool_call|>")))
        {
            return true;
        }

        // 2. Interrupted markdown section header or table divider cut off mid-structure
        if (cleanTrimmed.EndsWith("----------------------------------------") ||
            cleanTrimmed.EndsWith("========================================"))
        {
            return true;
        }

        // M2: 3. Unclosed markdown code fence (odd number of triple-backtick fences)
        var fenceCount = System.Text.RegularExpressions.Regex.Matches(cleanTrimmed, @"^```", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        if (fenceCount % 2 != 0)
        {
            return true;
        }

        // M2: 4. Mid-sentence cutoff — no terminal punctuation or closing bracket on final non-empty line
        var lastNonEmpty = cleanTrimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.TrimEnd();
        if (!string.IsNullOrEmpty(lastNonEmpty) && lastNonEmpty.Length > 20)
        {
            char lastChar = lastNonEmpty[^1];
            // Ends with a word char or comma/colon — likely truncated mid-sentence
            if (char.IsLetterOrDigit(lastChar) || lastChar == ',' || lastChar == ':')
            {
                // But NOT if it's a short code snippet, URL, or identifier-like token
                bool looksLikeProse = lastNonEmpty.Contains(' ');
                if (looksLikeProse) return true;
            }
        }

        return false;
    }

    private static readonly string[] ThinkStartTags = new[]
    {
        "<think>", "<|think|>", "<thought>", "<|thought|>", "[THINK]", "[THOUGHT]",
        "{antml:thinking_mode}", "<antml:thinking_mode>", "{thinking_mode}", "<thinking_mode>"
    };

    private static readonly string[] ThinkEndTags = new[]
    {
        "</think>", "</|think|>", "<|/think|>", "</thought>", "</|thought|>", "<|/thought|>",
        "[/THINK]", "[/THOUGHT]", "{/antml:thinking_mode}", "</antml:thinking_mode>", "{/thinking_mode}", "</thinking_mode>"
    };

    /// <summary>
    /// Closing tags that are dropped silently when NO thinking block is open — a model spamming
    /// </think> after already closing its block is the classic MoE "psycho loop", and those tags
    /// must never reach the user as visible chat text. The full streaming state machine lives in
    /// <see cref="ChatStreamParser"/>; these tags remain here for IsUnclosedThinkBlock.
    /// </summary>
    private static readonly string[] StrayThinkCloseTags = ThinkEndTags;

    /// <summary>
    /// True when the text contains an opening thinking tag whose matching close tag never appears.
    /// </summary>
    private static bool IsUnclosedThinkBlock(string text)
    {
        int lastStart = -1;
        int lastEnd = -1;
        foreach (var tag in ThinkStartTags)
        {
            int idx = text.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx > lastStart) lastStart = idx;
        }
        foreach (var tag in ThinkEndTags)
        {
            int idx = text.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx > lastEnd) lastEnd = idx;
        }
        return lastStart > lastEnd;
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

    /// <summary>
    /// Parses a qwen native <parameter> value: JSON objects/arrays (rendered by the template with
    /// tojson) become parsed values; anything else stays a plain string.
    /// </summary>
    private static object? TryParseQwenNativeJsonValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return null; // scalar: keep as string
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions { AllowTrailingCommas = true });
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private List<ToolCallRequest> ParseToolCalls(string response)
    {
        var results = new List<ToolCallRequest>();
        if (string.IsNullOrWhiteSpace(response)) return results;

        // 0. Qwen-native format (qwen35/qwen35moe models): <tool_call><function=NAME><parameter=K>
        // value</parameter>...</function></tool_call>. The embedded qwen template emits this exact
        // structure when tools are provided; it is NOT JSON, so it must be parsed before the
        // JSON heuristics below. Returns immediately when found — the native format is
        // unambiguous and the loose JSON fallbacks must never run on it.
        {
            // Qwen thinking models routinely omit the closing </tool_call> (observed in
            // production logs: <tool_call><function=search_web><parameter=query>...</function>
            // with no close tag). Treat the closing tag as optional so an unclosed native call
            // still parses and executes instead of derailing the whole turn.
            var nativeMatches = Regex.Matches(response,
                @"<tool_call>\s*<function=([a-zA-Z0-9_.\-]+)>(.*?)</function>(?:\s*</tool_call>)?",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (nativeMatches.Count > 0)
            {
                foreach (Match match in nativeMatches)
                {
                    var name = match.Groups[1].Value.Trim();
                    var args = new Dictionary<string, object>();
                    foreach (Match pm in Regex.Matches(match.Groups[2].Value,
                        @"<parameter=([a-zA-Z0-9_]+)>(.*?)</parameter>",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase))
                    {
                        var key = pm.Groups[1].Value.Trim();
                        var rawVal = System.Net.WebUtility.HtmlDecode(pm.Groups[2].Value.Trim());
                        // The template renders complex values (objects/arrays) with tojson; keep
                        // plain scalars as strings.
                        args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                    }
                    if (!string.IsNullOrEmpty(name) && args.Count > 0)
                    {
                        results.Add(new ToolCallRequest(name, args));
                    }
                }
                return results;
            }
        }

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
