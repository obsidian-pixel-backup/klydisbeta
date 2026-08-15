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
    /// When true and the loaded model uses the qwen-native tool-call template, sampling is
    /// grammar-constrained from the moment the model opens <tool_call> so malformed/abandoned
    /// calls cannot reach the regex parser. Default off; ChatEngine enables it exactly when the
    /// qwen native tools prelude is active.
    /// </summary>
    bool EnableToolGrammarConstrainedDecoding { get; set; }

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

    /// <summary>
    /// True when the most recent chat-path generation was cut short mid-stream by a native
    /// decode failure / context overflow AFTER emitting tokens, and the engine completed the
    /// stream cleanly with the partial output (neither a MaxTokens cap hit, a stop token, a
    /// cancellation, nor a detected degenerate loop). Read after the token stream ends: the
    /// response is truncated and should be resumed via auto-continuation, exactly like a
    /// MaxTokens cap hit.
    /// </summary>
    bool LastGenerationWasCutShort { get; }

    /// <summary>
    /// True when the most recent chat-path generation completed EMPTY because the prompt itself
    /// already fills the context window (recurrent architectures complete empty instead of
    /// overflowing the cache). Read after the token stream ends: the caller must reduce the
    /// prompt (rolling compression) rather than treating this as degenerate model output.
    /// </summary>
    bool LastGenerationPromptFilledWindow { get; }

    /// <summary>
    /// True when the most recent generation ended WITHOUT output because it was cancelled
    /// (model switch/unload, user stop, teardown) rather than because the model degenerated.
    /// An empty stream with this flag set must NOT be routed into the empty-response
    /// self-correction cascade — the correction would rebuild the context and re-trigger the
    /// very cancellation that produced the empty stream.
    /// </summary>
    bool LastGenerationWasCancelled { get; }
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
    Klydis.Core.RAG.VectorStore? vectorStore = null,
    Klydis.Core.Learning.AdaptiveLearningService? adaptiveLearning = null) : IGoalCompletionVerifier
{
    private readonly List<ChatMessage> _history = new();
    private readonly Klydis.Core.Learning.AdaptiveLearningService? _adaptiveLearning = adaptiveLearning;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<ChatMessage>> _sessionHistories = new();

    // Context-usage estimation caches for the status-bar gauge. The system prompt token count
    // is refreshed with the EXACT value at every prompt build; the history sum is invalidated
    // on wholesale history mutations (LoadHistory/ClearHistory) and re-derived when the message
    // count changes (messages are append-only otherwise).
    private long _lastSystemPromptTokens;
    private long _estimatedSystemPromptOnce = -1;
    private long _cachedHistoryTokens = -1;
    private int _cachedHistoryCount = -1;
    private readonly List<(string ToolName, string ArgsHash, string PriorResult)> _recentTools = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _pendingConsolidations = new();
    private int _consecutiveBlockedToolCalls = 0;

    public Klydis.Core.RAG.VectorStore? VectorStore { get; set; } = vectorStore;
    
    /// <summary>
    /// Calculates the rolling compression threshold: when HISTORY tokens reach this, older
    /// context is summarized into WorldState. The threshold is a fraction of the budget that
    /// is actually AVAILABLE for history — total context minus the response headroom, the
    /// safety margin, and the system prompt (tools schema + WorldState + skills + lessons can
    /// consume 30-40% of context on their own).
    /// The old fixed "60% of total context" threshold ignored the system prompt entirely: on
    /// small contexts compression NEVER fired (history + system prompt overflowed the window
    /// long before history alone hit 60%) so the prompt truncation path silently dropped
    /// history every turn instead of summarizing it, and on large contexts it fired later
    /// than the real budget allowed. The exact last-built system-prompt size is used when
    /// known (see _lastSystemPromptTokens); otherwise a conservative 25% of context is
    /// assumed. Fires when history fills ~75% of its budget — early enough that truncation
    /// (which drops history without summarizing) rarely has to engage.
    /// </summary>
    private int GetRollingCompressionThreshold()
    {
        int contextSize = (int)inferenceEngine.ContextSize;
        // Mirror the response-headroom + safety-margin reservation the prompt builder uses
        // (see maxTotalPromptTokens below), so the budget math stays consistent.
        int reservedForResponse = contextSize switch
        {
            <= 4096 => 1024,
            <= 16384 => Math.Min(contextSize / 4, 3072),
            <= 65536 => Math.Min(contextSize / 4, 6144),
            _ => Math.Min(contextSize / 4, 12288)
        };
        int safetyMargin = 256;

        long sysTokens = Interlocked.Read(ref _lastSystemPromptTokens);
        if (sysTokens <= 0)
        {
            // No prompt has been built yet this process; assume a typical system prompt size.
            sysTokens = Math.Max(512, (int)(contextSize * 0.25));
        }

        int historyBudget = Math.Max(1024, contextSize - reservedForResponse - safetyMargin - (int)sysTokens);
        return Math.Clamp((int)(historyBudget * 0.75), 2048, 1000000);
    }

    public ModelMessageQueue? MessageQueue { get; set; } = messageQueue;
    public string SelectedPersonality { get; set; } = "Default";
    public bool IsGoalMode { get; set; } = true;
    public string CurrentSessionId { get; private set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Deterministic completion evidence for the goal loop's verification gate: the persisted
    /// plan is the authoritative checklist, so "done" requires every item to be checked off.
    /// See <see cref="IGoalCompletionVerifier"/>.
    /// </summary>
    public IReadOnlyList<string> GetOpenPlanItems(string sessionId)
    {
        try
        {
            return toolExecutor.GetSessionPlanEntries(sessionId)
                .Where(e => !e.Done)
                .Select(e => e.Text)
                .ToList();
        }
        catch (Exception ex)
        {
            // A verifier failure must never crash the goal loop; degrade to "no evidence of
            // open work" (claim accepted) rather than blocking completion on an error.
            logger.LogDebug(ex, "Failed to read open plan items for completion verification.");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Deterministic progress signal for stagnation detection: (Total, Completed) counts from
    /// the persisted plan checklist. (0, 0) when there is no plan.
    /// </summary>
    public (int Total, int Completed) GetPlanProgress(string sessionId)
    {
        try
        {
            var entries = toolExecutor.GetSessionPlanEntries(sessionId);
            return (entries.Count, entries.Count(e => e.Done));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read plan progress for stagnation detection.");
            return (0, 0);
        }
    }

    /// <summary>
    /// The full plan checklist with done flags — the durable source for the continuation
    /// contract (see <see cref="IGoalCompletionVerifier.GetPlanEntries"/>).
    /// </summary>
    public IReadOnlyList<ToolExecutor.PlanEntry> GetPlanEntries(string sessionId)
    {
        try
        {
            return toolExecutor.GetSessionPlanEntries(sessionId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read plan entries for continuation contract.");
            return Array.Empty<ToolExecutor.PlanEntry>();
        }
    }
    public bool IsGenerating { get; private set; }
    public double TokensPerSecond { get; private set; }

    /// <summary>
    /// Optional wall-clock budget for a single user turn (the whole agent loop: every
    /// generation, tool call, and regeneration within one message). The loop checks it
    /// between iterations and terminates with a timeout notice once exceeded — a runaway
    /// long-horizon run can no longer burn unbounded tokens/time. Null = unlimited
    /// (default, preserving current behavior).
    /// </summary>
    public TimeSpan? MaxTurnDuration { get; set; }
    /// <summary>
    /// Gets a snapshot of the conversation history. Returns a copy rather than a live view
    /// because the generation task mutates <c>_history</c> (Add/AddRange/Clear) while UI
    /// threads enumerate this property — a live <see cref="List{T}.AsReadOnly"/> wrapper can
    /// throw "Collection was modified" during concurrent enumeration. Snapshot copies are
    /// O(n) but cheap relative to the generation they observe.
    /// </summary>
    public IReadOnlyList<ChatMessage> History => _history.ToList();

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
    /// <summary>
    /// Maximum number of session histories kept in memory. Every session visited on a long
    /// running app is cached in <see cref="_sessionHistories"/>; without a cap that grows
    /// without bound (each list can hold tens of thousands of tokens of messages). Evicted
    /// sessions simply reload from the store on the next select. The current session is always
    /// kept.
    /// </summary>
    private const int MaxCachedSessionHistories = 20;

    private void EvictOldSessionHistories(string keepId)
    {
        if (_sessionHistories.Count <= MaxCachedSessionHistories) return;
        int excess = _sessionHistories.Count - MaxCachedSessionHistories;
        foreach (var key in _sessionHistories.Keys.Where(k => k != keepId).Take(excess))
        {
            _sessionHistories.TryRemove(key, out _);
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
        _recentTools.Clear();
        _consecutiveBlockedToolCalls = 0;
        InvalidateContextUsageCache();
        CurrentSessionId = Guid.NewGuid().ToString();
        _sessionHistories[CurrentSessionId] = _history;
        EvictOldSessionHistories(CurrentSessionId);
    }

    /// <summary>
    /// Loads conversation history and sets the active session.
    /// </summary>
    /// <summary>
    /// True for engine-injected feedback messages (self-corrections, continuation notices,
    /// parse errors, tool-loop warnings, suspension notices). They are ephemeral guidance for
    /// the single turn in which they were created and must never govern later turns — a stale
    /// "answer in one short sentence" correction kept overriding the user's actual request for
    /// days (observed across sessions in production logs). They are filtered out whenever
    /// session history is loaded and purged from the in-memory history at the start of every
    /// new user turn (see StreamResponseAsync).
    /// </summary>
    public static bool IsEngineInjectedMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.StartsWith("[System Self-Correction:", StringComparison.Ordinal) ||
               content.StartsWith("[System Instruction:", StringComparison.Ordinal) ||
               content.StartsWith("[Tool Error:", StringComparison.Ordinal) ||
               content.StartsWith("[System Warning:", StringComparison.Ordinal) ||
               content.StartsWith("[System ERROR:", StringComparison.Ordinal) ||
               content.StartsWith("[SYSTEM OVERRIDE:", StringComparison.Ordinal) ||
               content.StartsWith("[System: Tool calling", StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends a message to the given session history AND, when the message belongs to the
    /// currently selected session, to the UI-facing <c>_history</c> mirror. The mirror add is
    /// skipped when both lists are the SAME object (ClearHistory aliases them) — otherwise
    /// every message would be appended twice and the model would see duplicates in its prompt.
    /// </summary>
    private void AddToSessionHistory(List<ChatMessage> history, ChatMessage message, string generatingSessionId)
    {
        history.Add(message);
        if (CurrentSessionId == generatingSessionId && !ReferenceEquals(_history, history))
        {
            _history.Add(message);
        }
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
        InvalidateContextUsageCache();
        CurrentSessionId = targetId;
        EvictOldSessionHistories(targetId);
    }

    /// <summary>
    /// Re-syncs the in-memory cached history for a session from the store WITHOUT changing
    /// the active session (CurrentSessionId and <c>_history</c> are untouched). Used when a
    /// background generation completes while the user is viewing another chat: a switch-back
    /// load may have replaced <c>_sessionHistories[sessionId]</c> with a mid-generation DB
    /// snapshot, so the finished turn (appended to the now-orphaned live list) would be
    /// missing from the model's next prompt in that chat.
    /// </summary>
    public async Task ResyncSessionHistoryFromStoreAsync(string sessionId)
    {
        try
        {
            var dbMessages = await messageStore.GetMessagesAsync(sessionId, null);
            var list = new List<ChatMessage>();
            foreach (var msg in dbMessages)
            {
                if (IsEngineInjectedMessage(msg.Content)) continue;
                if (msg.Role == ChatRole.Assistant && string.IsNullOrWhiteSpace(msg.Content)) continue;
                if (msg.IsConsolidated) continue;
                list.Add(new ChatMessage(msg.Role, msg.Content));
            }
            _sessionHistories[sessionId] = list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resync cached session history for {SessionId}.", sessionId);
        }
    }

    private void InvalidateContextUsageCache()
    {
        _cachedHistoryCount = -1;
        _cachedHistoryTokens = -1;
    }

    /// <summary>
    /// Estimates the CURRENT session's context occupancy (system prompt + existing chat
    /// history), independent of any running generation. This is what the status-bar gauge
    /// shows while idle — opening an existing chat must display its true usage immediately,
    /// not "0 used" until the user sends the next message.
    /// The history portion is summed once per history change (token counts are cached per
    /// content in ContextOrchestrator); the system prompt portion reuses the exact token
    /// count from the last prompt build, falling back to a one-time compact-prompt estimate.
    /// </summary>
    public async Task<long> EstimateCurrentContextTokensAsync(CancellationToken ct = default)
    {
        if (!inferenceEngine.IsModelLoaded) return 0;

        long systemTokens = Interlocked.Read(ref _lastSystemPromptTokens);
        if (systemTokens <= 0)
        {
            systemTokens = await EstimateSystemPromptTokensOnceAsync(ct);
        }

        long historyTokens;
        var snapshot = _history.ToList();
        if (_cachedHistoryCount == snapshot.Count && _cachedHistoryTokens >= 0)
        {
            historyTokens = _cachedHistoryTokens;
        }
        else
        {
            long sum = 0;
            foreach (var m in snapshot)
            {
                sum += contextOrchestrator.EstimateTokens(m.Content) + 25; // template formatting overhead
            }
            _cachedHistoryCount = snapshot.Count;
            _cachedHistoryTokens = sum;
            historyTokens = sum;
        }

        // The prompt builder budgets history down to fit the context; mirror that cap so the
        // gauge can never show "used" above the window on very long sessions.
        long total = inferenceEngine.ContextSize;
        long room = total - systemTokens;
        long capped = room > 0 ? Math.Min(historyTokens, room) : historyTokens;
        return systemTokens + capped;
    }

    private async Task<long> EstimateSystemPromptTokensOnceAsync(CancellationToken ct)
    {
        long cached = Interlocked.Read(ref _estimatedSystemPromptOnce);
        if (cached > 0) return cached;
        try
        {
            var tools = await toolExecutor.GetToolDefinitionsAsync();
            var schema = toolExecutor.FormatToolsForPrompt(tools);
            var text = new SystemPromptManager().BuildCompactSystemPrompt(schema, personalityMode: SelectedPersonality);
            long estimate = contextOrchestrator.EstimateTokens(text) + 64; // template application + safety
            Interlocked.Exchange(ref _estimatedSystemPromptOnce, estimate);
            return estimate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to estimate system prompt tokens; using fallback.");
            return 4096;
        }
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

        // Engine-injected feedback from PREVIOUS turns (self-corrections, continuation
        // notices, parse errors, suspension notices) is ephemeral guidance for the turn in
        // which it was created. Left in the shared in-memory history it keeps governing
        // unrelated later requests in the same session (observed: a stale "answer in one
        // short sentence" correction from an old turn still forcing one-line replies days
        // later in the same session). Purge leftovers before building this turn's prompt;
        // intra-turn iterations re-add their own below.
        if (activeHistory.RemoveAll(m => IsEngineInjectedMessage(m.Content)) > 0 &&
            CurrentSessionId == generatingSessionId)
        {
            _history.RemoveAll(m => IsEngineInjectedMessage(m.Content));
        }

        var userMsgObj = new ChatMessage(ChatRole.User, userMessage);
        AddToSessionHistory(activeHistory, userMsgObj, generatingSessionId);
        // ChatMessage is a value-equality record, so Contains() would skip legitimate duplicate
        // content (the user re-asking the same question, two identical assistant replies). The
        // mirror must stay in sync with activeHistory — add unconditionally.

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
                                   InferenceEngine.IsQwenThinkingArchitecture(inferenceEngine.Architecture) &&
                                   !string.IsNullOrWhiteSpace(inferenceEngine.RawChatTemplate) &&
                                   inferenceEngine.RawChatTemplate.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

        // ===== Adaptive learning loop =====
        // Pull the model's accumulated lessons (persisted across sessions) and decide whether it
        // gets the native <function=> tools prelude or the JSON format. A model that has failed
        // the native format repeatedly (recorded automatically by the parse-failure escalation)
        // is switched to JSON automatically on the NEXT session — the system evolves per model.
        string modelName = Klydis.Core.Learning.AdaptiveLearningService.DeriveModelName(inferenceEngine.CurrentModelPath);
        string lessonsSection = await (_adaptiveLearning?.BuildLessonsSectionAsync(modelName, ct: ct) ?? Task.FromResult(string.Empty));
        bool useQwenNativePrelude = isQwenThinkingModel && !string.IsNullOrWhiteSpace(toolsSchema);
        if (_adaptiveLearning != null && useQwenNativePrelude)
        {
            useQwenNativePrelude = !await _adaptiveLearning.HasNativeToolFormatIssuesAsync(modelName, ct);
        }

        // Grammar-constrained decoding: from the moment a qwen-native model opens <tool_call>,
        // sampling is constrained to the well-formed call grammar (ToolCallConstrainedSampling
        // Pipeline + BuildQwenNativeGbnf) so malformed/abandoned calls cannot reach the regex
        // parser — each failed parse used to cost a full prompt rebuild + re-prefill +
        // re-inference. Enabled exactly when the native prelude is used; for every other model
        // (JSON-format qwen, dense, non-qwen) the sampling pipeline stays free-form. The
        // pipeline latches off on any grammar failure, so a bad grammar can never kill a run.
        inferenceEngine.EnableToolGrammarConstrainedDecoding = useQwenNativePrelude;

        // Fire-and-forget lesson recording for correction events (telemetry-like; never blocks
        // the turn and never throws into the stream).
        void NoteLesson(string source, string detail)
        {
            if (_adaptiveLearning == null) return;
            _ = RecordLessonSafeAsync(source, detail);
        }
        async Task RecordLessonSafeAsync(string source, string detail)
        {
            try
            {
                await _adaptiveLearning.RecordCorrectionAsync(inferenceEngine.CurrentModelPath, source, detail);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record learning lesson {Source}.", source);
            }
        }

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
        // Window-full guard: when the ENGINE reports the prompt itself filled the context
        // window (recurrent architectures complete empty instead of overflowing the cache),
        // injecting an empty-response correction would grow the prompt and fail identically.
        // The correct remedy is rolling compression; if compression has already been attempted
        // this turn and the prompt STILL cannot fit, terminate with a clear error instead of
        // looping the "Model produced an empty response — self-correcting…" banner forever.
        bool windowCompressionAttemptedThisTurn = false;
        // Long-horizon budget recovery: when the per-iteration prompt budget pass evicts
        // history messages (hasDroppedMessages), run rolling compression SYNCHRONOUSLY (once
        // per turn) instead of letting the deferred WorldState consolidation rescue them next
        // turn. The evicted messages are archived + summarized into WorldState immediately and
        // the prompt is rebuilt from the compressed history, so the model keeps working memory
        // of early task context on long-horizon runs instead of losing it for a whole turn.
        bool budgetCompressionAttemptedThisTurn = false;
        // Set when the window-full branch terminates the turn with its own specific error; the
        // generic "no visible output" terminal error below is then suppressed so the user does
        // not see two stacked failure banners for the same cause.
        bool emittedWindowFullTerminalError = false;
        // Set when the turn ends because the generation was cancelled (model switch/unload,
        // user stop) instead of because the model degenerated. The interruption notice is the
        // only message shown — no self-corrections, no rescue attempt, and the generic
        // "no visible output" terminal error is suppressed.
        bool emittedCancellationNotice = false;

        bool rescueTriggered = false;
        bool rescueRequested = false;
        var rescueSysMsg = new ChatMessage(ChatRole.System,
            "You are Klydis. Answer the user's latest message directly and concisely in plain text. " +
            "Do not use tools, thinking blocks, tags, or formatting. Just give a clear, short answer.");

        // Parse-failure escalation: a model that opens <tool_call> but never completes it used to
        // get the SAME error forever (observed live: qwen3.6 fine-tunes repeating the identical
        // "INCOMPLETE" error 4+ times with no counter or fallback). Now the feedback escalates
        // across attempts — native format reminder -> offer the alternative JSON format ->
        // concrete completed example -> suspend tool calling for the turn and force a direct
        // answer — so the user always gets a coherent reply instead of an infinite correction loop.
        int consecutiveToolParseFailures = 0;
        bool toolsSuspendedForTurn = false;
        bool suspensionNoticeSent = false;

        // Tracks whether ANY visible text reached the user across all iterations of this turn.
        // A turn that ends with zero visible output (every correction, continuation and rescue
        // attempt failed) must surface an error instead of silently delivering nothing — the
        // "model stopped responding" failure mode (observed: minutes of re-prefill loops ending
        // in an empty turn).
        bool producedVisibleOutput = false;

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
            // ContextOrchestrator.EstimateTokens uses the native tokenizer when a model is
            // loaded and keeps a bounded content-keyed cache, so identical messages/tool
            // outputs across turns stop being re-tokenized on every prompt build.
            int t = contextOrchestrator.EstimateTokens(m.Content) + 25; // 25 tokens for template formatting overhead
            tokenCache[m] = t;
            return t;
        }

        var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (iterationCount < MAX_ITERATIONS)
        {
            iterationCount++;

            // Wall-clock budget: a turn is bounded by MaxTurnDuration (when set) in addition
            // to the iteration cap — checked between iterations so a native decode is never
            // interrupted mid-stream. The timeout notice ends the turn so the user can
            // re-prompt (the conversation history is preserved) instead of watching a stalled
            // or runaway run burn tokens.
            if (MaxTurnDuration.HasValue && turnStopwatch.Elapsed > MaxTurnDuration.Value)
            {
                logger.LogWarning("Turn exceeded the maximum duration budget ({Budget}). Terminating the agent loop at iteration {Iteration}.", MaxTurnDuration.Value, iterationCount);
                yield return new ChatStreamEvent(ChatStreamEventType.Error, $"⏱ This turn ran longer than the maximum allowed duration ({MaxTurnDuration.Value.TotalMinutes:F1} min) and was stopped. Continue in a new message — the conversation history is preserved.");
                break;
            }

            // Execute automated rolling compression when history tokens reach the threshold.
            int rollingThreshold = GetRollingCompressionThreshold();
            int estimatedHistoryTokens = activeHistory.Sum(TokensOf);
            if (estimatedHistoryTokens >= rollingThreshold)
            {
                yield return new ChatStreamEvent(ChatStreamEventType.MemorySummarizing, "🧠 Summarizing conversation context and saving to memory...");
                int keepRecent = Math.Clamp((int)(inferenceEngine.ContextSize * 0.25), 2048, 262144);
                logger.LogInformation("Active history tokens ({Tokens}) reached rolling compression threshold ({Threshold}). Compressing older context into WorldState. Keeping {KeepRecent} recent tokens.",
                    estimatedHistoryTokens, rollingThreshold, keepRecent);
                bool compressed = await contextOrchestrator.PerformRollingCompressionAsync(activeHistory, generatingSessionId, rollingThreshold, keepRecent);
                if (compressed && CurrentSessionId == generatingSessionId)
                {
                    // Keep the UI history and the idle context gauge in sync with the model's
                    // compacted history. On sessions loaded from the store, _history is a
                    // DIFFERENT list than activeHistory, so the Clear inside the orchestrator
                    // leaves the UI showing the full pre-compaction transcript while the model
                    // already sees the summary — and the gauge over-reports occupancy.
                    if (!ReferenceEquals(_history, activeHistory))
                    {
                        _history.Clear();
                        _history.AddRange(activeHistory);
                    }
                    InvalidateContextUsageCache();
                }
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
        if (useQwenNativePrelude)
        {
            // Qwen thinking models get their native tools prelude PREPENDED. The prelude embeds
            // the full ~17KB tools schema and teaches the model's native
            // <tool_call><function=...><parameter=...> format, so the base prompt must (a) NOT
            // re-embed the schema — duplicating it bloats the system prompt to ~37KB and pushes
            // fragile MoE models (qwen3.6-14B-A3B) into hard repetition loops (verified live:
            // dedup 3/3 clean vs doubled 1/3) — and (b) NOT teach the conflicting JSON
            // <tool_call>{"name":...} format, which makes the model flip-flop between the two
            // calling styles and destabilize. The compact base is lean and conflict-free, so it
            // is used for BOTH dense and MoE qwen thinking models. A model with recorded native-
            // format failures skips the prelude and gets the JSON format instead (adaptive).
            var compactBase = sysPromptManager.BuildCompactSystemPrompt("", worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
            sysPrompt = promptEngine.BuildQwenToolsPrelude(toolsSchema) + "\n\n" + compactBase;

            // The native tools prelude embeds the full ~17KB tool schema. On small context
            // windows it can ALONE fill the window — the engine then completes generation EMPTY
            // (recurrent arch) and the user sees the "Model produced an empty response" loop.
            // Budget-check the prelude version exactly like the dense-model full-prompt path
            // below: if it cannot fit alongside the minimum user budget, fall back to the
            // compact prompt (JSON tool format) instead of shipping a guaranteed-empty prompt.
            int preludeTokens = inferenceEngine.IsModelLoaded
                ? inferenceEngine.GetTokenCount(sysPrompt)
                : contextOrchestrator.EstimateTokens(sysPrompt);
            if (preludeTokens > maxTotalPromptTokens - minUserBudget)
            {
                logger.LogWarning("Qwen tools prelude ({PreludeTokens} tokens) exceeds the prompt budget ({Budget}); falling back to the compact prompt (JSON tool format) to keep the context window usable.",
                    preludeTokens, maxTotalPromptTokens - minUserBudget);
                NoteLesson("prelude_too_large", $"Qwen tools prelude ({preludeTokens} tokens) exceeded the prompt budget ({maxTotalPromptTokens - minUserBudget}); fell back to the compact prompt (JSON tool format).");
                sysPrompt = sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
            }
        }
        else if (inferenceEngine.IsMixtureOfExperts)
        {
            sysPrompt = sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
        }
        else
        {
            var fullPrompt = sysPromptManager.BuildCombinedPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode);
            int fullPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(fullPrompt) : contextOrchestrator.EstimateTokens(fullPrompt);
            sysPrompt = fullPromptTokens > maxTotalPromptTokens - minUserBudget
                ? sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode)
                : fullPrompt;
        }

        // User-authored session notes (right-side Notes panel): pinned steering/context notes
        // the user wants the model to honor on EVERY generation of this chat. They ride in the
        // system prompt so they survive rolling compression, which would otherwise prune them
        // from the raw conversation history.
        try
        {
            var sessionNotes = await messageStore.GetNotesAsync(generatingSessionId);
            if (sessionNotes.Count > 0)
            {
                var notesHeader = "\n\nUSER NOTES FOR THIS CHAT (authoritative instructions/context pinned in the NOTES tab by the user — read carefully and obey; they take precedence over ordinary conversation history):\n" +
                    string.Join("\n", sessionNotes.Select((n, i) => $"{i + 1}. {n.Content}"));
                sysPrompt = sysPrompt.TrimEnd() + notesHeader;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load session notes for prompt context.");
        }

        // Artifacts this chat has produced (files the model wrote) — mirrors the PREVIEW tab
        // of the workbench so the model knows what it has created and that the user can render
        // HTML/Markdown/JSON live. Rebuilt each loop iteration alongside the plan.
        try
        {
            var artifacts = toolExecutor.GetSessionArtifactPaths(generatingSessionId);
            if (artifacts.Count > 0)
            {
                var artifactHeader = "\n\nARTIFACTS PRODUCED IN THIS CHAT (files you wrote — shown in the PREVIEW tab; HTML, Markdown and JSON render live for the user):\n" +
                    string.Join("\n", artifacts.Take(15).Select(p => $"  - {p}"));
                if (artifacts.Count > 15)
                {
                    artifactHeader += $"\n  … and {artifacts.Count - 15} more";
                }
                sysPrompt = sysPrompt.TrimEnd() + artifactHeader;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load session artifacts for prompt context.");
        }

        // Agent's current task plan / todo list, maintained through the 'plan' tool. The
        // prompt is rebuilt on EVERY loop iteration, so the model always sees its own
        // checklist (with [x] checkmarks) and its reported progress — this is what closes the
        // goal-execution feedback loop: plan → execute → update plan → re-plan → … → complete.
        try
        {
            var currentPlan = toolExecutor.GetSessionPlan(generatingSessionId);
            if (currentPlan.Count > 0)
            {
                int planProgress = toolExecutor.GetSessionPlanProgress(generatingSessionId);
                var planHeader = "\n\nCURRENT TASK PLAN (your todo list — shown live to the user in the PLAN tab; keep it updated as you work and check off completed items with the 'plan' tool):\n" +
                    string.Join("\n", currentPlan.Select(l => $"  {l}")) +
                    (planProgress >= 0 ? $"\nOverall progress: {planProgress}%" : string.Empty);

                // EXECUTION STATE continuation contract — deterministic from durable sources
                // (plan checklist + queue), so rolling compaction can never erase the semantics
                // of what remains REQUIRED ("D = NOT COMPLETE"). This is the model window's
                // state, as opposed to the WorldState narrative summary.
                var contract = ContinuationContractBuilder.Build(
                    string.Empty,
                    toolExecutor.GetSessionPlanEntries(generatingSessionId),
                    MessageQueue?.GetPending(generatingSessionId).Count ?? 0);
                planHeader += "\n" + ContinuationContractBuilder.Format(contract);

                sysPrompt = sysPrompt.TrimEnd() + planHeader;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load current task plan for prompt context.");
        }

        var sysPromptMsg = new ChatMessage(ChatRole.System, sysPrompt);
        
        // Calculate system prompt size
        var sysOnlyPrompt = promptEngine.ApplyTemplate(new List<ChatMessage> { sysPromptMsg }, templateType, qwenThinking: isQwenThinkingModel);
        int sysPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(sysOnlyPrompt) : contextOrchestrator.EstimateTokens(sysOnlyPrompt);
        // Feed the EXACT system-prompt size to the idle context gauge (see EstimateCurrentContextTokensAsync).
        Interlocked.Exchange(ref _lastSystemPromptTokens, sysPromptTokens);

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

        // LONG-HORIZON CONTEXT BUDGET RECOVERY: the backward budget pass and/or the strict
        // truncation loop evicted history messages because prompt + history exceeded
        // maxTotalPromptTokens. Without intervention the model loses that context THIS turn —
        // the deferred WorldState consolidation only lands next turn (and the strict trim can
        // evict early goal context that a long-horizon run still needs). Run rolling
        // compression synchronously instead: the oldest messages are archived to disk and
        // summarized into WorldState, and history is pruned in place, so the next iteration's
        // prompt rebuild (which re-reads session.WorldState) fits the budget AND carries the
        // summary immediately. Bounded to one compression per turn so a session whose WorldState
        // itself cannot shrink does not spin the rebuild loop.
        if (hasDroppedMessages && !budgetCompressionAttemptedThisTurn)
        {
            budgetCompressionAttemptedThisTurn = true;
            logger.LogInformation("Prompt budget evicted history messages; running synchronous rolling compression so the model retains summarized context this turn.");
            yield return new ChatStreamEvent(ChatStreamEventType.MemorySummarizing, "🧠 Context budget exceeded — summarizing older conversation into memory…");
            // Keep exactly what the budget pass kept (the newest messages that fit), so the
            // evicted tail is precisely the oldest history that overflowed — nothing more is
            // summarized, and the rebuilt prompt fits the same budget.
            int keepRecent = Math.Max(512, targetUserBudget);
            int historyTokens = activeHistory.Sum(TokensOf);
            // Force the compression to engage: history already overflowed the prompt budget, so
            // use the measured size (instead of the 75%-headroom rolling threshold) as the
            // trigger — the oldest messages are exactly the ones that were evicted.
            int compressionThreshold = Math.Max(1, historyTokens);
            bool compressed = await contextOrchestrator.PerformRollingCompressionAsync(activeHistory, generatingSessionId, compressionThreshold, keepRecent);
            if (compressed && CurrentSessionId == generatingSessionId && !ReferenceEquals(_history, activeHistory))
            {
                _history.Clear();
                _history.AddRange(activeHistory);
            }
            InvalidateContextUsageCache();
            // Restart the iteration: the top-of-loop compression check (now below threshold) and
            // the prompt rebuild run against the compressed history + updated WorldState.
            continue;
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

        if (visibleTextBuilder.Length > 0)
        {
            producedVisibleOutput = true;
        }

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
                    NoteLesson($"loop_detector_{loopInfo.Reason}",
                        $"Model entered a degenerate '{loopInfo.Reason}' loop; escalating corrective instruction injected (correction {selfCorrectionsThisTurn} of {MaxSelfCorrectionsPerTurn}).");
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
                AddToSessionHistory(activeHistory, assistantMsgObj, generatingSessionId);
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
                AddToSessionHistory(activeHistory, correctionMsg, generatingSessionId);
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
                NoteLesson("rescue_mode", "Rescue mode triggered: loop corrections exhausted; a plain direct answer (no tools, no thinking blocks) was forced.");
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

            // After repeated malformed tool calls, block execution entirely and force a direct
            // answer. The notice is injected once; subsequent iterations with tool tags just
            // re-generate until the model answers plainly (or the iteration budget ends).
            if (toolsSuspendedForTurn &&
                (toolCallRequests.Count > 0 || Regex.IsMatch(visibleResponse, @"<\|?tool_call\|?>", RegexOptions.IgnoreCase)))
            {
                if (!suspensionNoticeSent)
                {
                    // First tool-tag emission after suspension: deliver the notice, then give
                    // the model ONE plain generation (rescue mode strips the tools schema and
                    // the think opener) instead of re-generating the same failing way.
                    suspensionNoticeSent = true;
                    var suspendMsg = "[System: Tool calling has been suspended for this turn after repeated malformed calls. Do NOT emit <tool_call> or any tool tags. Answer the user's request directly using the information you already have.]";
                    logger.LogWarning("Suspending tool calling for the turn after {FailureCount} consecutive parse failures.", consecutiveToolParseFailures);
                    var suspendMsgObj = new ChatMessage(ChatRole.Tool, suspendMsg);
                    AddToSessionHistory(activeHistory, suspendMsgObj, generatingSessionId);
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, suspendMsg);
                    if (!rescueTriggered && !rescueRequested)
                    {
                        rescueRequested = true;
                    }
                }
                else
                {
                    // The model ignored the suspension notice (and any rescue attempt) and is
                    // STILL emitting tool tags. Terminate the turn instead of regenerating until
                    // the iteration budget — the pre-fix behavior was an identical
                    // "INCOMPLETE" loop streaming for 40+ iterations / ~4 minutes (observed in
                    // production chat exports).
                    if (string.IsNullOrWhiteSpace(visibleTextBuilder.ToString()))
                    {
                        yield return new ChatStreamEvent(ChatStreamEventType.Error,
                            "⚠ Model repeatedly failed to produce a valid tool call or a direct answer. Please try rephrasing your request.");
                    }
                    break;
                }
                continue;
            }

            // Shared escalation ladder for malformed tool calls: whether parsing produced ZERO
            // requests despite a tool tag, or the executor rejected a structurally invalid call
            // (missing required argument), the model gets the same progressive fix-it feedback
            // — reminder → alternative format → completed example → suspend + direct answer.
            // Previously executor validation errors ("Command is required" ×7) were returned as
            // ordinary tool results and NEVER entered this ladder, so a model stuck in that loop
            // ran until the user gave up.
            string BuildToolCallEscalation(string attemptedHint)
            {
                if (consecutiveToolParseFailures >= 4)
                {
                    // Last resort: stop trying tools entirely and demand a direct answer.
                    toolsSuspendedForTurn = true;
                    NoteLesson("tool_call_suspension", $"Tool calling suspended for the turn after {consecutiveToolParseFailures} consecutive malformed tool calls; a direct answer was forced.");
                    return "[System: Tool calling has been suspended for this turn after repeated malformed calls. Do NOT emit <tool_call> or any tool tags. Answer the user's request directly using the information you already have.]";
                }

                if (consecutiveToolParseFailures >= 2)
                {
                    if (isQwenThinkingModel && _adaptiveLearning != null)
                    {
                        _ = _adaptiveLearning.RecordNativeToolFormatFailureAsync(inferenceEngine.CurrentModelPath);
                    }
                    NoteLesson("tool_parse_failure", $"Model emitted {consecutiveToolParseFailures} consecutive unparseable tool calls; escalation offered the alternative JSON format.");
                    return isQwenThinkingModel
                        ? $@"[Tool Error: Your tool call is STILL incomplete.{attemptedHint} The parser accepts BOTH formats — EITHER finish the native form: <tool_call><function=TOOL_NAME><parameter=ARG_NAME>value</parameter></function></tool_call> (close </function> and </tool_call>) OR use the JSON form: <tool_call>{{""name"": ""tool_name"", ""arguments"": {{""arg"": ""value""}}}}</tool_call>. Make the tool call the ENTIRE content of your response — nothing before, nothing after.]"
                        : $@"[Tool Error: Failed to parse <tool_call> JSON again.{attemptedHint} Emit the tool call as the ENTIRE response in exactly this form: <tool_call>{{""name"": ""tool_name"", ""arguments"": {{""arg"": ""value""}}}}</tool_call>. No surrounding text, no code fences.]";
                }

                // First failure: teach the model's native format (qwen) or the JSON form.
                return isQwenThinkingModel
                    ? $"[Tool Error: Your tool call is INCOMPLETE — you opened <tool_call> but did not finish it.{attemptedHint} Complete it using the native format: <tool_call><function=TOOL_NAME><parameter=ARG_NAME>value</parameter></function></tool_call>. Required parameters must be included. Do NOT use JSON inside the tags.]"
                    : $"[Tool Error: Failed to parse <tool_call> JSON.{attemptedHint} Please ensure arguments are valid JSON with 'name' and 'arguments'.]";
            }

            if (toolCallRequests.Count > 0)
            {
                bool forceTurnTermination = false;
                bool validationEscalated = false;

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
                        NoteLesson("tool_loop_guardrail", $"Repeated identical tool call '{req.Name}' detected after {matchCount + 1} attempts; tiered guardrail feedback injected.");

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
                        AddToSessionHistory(activeHistory, guardrailMsgObj, generatingSessionId);
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
                    consecutiveToolParseFailures = 0;

                    yield return new ChatStreamEvent(ChatStreamEventType.ToolCall, req.Name, new Dictionary<string, object> { ["Arguments"] = req.Arguments });
                    
                    var result = await toolExecutor.ExecuteToolAsync(req, generatingSessionId, ct, inferenceEngine.CurrentModelPath);

                    // Executor rejected a STRUCTURALLY INVALID call (missing required argument,
                    // malformed path). Route it into the parse-failure escalation ladder instead
                    // of feeding it back as an ordinary tool result: the observed "Command is
                    // required" ×7 loop happened because each empty call returned a plain error
                    // that never escalated. The escalation message names the missing piece so the
                    // model can fix the call on the next iteration.
                    if (result.IsValidationError)
                    {
                        consecutiveToolParseFailures++;
                        string validationHint = string.IsNullOrWhiteSpace(result.Error)
                            ? $" You were attempting to call '{req.Name}'."
                            : $" You called '{req.Name}' but it is malformed: {result.Error}";
                        string escalation = BuildToolCallEscalation(validationHint);
                        logger.LogWarning("Executor rejected invalid call for {ToolName} ({Error}); escalating (attempt {Attempt}).",
                            req.Name, result.Error, consecutiveToolParseFailures);
                        var escObj = new ChatMessage(ChatRole.Tool, escalation);
                        AddToSessionHistory(activeHistory, escObj, generatingSessionId);
                        // Engine-internal parse feedback: in-memory only (see IsEngineInjectedMessage).
                        yield return new ChatStreamEvent(ChatStreamEventType.Error, escalation);
                        validationEscalated = true;
                        break;
                    }

                    var toolOutput = string.IsNullOrWhiteSpace(result.Output) ? (result.Error ?? "Empty result") : result.Output;
                    
                    var currentPendingQueue = MessageQueue?.GetPending(generatingSessionId);
                    if (currentPendingQueue != null && currentPendingQueue.Count > 0)
                    {
                        var queueSummaries = string.Join("; ", currentPendingQueue.Select(m => $"[ID: {m.Id}, Mode: {m.Mode}]: \"{m.Content}\""));
                        toolOutput += $"\n\n[SYSTEM NOTIFICATION: You have {currentPendingQueue.Count} pending queued message(s) waiting: {queueSummaries}. Call tool 'incorporate_queued_message' with argument {{\"queue_id\": \"<ID>\"}} to inspect and incorporate them into your workflow.]";
                    }

                    _recentTools.Add((req.Name, argsHash, toolOutput));

                    // Bound the loop-detection history: it only needs recent context, and an
                    // unbounded list grows forever on long autonomous sessions (O(n) scan per call).
                    if (_recentTools.Count > 200)
                    {
                        _recentTools.RemoveRange(0, _recentTools.Count - 200);
                    }

                    var toolOutputObj = new ChatMessage(ChatRole.Tool, toolOutput, req.Name);
                    AddToSessionHistory(activeHistory, toolOutputObj, generatingSessionId);
                    await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Tool, toolOutput, 0, null);
                    
                    yield return new ChatStreamEvent(ChatStreamEventType.ToolResult, toolOutput, new Dictionary<string, object> { ["Success"] = result.Success });
                }

                if (validationEscalated)
                {
                    // The escalation message is in history; regenerate so the model can fix the
                    // call (same recovery the zero-parse branch uses). Only true termination
                    // reasons below break the turn.
                    continue;
                }

                if (forceTurnTermination || _consecutiveBlockedToolCalls >= 5)
                {
                    break;
                }
            }
            else if (Regex.IsMatch(visibleResponse, @"<\|?tool_call\|?>", RegexOptions.IgnoreCase))
            {
                // Assistant attempted a tool call tag but parsing produced 0 valid requests.
                // Escalate the feedback across attempts instead of repeating the same error, so
                // models that cannot produce the taught format get an exit path.
                consecutiveToolParseFailures++;
                logger.LogWarning("Assistant emitted <tool_call> tag but parsing failed (attempt {Attempt}).", consecutiveToolParseFailures);

                // Surface the tool the model was attempting so the fix instruction is concrete.
                // Covers every tolerated function-tag form (<function=NAME>, <function name=...>,
                // and the broken <function>NAME) so the error names the real tool.
                var attemptedName = Regex.Match(visibleResponse,
                    @"<function\s*(?:=\s*([a-zA-Z0-9_.-]+)|name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.-]+))|([a-zA-Z0-9_.-]+)(?=\s*[><]))",
                    RegexOptions.IgnoreCase);
                var attemptedTool = attemptedName.Success
                    ? FirstNonEmpty(attemptedName, 1, 2, 3, 4, 5)
                    : (Regex.Match(visibleResponse, @"(?:name|function|tool)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value);
                string attemptedHint = string.IsNullOrEmpty(attemptedTool)
                    ? ""
                    : $" You were attempting to call '{attemptedTool}'.";

                string parseErrorMsg = BuildToolCallEscalation(attemptedHint);

                var parseErrMsgObj = new ChatMessage(ChatRole.Tool, parseErrorMsg);
                AddToSessionHistory(activeHistory, parseErrMsgObj, generatingSessionId);
                // Engine-internal parse feedback: keep it in the in-memory history for the next
                // iteration, but never persist it (see IsEngineInjectedMessage).
                yield return new ChatStreamEvent(ChatStreamEventType.Error, parseErrorMsg);
            }
            else
            {
                // Check if generation ended prematurely mid-response (truncated before completing
                // output). Three independent signals: (a) the engine exhausted its MaxTokens budget
                // (the stream was cut at the output cap — even a response that ends cleanly with a
                // period is truncated), (b) the visible text ends mid-sentence/structure, or
                // (c) the native decode failed / context overflowed mid-stream AFTER tokens were
                // emitted and the engine completed the channel with the partial output (see
                // LastGenerationWasCutShort — without it, such a cut that lands on a sentence
                // boundary looks like a natural stop and the turn silently ends with a partial
                // answer, the observed "generation terminates after ~1k tokens" failure). The
                // instruction may be injected up to MaxContinuationsPerTurn per user turn so long
                // generations resume across chunks, but the budget prevents the old pathological
                // cascade (each re-injection rebuilds and re-prefills the whole prompt).
                bool hitOutputCap = inferenceEngine.LastGenerationHitMaxTokens;
                bool cutShortMidStream = inferenceEngine.LastGenerationWasCutShort;
                bool isTruncatedMidGeneration = IsTruncatedMidGeneration(fullResponse, visibleResponse);
                bool visibleEmpty = string.IsNullOrWhiteSpace(visibleTextBuilder.ToString());

                // A qwen thinking model that never closed its think block produced reasoning
                // only — all streamed content was ThinkingTokens, so there is nothing visible to
                // continue. Continuing just reopens the same <think> block and the model keeps
                // reasoning (each continuation rebuilds and re-prefills the whole prompt — the
                // minutes-long "stopped responding" crawl on hybrid models). Route straight to
                // the empty-response correction, which demands an actual answer.
                bool stuckInThink = qwenNeverClosedThink && visibleEmpty;

                // Once a continuation has been injected and the model STILL produced no visible
                // text, it is not a truncated response — it is an empty/degenerate one. Keep
                // continuing would burn up to MaxContinuationsPerTurn full re-prefills on a
                // model that is not producing output.
                bool noVisibleProgress = continuationsThisTurn > 0 && visibleEmpty;
                bool continuationAllowed = !stuckInThink && !noVisibleProgress;

                if (inferenceEngine.LastGenerationPromptFilledWindow)
                {
                    // The prompt itself cannot fit the window — the empty stream is a
                    // structural failure, NOT degenerate model behavior. Do NOT inject an
                    // empty-response correction (it grows the prompt and fails identically).
                    // First remedy: compress the conversation history and retry once.
                    if (!windowCompressionAttemptedThisTurn)
                    {
                        windowCompressionAttemptedThisTurn = true;
                        logger.LogWarning("Generation completed empty because the prompt fills the context window. Running rolling compression and retrying.");
                        NoteLesson("window_full_empty", $"Generation completed empty because the prompt filled the context window; injected rolling compression and retried.");
                        yield return new ChatStreamEvent(ChatStreamEventType.MemorySummarizing, "🧠 Context window is full — summarizing conversation history and retrying…");
                        int keepRecent = Math.Clamp((int)(inferenceEngine.ContextSize * 0.25), 2048, 262144);
                        await contextOrchestrator.PerformRollingCompressionAsync(activeHistory, generatingSessionId, Math.Max(1024, (int)(inferenceEngine.ContextSize / 2)), keepRecent);
                        if (CurrentSessionId == generatingSessionId && !ReferenceEquals(_history, activeHistory))
                        {
                            _history.Clear();
                            _history.AddRange(activeHistory);
                        }
                        InvalidateContextUsageCache();
                        // Fall through: the loop rebuilds the prompt from the compressed history
                        // and retries the generation.
                    }
                    else
                    {
                        // Compression was already attempted and the prompt STILL cannot fit.
                        // Terminate with an actionable error instead of a silent/looping empty
                        // response — the user must reduce context or raise the window.
                        logger.LogWarning("Prompt still fills the context window after compression. Terminating the turn with a clear error.");
                        NoteLesson("window_full_empty_persistent", "Prompt still fills the context window after rolling compression; turn terminated with an explicit error.");
                        emittedWindowFullTerminalError = true;
                        yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ The model's context window is full even after compressing the conversation. Try starting a new chat or reducing context usage.");
                        break;
                    }
                }
                else if (visibleEmpty && inferenceEngine.LastGenerationWasCancelled)
                {
                    // The empty stream is the result of a CANCELLATION (model switch/unload,
                    // user stop, teardown), not a degenerate model. Injecting an empty-response
                    // self-correction here rebuilds the context and re-triggers the very
                    // cancellation that produced the empty stream — the observed correction-storm
                    // during model alternation (identical empty_response lessons across all qwen
                    // models, 5 context rebuilds in 9 seconds, zero decodes). End the turn with
                    // an accurate interruption notice; the user's message stays un-answered and
                    // can be re-sent once the model is stable.
                    emittedCancellationNotice = true;
                    logger.LogWarning("Generation was cancelled before producing visible output (model switch, unload, or user stop). Ending the turn with an interruption notice instead of self-correcting.");
                    NoteLesson("generation_cancelled", "Generation was cancelled (model switch/unload) before producing output; turn ended with an interruption notice instead of the empty-response correction cascade.");
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ Generation was interrupted — the model was switched or unloaded while responding. Your message is still here; send it again once the model has finished loading.");
                    break;
                }
                else if ((isTruncatedMidGeneration || hitOutputCap || cutShortMidStream) && continuationAllowed && iterationCount < MAX_ITERATIONS && continuationsThisTurn < MaxContinuationsPerTurn)
                {
                    continuationsThisTurn++;
                    logger.LogInformation("Output generation cut off (hitMaxTokens={HitCap}, midSentence={MidSentence}, cutShortMidStream={CutShort}). Triggering auto-continuation iteration {Count}/{Max}.",
                        hitOutputCap, isTruncatedMidGeneration, cutShortMidStream, continuationsThisTurn, MaxContinuationsPerTurn);
                    var continuationInstruction = "[System Instruction: Your previous output was truncated mid-generation due to output token constraints. Continue immediately from the exact point of truncation without repeating any previously written text.]";
                    var continuationMsgObj = new ChatMessage(ChatRole.User, continuationInstruction);
                    AddToSessionHistory(activeHistory, continuationMsgObj, generatingSessionId);
                    // Engine-internal continuation notice: in-memory only (see IsEngineInjectedMessage).
                }
                else if (visibleEmpty && selfCorrectionsThisTurn < MaxSelfCorrectionsPerTurn)
                {
                    // Empty/degenerate response: the model produced no actual visible content
                    // (reasoning alone does not count — the user sees nothing). Ending the turn
                    // here would silently deliver nothing — treat it like a loop and self-correct
                    // instead. NOT gated on qwenNeverClosedThink: an unclosed think block with
                    // zero visible text is exactly the degenerate case that must reach this
                    // path — previously it fell through the gated branches to a silent break
                    // after burning up to 16 continuation re-prefills.
                    selfCorrectionsThisTurn++;
                    NoteLesson("empty_response", $"Model produced an empty visible response; empty-response self-correction injected (correction {selfCorrectionsThisTurn}).");
                    logger.LogWarning("Model produced an empty visible response. Injecting empty-response self-correction (correction {Count} of {Max} this turn).",
                        selfCorrectionsThisTurn, MaxSelfCorrectionsPerTurn);
                    var emptyCorrection = "[System Self-Correction: Your previous response was EMPTY — you produced no actual content. Re-read the user's message carefully and respond DIRECTLY with a real answer. Do not just close tags or emit whitespace.]";
                    var emptyMsgObj = new ChatMessage(ChatRole.User, emptyCorrection);
                    AddToSessionHistory(activeHistory, emptyMsgObj, generatingSessionId);
                    // Engine-internal correction: in-memory only (see IsEngineInjectedMessage).
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ Model produced an empty response — self-correcting…");
                }
                else if (visibleEmpty && !rescueTriggered)
                {
                    // Empty responses exhausted the correction budget — rescue mode: plain
                    // direct answer without tools or thinking blocks (see the rescue activation
                    // above). One final attempt so the user is never left with nothing.
                    rescueTriggered = true;
                    isQwenThinkingModel = false;
                    sysPromptMsg = rescueSysMsg;
                    logger.LogWarning("Empty-response corrections exhausted ({Max}). Switching to rescue mode: plain direct answer without tools or thinking blocks.", MaxSelfCorrectionsPerTurn);
                    NoteLesson("rescue_mode_empty", "Rescue mode triggered after repeated empty responses; plain direct answer forced.");
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

        // Terminal-output guarantee: if the ENTIRE turn produced no visible text (all
        // self-corrections, the rescue attempt, and every continuation failed), surface a clear
        // error instead of ending silently. Previously this dead-ended with zero output — the
        // user saw a thought bubble stall, then nothing ("the model stopped responding").
        if (!producedVisibleOutput && !emittedWindowFullTerminalError && !emittedCancellationNotice)
        {
            logger.LogWarning("Turn ended with no visible output across {Iterations} iterations (all corrections/rescue attempts failed).", iterationCount);
            yield return new ChatStreamEvent(ChatStreamEventType.Error,
                "⚠ The model produced no visible response after exhausting self-correction and rescue attempts. Please try rephrasing your request or adjusting the generation settings.");
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
                "ParagraphCopy" => "[System Self-Correction: You began repeating the same paragraph over and over (even with words merged or spacing removed). Stop immediately. Re-read the user's request and continue with genuinely NEW content that advances the task instead of copying what you already wrote.]",
                "ThinkOverflow" => "[System Self-Correction: You produced ONLY internal reasoning with no visible answer. Stop reasoning immediately. Close your thinking block, then answer the user's request directly in plain text — or emit ONE complete, well-formed tool call if a tool is needed. Do not continue planning inside the thinking block.]",
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
        "ParagraphCopy" => "repeated copied paragraphs",
        "ThinkOverflow" => "uninterrupted reasoning with no answer",
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

        // 5. Bare structural ending: the visible text stops on a markdown header, a bare
        //    horizontal divider, or an empty list marker — a section was OPENED but never
        //    written. The observed "terminates after ~2k tokens" failure: a qwen MoE model
        //    writing an enumerated report EOS's right after "### 2. write_file (Text Writing)"
        //    because the repeated section template is suppressed by the frequency/presence
        //    sampling penalties (the cut looks like a natural stop — the line ends with a
        //    closing paren, so rule 4 above misses it). Each auto-continuation rebuilds the
        //    prompt with a fresh penalty state, so the model resumes and completes the
        //    enumeration across chunks instead of the turn silently ending with a partial
        //    answer. A genuinely completed response essentially never ends on a bare header.
        if (EndsWithStructuralCut(cleanTrimmed))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the visible (think-stripped) text ends on a bare structural marker — a
    /// markdown header, a lone horizontal divider, or an empty numbered/bulleted list item —
    /// with no body content after it. The marker must FOLLOW real content: a response that is
    /// nothing but a header ("### Done") is a deliberate short answer, not a cut.
    /// </summary>
    internal static bool EndsWithStructuralCut(string cleanTrimmed)
    {
        if (cleanTrimmed.Length < 40) return false;

        var lines = cleanTrimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? last = lines.Length > 0 ? lines[^1].TrimEnd() : null;
        if (string.IsNullOrWhiteSpace(last)) return false;

        // The structural marker must terminate an existing section — a lone header (or a
        // response that is only markers) is an intentional short reply.
        bool hasPrecedingContent = lines.Take(lines.Length - 1).Any(l => !string.IsNullOrWhiteSpace(l));
        if (!hasPrecedingContent) return false;

        // 1. Markdown header as the last line ("# ", "## ", "### ", "#### " ...).
        if (Regex.IsMatch(last, @"^#{1,6}\s+\S"))
        {
            return true;
        }

        // 2. Bare horizontal divider as the last line ("---", "***", "___" alone on a line).
        if (Regex.IsMatch(last, @"^(?:-{3,}|\*{3,}|_{3,})$"))
        {
            return true;
        }

        // 3. Empty numbered/bulleted list marker as the last line ("3.", "- ", "* " with no
        //    item text). A cut enumeration ends with the dangling marker before the next item.
        if (Regex.IsMatch(last, @"^\s*(?:\d+[.)]\s*$|[-*+]\s*$)"))
        {
            return true;
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

    /// <summary>
    /// Returns the first non-empty captured group of <paramref name="m"/>, in group-number
    /// order. Used by the tolerant native-format regexes, where the same semantic value may be
    /// captured by any one of several alternatives (quoted / unquoted / '=' / bare).
    /// </summary>
    private static string FirstNonEmpty(Match m, params int[] groups)
    {
        foreach (int g in groups)
        {
            if (m.Groups[g].Success && m.Groups[g].Value.Length > 0)
            {
                return m.Groups[g].Value.Trim();
            }
        }
        return string.Empty;
    }

    private List<ToolCallRequest> ParseToolCalls(string response)
    {
        var results = new List<ToolCallRequest>();
        if (string.IsNullOrWhiteSpace(response)) return results;

        // Self-protecting: strip thinking blocks up front (thinking tags only — antml/qwen
        // tool calls survive). Reasoning routinely contains JSON shaped like {"name": ...} /
        // {"tool": ...} planning lines, and the loose JSON fallback layers below would
        // misread them as real tool calls (phantom executions that re-run the whole turn
        // loop). The production caller already passes a think-stripped response; stripping
        // here makes the parser safe for any caller.
        response = OutputSanitizer.StripThinkingBlocks(response);
        if (string.IsNullOrWhiteSpace(response)) return results;

        // 0. Qwen-native format (qwen35/qwen35moe models): <tool_call><function=NAME><parameter=K>
        // value</parameter>...</function></tool_call>. The embedded qwen template emits this exact
        // structure when tools are provided; it is NOT JSON, so it must be parsed before the
        // JSON heuristics below. Returns immediately when found — the native format is
        // unambiguous and the loose JSON fallbacks must never run on it.
        //
        // Parsing is deliberately TOLERANT of the syntax qwen fine-tunes actually produce
        // (observed in production chat exports): the function tag may be <function=NAME>,
        // <function name="NAME">, or the broken <function>NAME (missing '='); parameters may be
        // <parameter=K>value</parameter>, <parameter name="K">value</parameter>,
        // <parameter K>value</parameter> (missing '='), or bare <K>value</K> tags. A strict
        // regex turned well-intentioned calls into the "INCOMPLETE" feedback loop — the model
        // was told to fix a call the parser simply could not read.
        {
            // Qwen thinking models routinely omit the closing </tool_call> (observed in
            // production logs: <tool_call><function=search_web><parameter=query>...</function>
            // with no close tag). Treat the closing tag as optional so an unclosed native call
            // still parses and executes instead of derailing the whole turn.
            var nativeBlocks = Regex.Matches(response,
                @"<\|?tool_calls?\|?>(.*?)(?:</\|?tool_calls?\|?>|<\|/tool_calls?\|?>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match block in nativeBlocks)
            {
                var body = block.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(body) ||
                    body.IndexOf("<function", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Not a native call (e.g. <tool_call>{"name": ...}</tool_call> JSON form) —
                    // leave it for the JSON heuristics below.
                    continue;
                }

                // Function name: attribute form, '=' form, or bare-text form (model dropped '=').
                string? name = null;
                var fnAttr = Regex.Match(body,
                    @"<function\s+name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))\s*>",
                    RegexOptions.IgnoreCase);
                if (fnAttr.Success)
                {
                    name = FirstNonEmpty(fnAttr, 1, 2, 3);
                }
                else
                {
                    var fnEq = Regex.Match(body, @"<function\s*=\s*([a-zA-Z0-9_.\-]+)\s*>", RegexOptions.IgnoreCase);
                    if (fnEq.Success)
                    {
                        name = fnEq.Groups[1].Value;
                    }
                    else
                    {
                        var fnBare = Regex.Match(body, @"<function>\s*([a-zA-Z0-9_.\-]+)(?:\s*[><])", RegexOptions.IgnoreCase);
                        if (fnBare.Success)
                        {
                            name = fnBare.Groups[1].Value;
                        }
                    }
                }
                if (string.IsNullOrEmpty(name)) continue;

                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                // <parameter ...>value</parameter> in any tolerated syntax: =K, name="K", name=K,
                // or a bare K (the model dropping the '=').
                foreach (Match pm in Regex.Matches(body,
                    @"<parameter\s*(?:=\s*([a-zA-Z0-9_.\-]+)|name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))|([a-zA-Z0-9_.\-]+))\s*>([\s\S]*?)</parameter>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var key = FirstNonEmpty(pm, 1, 2, 3, 4, 5);
                    if (string.IsNullOrEmpty(key)) continue;
                    var rawVal = System.Net.WebUtility.HtmlDecode(pm.Groups[6].Value.Trim());
                    // The template renders complex values (objects/arrays) with tojson; keep
                    // plain scalars as strings.
                    args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                }

                // Bare <K>value</K> tags as parameters (the model's <location>Cape Town</location>
                // style). Reserved tags never map to arguments.
                foreach (Match bm in Regex.Matches(body,
                    @"<(?!function\b|parameter\b|tool_call\b|tool_calls\b|think\b|thought\b|/)([a-zA-Z_][a-zA-Z0-9_.\-]*)\s*>([\s\S]*?)</\1>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var key = bm.Groups[1].Value;
                    if (string.IsNullOrEmpty(key) || args.ContainsKey(key)) continue;
                    var rawVal = System.Net.WebUtility.HtmlDecode(bm.Groups[2].Value.Trim());
                    args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                }

                // Zero-parameter tools (get_system_info, list_rag_collections, ...) emit
                // <function=NAME> with no <parameter> block. Requiring args.Count > 0 dropped
                // those calls, which then triggered the "INCOMPLETE tool call" feedback loop
                // — the model was told to fix a call that was already well-formed.
                if (!string.IsNullOrEmpty(name))
                {
                    results.Add(new ToolCallRequest(name, args));
                }
            }
            if (results.Count > 0) return results;
        }

        // 0b. Anthropic/Claude native format (antml): <antml:invoke name="TOOL"><antml:parameter
        // name="ARG">value</antml:parameter></antml:invoke>. Claude-fine-tuned models (e.g. the
        // qwen35 "Claude-4.6" hybrid seen in production) emit THIS shape, not the qwen native
        // tags above. Without a dedicated parser their calls fell through every layer and died
        // as "INCOMPLETE tool call" — a well-formed Claude call was told to fix itself.
        // Parsing is tolerant: the parameter name may be quoted or bare, multiple parameters
        // are supported, and the closing tag is optional.
        {
            var antmlBlocks = Regex.Matches(response,
                @"<antml:invoke\s+name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))[^>]*>(.*?)(?:</antml:invoke>|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            foreach (Match block in antmlBlocks)
            {
                var name = FirstNonEmpty(block, 1, 2, 3);
                if (string.IsNullOrEmpty(name)) continue;

                var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (Match pm in Regex.Matches(block.Groups[4].Value,
                    @"<antml:parameter\s+name\s*=\s*(?:""([^""]+)""|'([^']+)'|([a-zA-Z0-9_.\-]+))[^>]*>([\s\S]*?)</antml:parameter>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var key = FirstNonEmpty(pm, 1, 2, 3);
                    if (string.IsNullOrEmpty(key)) continue;
                    var rawVal = System.Net.WebUtility.HtmlDecode(pm.Groups[4].Value.Trim());
                    args[key] = TryParseQwenNativeJsonValue(rawVal) ?? rawVal;
                }

                results.Add(new ToolCallRequest(name, args));
            }
            if (results.Count > 0) return results;
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

        // Layer 5 (narrative simulated tool calls — "- tool_name\n- Input: {...}") was REMOVED.
        // It regex-extracted prose that merely LOOKED like a call and executed it as a real tool,
        // laundering fabricated planning text into commands (the KMS chats' invented
        // "kms-agent" execution). Real formats are parsed in layers 0-4 above; prose that is not
        // a structured call must be answered, not executed.

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
