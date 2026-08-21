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
using Klydis.Core.Tasks;
using Klydis.Core.Orchestration;

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
    MemorySummarizing,

    /// <summary>Supervisor accepted a task_complete claim — the task is verified done.</summary>
    GoalComplete,

    /// <summary>Supervisor rejected a task_complete claim (plan items still open).</summary>
    CompletionRejected
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

    /// <summary>
    /// How the most recent generation treated the KV cache relative to the previous generation's
    /// prompt: ExactReuse (new prompt is a strict prefix-extension of the evaluated prompt),
    /// PartialReuse (rewound to the common prefix), Reset* (no usable prefix — context rebuilt),
    /// IsolatedReset. Read after the token stream ends; lets the orchestration layer log the
    /// turn-boundary decision so a "new turn running on stale KV" failure is visible per
    /// generation.
    /// </summary>
    string LastGenerationContextDecision { get; }

    /// <summary>
    /// Char length of the prompt prefix reused by the most recent generation (0 when the
    /// context was reset).
    /// </summary>
    int LastGenerationPrefixLength { get; }

    /// <summary>
    /// Structured result of the most recent generation, populated at generation completion.
    /// </summary>
    GenerationResult? LastGenerationResult { get; }

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
    Klydis.Core.Learning.AdaptiveLearningService? adaptiveLearning = null,
    Klydis.Core.Tasks.TaskManager? taskManager = null,
    Klydis.Core.Tasks.AgentRuntime? agentRuntime = null) : IGoalCompletionVerifier
{
    private readonly List<ChatMessage> _history = new();
    private readonly Klydis.Core.Learning.AdaptiveLearningService? _adaptiveLearning = adaptiveLearning;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<ChatMessage>> _sessionHistories = new();

    // Context-usage estimation caches for the status-bar gauge. The system prompt token count
    // is refreshed with the EXACT value at every prompt build; the history sum is invalidated
    // on wholesale history mutations (LoadHistory/ClearHistory) and re-derived when the message
    // count changes (messages are append-only otherwise).
    private long _lastSystemPromptTokens;
    // Per-mode fallback estimates for the idle context gauge (index = InteractionMode).
    // Conversation-mode prompts are deliberately minimal (no tools), so a single global
    // agent-prompt estimate overstates usage and misbudgets history for chat turns. The
    // EXACT token count from the last prompt build (_lastSystemPromptTokens) dominates once
    // a build has run; these are only the pre-first-build fallbacks.
    private long[] _estimatedSystemPromptByMode = { -1, -1, -1 };
    private InteractionMode _lastPromptMode = InteractionMode.Conversation;
    private long _cachedHistoryTokens = -1;
    private int _cachedHistoryCount = -1;
    private readonly List<(string ToolName, string ArgsHash, string PriorResult)> _recentTools = new();
    // P0 turn isolation: serializes the COMPLETE StreamResponseAsync lifecycle. See the
    // gate in StreamResponseAsync — without it, concurrent callers (UI, GoalOrchestrator,
    // queue processing) interleave on the shared CurrentTaskId / toolExecutor.Current* /
    // inference-engine state.
    private readonly System.Threading.SemaphoreSlim _turnGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _pendingConsolidations = new();
    private int _consecutiveBlockedToolCalls = 0;

    // P0: consecutive-stalled-turn tracking, keyed by task (fallback: session). The
    // supervisor's stagnation circuit breaker consumes this value — previously the snapshot
    // hardcoded ConsecutiveStalledTurns: 0, so the Replan-on-stagnation branch could never
    // fire. Incremented on a supervisor checkpoint with NO factual state delta (no tool
    // executed, no file/plan/evidence change); reset on meaningful progress, on a user
    // decision (a fresh turn), and on a supervisor Replan.
    private readonly Dictionary<string, int> _stalledTurnsByTask = new();

    // P1.12: run-scoped action replay ledger. Side-effect-bearing actions that SUCCESSFULLY
    // executed are recorded by replay key (tool + canonicalized args) and the gate rejects a
    // re-execution — a recovery loop must never duplicate a command or destructive call. The
    // ledger persists ACROSS TURNS within a run (the old per-iteration HashSet reset every
    // generation, so turn 2 could replay turn 1's action); it is cleared only when the active
    // run id changes (a fresh run / new task).
    private readonly HashSet<string> _runExecutedActions = new(StringComparer.Ordinal);
    private string? _runExecutedActionsRunId;
    private readonly Klydis.Core.Tasks.FailureFingerprintTracker _fingerprintTracker = new();
    private readonly Klydis.Core.Inference.IBudgetManager _budgetManager = new Klydis.Core.Inference.BudgetManager();

    public Klydis.Core.RAG.VectorStore? VectorStore { get; set; } = vectorStore;

    private readonly Klydis.Core.Tasks.TaskManager? _taskManager = taskManager;
    private readonly Klydis.Core.Tasks.AgentRuntime? _runtime = agentRuntime;

    // Cross-method supervisor signal: set when the deterministic gate accepts task_complete
    // inside the inner generation loop, read by the outer StreamResponseAsync finally to end
    // the run as Completed. Reset at the start of every turn.
    private bool _goalCompletedThisTurn;

    /// <summary>
    /// The task the most recent turn resolved to (set per turn by the task resolver), or
    /// null when no task layer is active. Exposed so the UI can stamp queue enqueues and
    /// observe the current task identity.
    /// </summary>
    public string? CurrentTaskId { get; private set; }

    /// <summary>
    /// The objective of the current task, injected into the prompt as the immutable task
    /// contract so the model always knows what task it is working (report §44) — and so the
    /// completion gate can compare claims against the actual objective.
    /// </summary>
    public string? CurrentTaskObjective { get; private set; }

    /// <summary>
    /// The active agent runtime instance, if configured.
    /// </summary>
    public Klydis.Core.Tasks.AgentRuntime? AgentRuntime => _runtime;

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
    // (IsGoalMode was removed: it defaulted to true and nothing ever drove it, so every
    // message got the AUTONOMOUS GOAL EXECUTION MODE section — the active mode is decided
    // per-turn by the explicit isGoalMode argument or the interaction-mode classifier.)
    public string CurrentSessionId { get; private set; } = Guid.NewGuid().ToString();

    private Klydis.Core.Protocol.ModelProfile? _currentModelProfile;
    private string? _profileBuiltForModel;
    private Klydis.Core.Protocol.IModelProtocol? _currentProtocolAdapter;
    private string? _protocolAdapterForModel;

    /// <summary>
    /// The P1 model profile for the currently loaded model — the authoritative description of
    /// how THIS model communicates (template, reasoning protocol, tool protocol, capability
    /// levels). Built lazily from the inference engine's metadata via
    /// <see cref="Klydis.Core.Protocol.ModelProfileFactory"/>; the protocol fingerprint is
    /// logged once per model so every session starts with a visible protocol decision.
    /// Null when no model is loaded or the profile cannot be built.
    /// </summary>
    public Klydis.Core.Protocol.ModelProfile? CurrentModelProfile
    {
        get
        {
            // inferenceEngine may be null when the engine is partially constructed (tests
            // build it via GetUninitializedObject); no engine means no profile — the caller
            // falls back to the legacy paths.
            if (inferenceEngine == null ||
                !inferenceEngine.IsModelLoaded ||
                string.IsNullOrWhiteSpace(inferenceEngine.CurrentModelPath))
            {
                return null;
            }
            if (_profileBuiltForModel != inferenceEngine.CurrentModelPath)
            {
                _profileBuiltForModel = inferenceEngine.CurrentModelPath;
                _currentModelProfile = null;
                try
                {
                    var profile = Klydis.Core.Protocol.ModelProfileFactory.Build(
                        inferenceEngine.CurrentModelPath,
                        inferenceEngine.CurrentModelPath,
                        inferenceEngine.Architecture ?? "unknown",
                        rawChatTemplate: inferenceEngine.RawChatTemplate,
                        declaredTemplate: null,
                        explicitOverride: null);
                    // GGUF-derived stop tokens (eos/bos token text) merged with the
                    // template-family defaults as a safety net (blueprint TODO 012).
                    profile = profile with { StopTokens = ResolveProfileStopTokens(inferenceEngine.CurrentModelPath, profile.Template) };
                    _currentModelProfile = profile;
                    var protocolKey = Klydis.Core.Protocol.ProtocolRegistry.ResolveProtocolKey(profile) ?? "legacy-fallback";
                    logger.LogInformation(
                        "ModelProfile: id={Model} arch={Arch} template={Template} reasoning={Reasoning} tools={ToolProtocol} fingerprint={Fingerprint} protocol={ProtocolKey}",
                        System.IO.Path.GetFileNameWithoutExtension(inferenceEngine.CurrentModelPath),
                        profile.Architecture, profile.Template, profile.Reasoning, profile.ToolProtocol,
                        profile.Fingerprint, protocolKey);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to build model profile; using legacy protocol fallback.");
                    _currentModelProfile = null;
                }
            }
            return _currentModelProfile;
        }
    }

    /// <summary>
    /// The P1.6 protocol adapter driving the live loop for the current model. Resolved from
    /// the model profile via <see cref="Klydis.Core.Protocol.ProtocolRegistry"/>; null means
    /// this model runs on the LEGACY protocol paths (PromptTemplateEngine + the shared
    /// ActionDialectParser) — logged here so migration progress is observable.
    ///
    /// Migration completion criterion: when every model family has a registered adapter this
    /// never logs the fallback warning, the adapter-less branch in ParseToolCalls can be
    /// deleted, and the test suite can assert 0 legacy protocol executions.
    /// </summary>
    public Klydis.Core.Protocol.IModelProtocol? CurrentProtocolAdapter
    {
        get
        {
            var profile = CurrentModelProfile;
            if (profile == null) return null;
            if (_protocolAdapterForModel != _profileBuiltForModel)
            {
                _protocolAdapterForModel = _profileBuiltForModel;
                _currentProtocolAdapter = null;
                try
                {
                    Klydis.Core.Protocol.ProtocolRegistry.RegisterDefaultAdapters();
                    _currentProtocolAdapter = Klydis.Core.Protocol.ProtocolRegistry.Resolve(profile);
                    string modelName = System.IO.Path.GetFileNameWithoutExtension(inferenceEngine.CurrentModelPath ?? "unknown");
                    if (_currentProtocolAdapter == null)
                    {
                        logger.LogWarning(
                            "Legacy protocol fallback: no registered adapter for protocol '{ProtocolKey}' (model {Model}). " +
                            "The live loop uses the shared tolerant parser until an adapter lands for this family.",
                            Klydis.Core.Protocol.ProtocolRegistry.ResolveProtocolKey(profile) ?? "unknown",
                            modelName);
                    }
                    else
                    {
                        logger.LogInformation(
                            "ProtocolAdapter selected: {Protocol} for model {Model} (key '{ProtocolKey}').",
                            _currentProtocolAdapter.GetType().Name,
                            modelName,
                            Klydis.Core.Protocol.ProtocolRegistry.ResolveProtocolKey(profile));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to resolve protocol adapter; using legacy protocol fallback.");
                    _currentProtocolAdapter = null;
                }
            }
            return _currentProtocolAdapter;
        }
    }

    /// <summary>
    /// Resolves the per-model stop-token list (blueprint TODO 012): the GGUF tokenizer's
    /// eos/bos token text when the file carries it, merged with the template-family defaults
    /// (kept as a safety net — some templates end generation on a structural token the model's
    /// own eos/bos do not cover). Falls back to the family defaults alone when the model file
    /// is unavailable or unreadable. The GGUF parse is cached, so this is cheap after the
    /// first call per model.
    /// </summary>
    private static IReadOnlyList<string> ResolveProfileStopTokens(string? modelPath, Klydis.Core.Chat.ChatTemplate template)
    {
        var defaults = new PromptTemplateEngine().GetStopTokens(template);
        if (string.IsNullOrWhiteSpace(modelPath)) return defaults;

        try
        {
            var meta = Klydis.Core.Models.GgufMetadataReader.ParseCached(modelPath);
            if (meta == null) return defaults;

            var tokens = new List<string>(defaults.Length + 2);
            if (!string.IsNullOrWhiteSpace(meta.EosToken)) tokens.Add(meta.EosToken);
            if (!string.IsNullOrWhiteSpace(meta.BosToken)) tokens.Add(meta.BosToken);
            foreach (var d in defaults)
            {
                if (!tokens.Contains(d)) tokens.Add(d);
            }
            return tokens;
        }
        catch (Exception ex)
        {
            // A metadata read failure must never break model loading — fall back to defaults.
            System.Diagnostics.Debug.WriteLine($"Failed to read GGUF stop tokens: {ex.Message}");
            return defaults;
        }
    }

    /// <summary>
    /// Deterministic completion evidence for the goal loop's verification gate: the persisted
    /// plan is the authoritative checklist, so "done" requires every item to be checked off.
    /// See <see cref="IGoalCompletionVerifier"/>.
    /// </summary>
    public IReadOnlyList<string>? GetOpenPlanItems(string sessionId)
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
            // FAIL CLOSED (P0.6): a verifier read failure must NEVER degrade to "no open
            // items" — that would accept a task_complete claim on a database fault. Return
            // null (verification unavailable) so the deterministic gate REJECTS the claim;
            // the loop continues and retries verification instead of completing falsely.
            logger.LogWarning(ex, "Failed to read open plan items for completion verification; verification is UNAVAILABLE and any completion claim will be rejected.");
            return null;
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

    // ---- Harness-owned initial planner (report §5 / §51-P0) ----
    // The runtime establishes a baseline plan for actionable requests; the model refines or
    // replaces it. This removes the "the model must remember to call plan" dependency while
    // keeping the checklist fully model-editable via the 'plan' tool. The baseline steps come
    // from InitialPlanGenerator (domain-aware), not a generic scaffold.

    // Verbs that imply a multi-step task worth a plan. Short messages and pure questions
    // ("what is 2+2") are deliberately excluded by the length gate in IsActionableTaskRequest.
    private static readonly string[] TaskActionVerbs =
    {
        "build", "create", "implement", "develop", "refactor", "migrate", "port",
        "optimize", "analyze", "investigate", "research", "debug", "fix", "test",
        "design", "configure", "integrate", "set up", "make", "write", "document",
        "compare", "evaluate", "review", "add", "install", "deploy", "summarize", "produce"
    };

    /// <summary>
    /// True when the message reads as a substantive, actionable task (long enough to be a
    /// real request AND contains a task verb). Trivial questions and one-liners skip the
    /// scaffolding ceremony entirely.
    /// </summary>
    private static bool IsActionableTaskRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length < 40) return false;
        string lower = message.ToLowerInvariant();
        return TaskActionVerbs.Any(v => lower.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Seeds a baseline plan once per user message. Seeded when the session has no plan, or
    /// when the existing plan belongs to a PREVIOUS task and the new message reads as a new
    /// task (actionable + substantial) — the harness then replaces the obsolete checklist with
    /// a fresh scaffold stamped to the new message. Short steering messages ("change the
    /// color to blue") leave the existing plan untouched so a mid-task steer keeps its
    /// checklist; a genuine steer is still re-adopted as the current task's plan the moment
    /// the model mutates it via the 'plan' tool.
    /// </summary>
    private async Task SeedInitialPlanIfNeededAsync(string sessionId, string userMessage)
    {
        try
        {
            if (!IsActionableTaskRequest(userMessage)) return;

            var existing = toolExecutor.GetSessionPlanEntries(sessionId);
            // Domain-aware initial plan (workbench spec §2): a landing-page request gets real
            // section-by-section steps, not the old generic four-item scaffold.
            var steps = InitialPlanGenerator.Generate(userMessage);
            if (existing.Count == 0)
            {
                await toolExecutor.SeedSessionPlanAsync(sessionId, steps);
                return;
            }

            string? owner = toolExecutor.GetSessionPlanOwner(sessionId);
            if (!string.IsNullOrEmpty(owner) && owner != userMessage)
            {
                bool hasUnfinishedItems = existing.Any(e => !e.Done);
                if (!hasUnfinishedItems)
                {
                    logger.LogInformation(
                        "Replacing completed plan from a previous task with a fresh harness-seeded plan for the current message.");
                    await toolExecutor.SeedSessionPlanAsync(sessionId, steps);
                }
                else
                {
                    logger.LogInformation("Continuing active plan with updated user steering: '{Message}'.", userMessage);
                }
            }
        }
        catch (Exception ex)
        {
            // Seeding must never break a turn — a missing scaffold is strictly better than a
            // failed send. The model can still establish a plan itself via the 'plan' tool.
            logger.LogDebug(ex, "Failed to seed initial plan for session {SessionId}.", sessionId);
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
    /// Stall watchdog threshold for the per-turn activity clock: if the turn produces NO
    /// stream events (tokens, tool-call boundaries, iteration progress) for this long, the
    /// turn is cancelled and ends with a stall notice. Defaults to 4 hours to accommodate
    /// large reasoning models, extensive prompt evaluation, and CPU/RAM inference.
    /// </summary>
    public TimeSpan TurnStallThreshold { get; set; } = TimeSpan.FromHours(4);
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

    /// <summary>The loaded model's context window size in tokens (diagnostics/exports).</summary>
    public uint ContextSize => inferenceEngine.ContextSize;

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
               content.StartsWith("[System: Tool calling", StringComparison.Ordinal) ||
               // P0: gate/completion rejections are runtime-control messages, not conversation.
               // They are persisted as ChatRole.Tool rows (legacy path) and must never re-enter
               // history as ordinary tool results on a later load.
               content.StartsWith("[SYSTEM — ACTION REJECTED BY ACTION GATE]", StringComparison.Ordinal) ||
               content.StartsWith("[SYSTEM — COMPLETION CLAIM REJECTED BY DETERMINISTIC VERIFIER]", StringComparison.Ordinal) ||
               content.StartsWith("[SYSTEM — ACTION LEDGER UNAVAILABLE]", StringComparison.Ordinal);
    }

    /// <summary>
    /// Short stable fingerprint of a prompt (first 6 hex chars of SHA-256) for the TurnInfo
    /// instrumentation line — enough to spot "same prompt, different turn" and "prompt changed
    /// but KV was reused" at a glance.
    /// </summary>
    private static string ComputePromptHash(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return "—";
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(prompt));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
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
            // Fall back to a per-mode estimate matching how the NEXT prompt would actually be
            // built (the last turn's mode), not a global agent-prompt estimate that overstates
            // conversation turns.
            systemTokens = await EstimateSystemPromptTokensOnceAsync(ct, _lastPromptMode);
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

    private async Task<long> EstimateSystemPromptTokensOnceAsync(CancellationToken ct, InteractionMode mode)
    {
        int idx = Math.Clamp((int)mode, 0, _estimatedSystemPromptByMode.Length - 1);
        long cached = Interlocked.Read(ref _estimatedSystemPromptByMode[idx]);
        if (cached > 0) return cached;
        try
        {
            string text;
            if (mode == InteractionMode.Conversation)
            {
                // Conversation turns build the minimal conversational prompt (no tools).
                text = new SystemPromptManager().BuildConversationalSystemPrompt(personalityMode: SelectedPersonality);
            }
            else
            {
                var tools = await toolExecutor.GetToolDefinitionsAsync();
                var schema = toolExecutor.FormatToolsForPrompt(tools);
                text = new SystemPromptManager().BuildCompactSystemPrompt(schema, personalityMode: SelectedPersonality);
            }
            long estimate = contextOrchestrator.EstimateTokens(text) + 64; // template application + safety
            Interlocked.Exchange(ref _estimatedSystemPromptByMode[idx], estimate);
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
        await _turnGate.WaitAsync(ct);
        try
        {
            await foreach (var evt in StreamResponseCoreAsync(userMessage, ct, skillContext, isGoalMode))
            {
                yield return evt;
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// The gated turn body — the full setup/generation/tool/cleanup lifecycle. Exceptions
    /// during setup (task resolution, plan seeding, message persistence) unwind through the
    /// finally below, so the shared turn state is always cleared and IsGenerating is never
    /// left stuck true with an open run.
    /// </summary>
    private async IAsyncEnumerable<ChatStreamEvent> StreamResponseCoreAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct,
        string? skillContext = null,
        bool? isGoalMode = null)
    {
        IAsyncEnumerator<ChatStreamEvent>? enumerator = null;
        try
        {
            IsGenerating = true;
            _recentTools.Clear();

            string generatingSessionId = CurrentSessionId;

            // ===== DETERMINISTIC DIRECT ACTION FAST-PATH =====
            // Recognize obvious system telemetry and desktop operational queries ("CPU load", "GPU load", "OS version",
            // "full system report", "open chrome") and execute them immediately with zero LLM reasoning latency
            // or capability hallucination risk.
            var directRoute = DirectActionRouter.TryRoute(userMessage);
            if (directRoute != null)
            {
                var directUserMsg = new ChatMessage(ChatRole.User, userMessage);
                var activeHist = _sessionHistories.GetOrAdd(generatingSessionId, _ => new List<ChatMessage>(_history));
                AddToSessionHistory(activeHist, directUserMsg, generatingSessionId);
                await messageStore.AddMessageAsync(generatingSessionId, ChatRole.User, userMessage, 0, null);

                var directResult = await DirectActionRouter.ExecuteAsync(directRoute, toolExecutor, generatingSessionId, ct);
                yield return new ChatStreamEvent(ChatStreamEventType.Token, directResult.FormattedResponse);

                var directAssistantMsg = new ChatMessage(ChatRole.Assistant, directResult.FormattedResponse);
                AddToSessionHistory(activeHist, directAssistantMsg, generatingSessionId);
                await messageStore.AddMessageAsync(generatingSessionId, ChatRole.Assistant, directResult.FormattedResponse, 0, null);

                yield return new ChatStreamEvent(ChatStreamEventType.StreamEnd, "");
                yield break;
            }

        // ===== INTERACTION-MODE BOUNDARY =====
        // Decide BEFORE task resolution whether this message is ordinary conversation or
        // executable work. Conversation turns (greetings, small talk, explanations) must
        // NEVER create a task, a run, a plan, or a task contract — the observed failure: a
        // greeting became AgentTask T-... (status Running, objective "good evening") and the
        // runtime told the model it had a task plus 20+ tools, so it answered a greeting with
        // run_command suggestions. Conversation mode strips tools, queue, skills, RAG, plan
        // and the task contract from the prompt entirely (see StreamResponseInternalAsync).
        InteractionMode mode = InteractionClassifier.Classify(userMessage);
        bool isConversationTurn = mode == InteractionMode.Conversation;

        // Remember the mode for the idle context gauge: its fallback system-prompt estimate
        // must match how the NEXT prompt would actually be built (per-mode), not a global
        // agent-prompt estimate.
        _lastPromptMode = mode;

        // Fail-closed latch: set when task resolution/storage throws for a Task/Autonomous
        // turn. The turn then runs with NO task state and NO tools, and a runtime notice is
        // surfaced instead of silently degrading to legacy session-scoped behavior.
        bool taskLayerFailed = false;

        // Goal-mode directives are driven by the INTERACTION MODE, not the app-level default.
        // Previously IsGoalMode defaulted to TRUE and nothing ever drove it, so every message
        // (including a greeting) got the AUTONOMOUS GOAL EXECUTION MODE section while the task
        // layer simultaneously said "no task" / "new task" — the contradictory signals that
        // left the model stuck between "chat" and "agent" behavior. Now: Autonomous → goal
        // mode; Task/Conversation → not. An explicit isGoalMode parameter (the goal-mode
        // toggle) still wins when provided.
        bool activeGoalMode = isGoalMode ?? mode == InteractionMode.Autonomous;

        // Set by the supervisor when a task_complete claim passes the deterministic gate;
        // ends the turn (and the run) with a completion event.
        _goalCompletedThisTurn = false;        // Stamp the user message this turn executes onto the tool executor BEFORE any tool call
        // runs, so plan mutations record the task boundary (which message's task the plan
        // belongs to). Cleared in the finally below so direct tool invocations outside a turn
        // never inherit a stale owner.
        toolExecutor.CurrentTaskUserMessage = userMessage;

        // Task resolution: classify this user message (NEW / CONTINUE / STEER / REOPEN)
        // BEFORE the model runs and persist the decision. The task the message belongs to
        // drives the plan shown, the queue offered, and the completion gate — a new task in
        // the same chat never inherits the old task's checklist, because plan/queue isolation
        // is enforced at the storage layer, not by prompting. On a task switch the previous
        // task's plan is already mirrored in its record; sessions.plan_json is swapped to the
        // new task's plan and the live checklist re-armed. Conversation turns bypass task
        // resolution entirely (no task is created for "good evening") and clear any stale
        // task context so the harness never exposes a task contract for a greeting.
        string? previousTaskId = null;
        if (isConversationTurn)
        {
            CurrentTaskId = null;
            CurrentTaskObjective = null;
            toolExecutor.CurrentTaskId = null;
            toolExecutor.CurrentRunId = null;
        }
        else if (_taskManager != null)
        {
            try
            {
                var previousTask = await _taskManager.GetCurrentTaskAsync(generatingSessionId);
                previousTaskId = previousTask?.TaskId;
                var task = await _taskManager.ResolveOrCreateCurrentTaskAsync(generatingSessionId, userMessage);
                if (task.TaskId != previousTaskId)
                {
                    // A NEW task replaced the previous one: close the previous task's run as
                    // Suspended (its task is still active/resumable) so each task's run truly
                    // spans its own execution attempt and no two tasks share one open run.
                    if (_runtime != null && previousTaskId != null)
                    {
                        await _runtime.EndRunAsync(previousTaskId, Klydis.Core.Tasks.RunStatus.Suspended);
                    }
                    // P0: a plan-read/write failure on task switch must propagate to the
                    // fail-closed handler below — the OLD task's session plan must never stay
                    // in place while the executor claims the NEW task (that would let task B
                    // execute under task A's plan). Previously this was swallowed at Debug and
                    // the turn continued with mismatched plan/task state.
                    string? newTaskPlan = await _taskManager.GetPlanAsync(task.TaskId);
                    await messageStore.SaveSessionPlanAsync(generatingSessionId, newTaskPlan);
                    toolExecutor.ResetSessionPlanState(generatingSessionId);
                }
                CurrentTaskId = task.TaskId;
                CurrentTaskObjective = task.Objective;
                toolExecutor.CurrentTaskId = task.TaskId;
                if (_runtime != null)
                {
                    await _runtime.EnsureRunAsync(task.TaskId);
                    // P0: stamp the executor with the ACTIVE RUN id so durable tool-activity and
                    // execution-event rows are (task, run)-attributable instead of RunId: null.
                    toolExecutor.CurrentRunId = _runtime.GetActiveRunId(task.TaskId);
                }
            }
            catch (Exception ex)
            {
                // FAIL CLOSED: a task-identity/storage failure must NEVER resurrect legacy
                // session-scoped state (old plan, old queue, old task contract) — that is the
                // exact contamination this architecture exists to prevent. Clear all task
                // state, disable tools for the turn, and surface the runtime error instead of
                // silently degrading to an un-scoped agent turn.
                logger.LogWarning(ex, "Task layer unavailable for session {SessionId}; failing closed (no task scoping, no tool execution this turn).", generatingSessionId);
                CurrentTaskId = null;
                CurrentTaskObjective = null;
                toolExecutor.CurrentTaskId = null;
                toolExecutor.CurrentRunId = null;
                taskLayerFailed = true;
            }
        }

        // Harness-owned initial planner: for an actionable multi-step request the runtime
        // establishes a baseline plan BEFORE the model's first turn, so the task has a durable
        // backbone (PLAN tab, completion gate, stagnation tracker) without depending on the
        // model remembering to call the 'plan' tool. Runs once per user message — see
        // SeedInitialPlanIfNeededAsync for the new-task-vs-steer boundary. Never for
        // conversation turns: a greeting must not acquire a plan.
        if (!isConversationTurn && !taskLayerFailed)
        {
            await SeedInitialPlanIfNeededAsync(generatingSessionId, userMessage);
        }

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
        
            enumerator = StreamResponseInternalAsync(generatingSessionId, activeHistory, userMessage, ct, skillContext, activeGoalMode, mode, taskLayerFailed).GetAsyncEnumerator(ct);
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
            if (enumerator != null)
            {
                await enumerator.DisposeAsync();
            }
            if (_runtime != null && !string.IsNullOrEmpty(CurrentTaskId))
            {
                // A Run is one CONTINUOUS execution attempt spanning many turns: it is closed
                // ONLY on a terminal outcome (supervisor-sealed completion) or a hard
                // interruption (user stop / cancellation). An ordinary turn end leaves the
                // run open so the next user turn continues the SAME run — the old behavior
                // opened and immediately Cancelled a fresh run around every turn, so telemetry
                // reported "Run cancelled" whenever the model simply stopped early while the
                // task remained active.
                if (_goalCompletedThisTurn)
                {
                    try
                    {
                        await _runtime.EndRunAsync(CurrentTaskId, Klydis.Core.Tasks.RunStatus.Completed);
                    }
                    catch (Exception ex)
                    {
                        // P0.8: a run-termination write failure must never crash the turn-
                        // ending path (this runs in the stream's finally). Log it loudly —
                        // the failure is surfaced in diagnostics, not silently swallowed.
                        logger.LogError(ex, "Failed to persist run termination (Completed) for task {TaskId}.", CurrentTaskId);
                    }
                }
                else if (ct.IsCancellationRequested)
                {
                    try
                    {
                        await _runtime.EndRunAsync(CurrentTaskId, Klydis.Core.Tasks.RunStatus.Interrupted);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to persist run termination (Interrupted) for task {TaskId}.", CurrentTaskId);
                    }
                }
            }
            toolExecutor.CurrentTaskUserMessage = null;
            toolExecutor.CurrentTaskId = null;
            toolExecutor.CurrentRunId = null;
            IsGenerating = false;
        }
    }

    private async IAsyncEnumerable<ChatStreamEvent> StreamResponseInternalAsync(
        string generatingSessionId,
        List<ChatMessage> activeHistory,
        string currentUserMessage,
        [EnumeratorCancellation] CancellationToken ct,
        string? skillContext = null,
        bool isGoalMode = false,
        InteractionMode mode = InteractionMode.Autonomous,
        bool taskLayerFailed = false)
    {
        // Conversation mode: the model is having a chat, not executing work. No tool schema
        // is exposed (so no tool format instructions, no qwen tools prelude, no grammar
        // constraints, and stray tool tags are never executed), and the agentic headers
        // (queue / RAG / skills / lessons / plan / task contract / artifacts) are dropped.
        // The prompt becomes BuildConversationalSystemPrompt — persona + conversation rules
        // + World State + user notes only.
        bool isConversation = mode == InteractionMode.Conversation;
        // Fail-closed: when the task layer itself failed, the turn runs with tools disabled
        // and no task context (same exposure as conversation) plus an explicit runtime notice.
        bool toolsDisabled = isConversation || taskLayerFailed;

        // P1.8 — factual state collector: what ACTUALLY changed this turn (tool executions,
        // plan/step progress). The supervisor's notion of progress comes from this, never
        // from how much text the model produced.
        var turnState = new Klydis.Core.Tasks.TurnStateCollector();
        // P0: a new user turn is a user decision — the stall clock restarts (see
        // _stalledTurnsByTask). A stale count from an earlier task/turn must not carry over.
        _stalledTurnsByTask.Clear();
        IReadOnlyList<ToolExecutor.PlanEntry>? planAtTurnStart = null;
        try
        {
            planAtTurnStart = toolExecutor.GetSessionPlanEntries(generatingSessionId);
        }
        catch (Exception ex)
        {
            // Plan unavailable — the delta simply won't report plan changes.
            logger.LogDebug(ex, "Failed to capture turn-start plan for state delta.");
        }

        // P1.6a: resolve the chat-template family through the model profile first — the
        // profile implements the corrected priority order (explicit override → embedded GGUF
        // template → declared metadata → family → generic). The legacy detector remains only
        // as a fallback when no profile is available (no model loaded). For the qwen3.x
        // thinking models this resolves to Qwen exactly as before; for models whose embedded
        // template disagrees with their architecture it now follows the embedded template
        // (P0-8 finding).
        var templateType = CurrentModelProfile?.Template
                           ?? promptEngine.DetectTemplate(
                               inferenceEngine.Architecture,
                               inferenceEngine.CurrentModelPath,
                               inferenceEngine.RawChatTemplate,
                               inferenceEngine.FineTuneName);
        // Per-model stop tokens (blueprint TODO 012): when the model profile carries GGUF-derived
        // eos/bos token text, prefer it — the family defaults remain inside the merged list as a
        // safety net, so this can only ADD correct stop behavior, never remove it.
        var profileStopTokens = CurrentModelProfile?.StopTokens;
        var nativeStopTokens = (profileStopTokens != null && profileStopTokens.Count > 0)
            ? profileStopTokens
            : promptEngine.GetStopTokens(templateType);
        var stopTokensList = new List<string>(nativeStopTokens);
        var stopTokens = stopTokensList.ToArray();
        var tools = toolsDisabled ? Array.Empty<ToolDefinition>() : await toolExecutor.GetToolDefinitionsAsync();
        // P1.8: the FULL schema is kept for the qwen-prelude/grammar gate below; the schema the
        // model actually sees is sliced per-iteration to the current step's allowed set.
        var fullToolsSchema = toolsDisabled ? string.Empty : toolExecutor.FormatToolsForPrompt(tools);

        // Qwen3.5/Qwen3.6 thinking models: resolved AUTHORITATIVELY through the model profile's
        // ReasoningProtocol (set by ModelProfileFactory from template + architecture). The profile
        // owns the decision of whether native thinking is supported, so fine-tuned, converted, or
        // partially compatible models whose template contains <tool_call> but do NOT support native
        // thinking are not incorrectly activated. Falls back to the legacy heuristic only when no
        // profile is available (model not loaded or profile construction failed).
        var modelProfile = CurrentModelProfile;
        bool isQwenThinkingModel = modelProfile != null
            ? modelProfile.Reasoning == Protocol.ReasoningProtocol.NativeThinkBlock
            : templateType == ChatTemplate.Qwen &&
              InferenceEngine.IsQwenThinkingArchitecture(inferenceEngine.Architecture) &&
              !string.IsNullOrWhiteSpace(inferenceEngine.RawChatTemplate) &&
              inferenceEngine.RawChatTemplate.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

        // ===== Adaptive learning loop =====
        // Pull the model's accumulated lessons (persisted across sessions) and decide whether it
        // gets the native <function=> tools prelude or the JSON format. A model that has failed
        // the native format repeatedly (recorded automatically by the parse-failure escalation)
        // is switched to JSON automatically on the NEXT session — the system evolves per model.
        string modelName = Klydis.Core.Learning.AdaptiveLearningService.DeriveModelName(inferenceEngine.CurrentModelPath);
        string lessonsSection = toolsDisabled
            ? string.Empty
            : await (_adaptiveLearning?.BuildLessonsSectionAsync(modelName, ct: ct) ?? Task.FromResult(string.Empty));
        bool useQwenNativePrelude = !toolsDisabled && isQwenThinkingModel && !string.IsNullOrWhiteSpace(fullToolsSchema);
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
        int maxIterations = (isGoalMode || mode == InteractionMode.Autonomous) ? 1000 : 100;

        // P1.12/review §3: the typed-step mirror is persisted only when the plan actually
        // changed (signature = text + done flags), so per-iteration step persistence never
        // becomes a hot path.
        string? lastPersistedPlanSig = null;

        // Turn-level count of task_complete claims the deterministic gate rejected this turn
        // (across all iterations). Feeds the snapshot so the supervisor's Pause decision
        // reflects the real rejection history instead of a hardcoded zero.
        int completionRejectionsThisTurnTotal = 0;

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

        // EOS-decline guard: a chunk that ends because the MODEL ended its own turn (stop
        // token) mid-sentence, with no tool call, is the model declining to continue — NOT an
        // output-budget cut. Pushing once is the auto-continuation's purpose (a lazy model
        // finishes when nudged), but a model that keeps declining answers each push with a
        // fresh tiny turn, and pushing again only churns full re-prefills of an ever-growing
        // prompt (observed: 11-16 consecutive mid-sentence chunks, ~10k output tokens, zero
        // tool calls, then a permanent stall). Cap the declines at 2, then deliver the
        // partial response with a clear notice instead of cascading.
        int eosDeclinesThisTurn = 0;
        const int MaxConsecutiveEosDeclines = 2;
        const int MaxSelfCorrectionsPerTurn = 3;

        // Autonomous no-action repair budget (protocol reliability, review §11–§14): a
        // text-only response in Autonomous mode is NOT a successful turn — the model may
        // understand the request yet refuse to enter the tool protocol ("Good morning!
        // Please tell me what you want next"). Each repair injects a COMPACT action-required
        // instruction and regenerates; the budget bounds the churn, after which the turn
        // ends with an explicit diagnostic and the task stays active.
        int noActionRepairsThisTurn = 0;
        const int MaxNoActionRepairs = 3;

        // A Reason step's deliverable is a DESIGN DIRECTION — it must be substantive. A
        // one-line commitment ("I'll get started on that right away") is filler, not a
        // deliverable; only real content satisfies the step. This is NOT "length = progress"
        // (which the supervisor rejects for execution steps) — it is the only signal a
        // text-deliverable step has, since its deliverable IS text.
        const int MinTextDeliverableLength = 80;

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

        // Turn-level stall watchdog: if NO stream activity (tokens, tool-call boundaries,
        // iteration progress) occurs for TurnStallThreshold while the turn is running, the
        // linked token is cancelled and the turn ends with a stall notice. This converts the
        // "Working… forever" failure mode into a bounded, diagnosed stop: the generation
        // pipeline can park on an await that never resumes (observed 2026-08-16: a turn froze
        // mid-auto-continuation with zero new log lines, no thread executing the pipeline,
        // and the chat header stuck on Working… indefinitely). The clock resets on every
        // token/event and around tool execution, so legitimately long tool calls and slow
        // re-prefills never trip it — 5 minutes of absolute silence is a genuine hang.
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        DateTime lastTurnActivityUtc = DateTime.UtcNow;
        bool stallWatchdogFired = false;
        bool toolCallInProgress = false;
        using var stallTimer = new System.Threading.Timer(_ =>
        {
            if (stallWatchdogFired || toolCallInProgress) return;
            if (DateTime.UtcNow - lastTurnActivityUtc > TurnStallThreshold)
            {
                stallWatchdogFired = true;
                logger.LogWarning(
                    "Turn stall watchdog fired: no stream activity for {StallMinutes:F1} minutes (threshold: {ThresholdHours:F1}h) while the turn was running. Cancelling the turn.",
                    (DateTime.UtcNow - lastTurnActivityUtc).TotalMinutes,
                    TurnStallThreshold.TotalHours);
                try { stallCts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        while (iterationCount < maxIterations)
        {
            iterationCount++;
            lastTurnActivityUtc = DateTime.UtcNow;

            // P1.8 — the current step's execution contract, resolved ONCE per iteration from the
            // durable plan. Every prompt projection in this iteration (the sliced tool schema,
            // CURRENT STEP ALLOWED TOOLS, the CURRENT ACTION CONTRACT) and the runtime Action
            // Gate derive from this TaskStep / its ActionObligation — never from re-classifying
            // step text here. ChatEngine RENDERS the obligation; StepClassifier (the single
            // owner of step semantics) is never called directly in the loop.
            Klydis.Core.Tasks.TaskStep? currentStepForTurn = null;
            Klydis.Core.Tasks.ActionObligation? currentObligationForTurn = null;
            try
            {
                currentStepForTurn = Klydis.Core.Tasks.TaskStepBuilder.CurrentStep(
                    Klydis.Core.Tasks.TaskStepBuilder.Build(
                        toolExecutor.GetSessionPlanEntries(generatingSessionId), CurrentTaskId));
                currentObligationForTurn = currentStepForTurn == null
                    ? null
                    : Klydis.Core.Tasks.ActionObligation.FromStep(currentStepForTurn);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to resolve the current step for the prompt contract.");
            }

            // P1.8 — expose ONLY the current step's allowed tools in the model schema. The model
            // sees a small, deterministic obligation surface instead of the full tool universe,
            // so a weaker model never has to hear "40 tools exist but only 3 are allowed right
            // now" in its head. The Action Gate remains the runtime second line of defense
            // against the full registered surface. When the step declares no restriction
            // (StepClassification.Default), the full schema stays visible.
            //
            // P1.10 (review §19) — task_complete is NOT exposed while plan items remain
            // open: completion is runtime-eligible, and the best validation is the one that
            // happens BEFORE generation. The gate still backstops a premature claim (the
            // model can literally not call what it never saw).
            string toolsSchema = rescueTriggered ? string.Empty : fullToolsSchema;
            if (!toolsDisabled && !rescueTriggered && currentStepForTurn?.AllowedTools != null && tools.Count > 0)
            {
                var allowedForStep = currentStepForTurn.AllowedTools
                    .Where(n => !n.Equals("task_complete", StringComparison.OrdinalIgnoreCase));
                var sliced = new List<ToolDefinition>(tools.Count);
                foreach (var t in tools)
                {
                    if (allowedForStep.Contains(t.Name)) sliced.Add(t);
                }
                if (sliced.Count > 0)
                {
                    toolsSchema = toolExecutor.FormatToolsForPrompt(sliced);
                }
            }

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
                lastTurnActivityUtc = DateTime.UtcNow;
                yield return new ChatStreamEvent(ChatStreamEventType.MemorySummarizing, "🧠 Summarizing conversation context and saving to memory...");
                int keepRecent = Math.Clamp((int)(inferenceEngine.ContextSize * 0.25), 2048, 262144);
                logger.LogInformation("Active history tokens ({Tokens}) reached rolling compression threshold ({Threshold}). Compressing older context into WorldState. Keeping {KeepRecent} recent tokens.",
                    estimatedHistoryTokens, rollingThreshold, keepRecent);
                bool compressed = await contextOrchestrator.PerformRollingCompressionAsync(activeHistory, generatingSessionId, rollingThreshold, keepRecent);
                lastTurnActivityUtc = DateTime.UtcNow;
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
        // World State is SUMMARIZED HISTORICAL CONTEXT from earlier in the conversation, not
        // an active obligation list. Without explicit framing, models treat summarized old
        // tasks inside it ("the user wants a landing page…") as open work and resume them on
        // the next turn instead of the current task (observed: the model kept executing a
        // previous task for an entire session). The injected header now says so explicitly.
        var worldStateHeader = (session != null && !string.IsNullOrWhiteSpace(session.WorldState))
            ? $"\n\nLong-term Memory / World State (summarized HISTORICAL context from earlier in this conversation):\n{session.WorldState}\n" +
              $"The World State above is background history ONLY — it is not a list of active tasks. Do NOT resume, continue, or re-attempt anything described in it unless the user's latest message explicitly asks you to."
            : "";

        if (toolExecutor.CapabilityToolBridge?.WorldModel != null)
        {
            try
            {
                var machineFactsSummary = await toolExecutor.CapabilityToolBridge.WorldModel.SummarizeStateAsync(ct);
                if (!string.IsNullOrWhiteSpace(machineFactsSummary))
                {
                    worldStateHeader = string.IsNullOrWhiteSpace(worldStateHeader)
                        ? $"\n\n{machineFactsSummary}\n"
                        : $"{worldStateHeader}\n\n{machineFactsSummary}\n";
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to query verified machine facts from WorldModel for prompt injection.");
            }
        }

        if (MessageQueue != null && toolExecutor.MessageQueue == null)
        {
            toolExecutor.MessageQueue = MessageQueue;
        }

        // Task boundary for the queue notice: when the session's plan belongs to a PREVIOUS
        // user message (an earlier task), queued items may refer to that task. The model must
        // not let old queued messages override the task in the latest message — warn it
        // explicitly instead of presenting every queued item as a fresh obligation.
        bool planBelongsToPreviousTask = false;
        try
        {
            string? planOwnerForBoundary = toolExecutor.GetSessionPlanOwner(generatingSessionId);
            planBelongsToPreviousTask = !string.IsNullOrEmpty(planOwnerForBoundary) && planOwnerForBoundary != currentUserMessage;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read plan owner for queue boundary notice.");
        }

        // Conversation turns never see the queue — queued messages are execution-state, not
        // conversation context.
        var pendingQueue = toolsDisabled ? null : MessageQueue?.GetPending(generatingSessionId, CurrentTaskId);
        var queueNotice = (pendingQueue != null && pendingQueue.Count > 0)
            ? "\n\n[PENDING QUEUED USER MESSAGES AVAILABLE]\n" +
              "You have pending queued message(s) from the user waiting in the queue:\n" +
              string.Join("\n", pendingQueue.Select(m => $"- Queue ID: {m.Id} | Mode: {m.Mode} | Content: \"{m.Content}\"")) +
              "\nWhen you reach an optimal point during your reasoning or execution task to incorporate a queued message, call tool 'incorporate_queued_message' with argument {{\"queue_id\": \"<ID>\"}} to retrieve and steer using that message." +
              (planBelongsToPreviousTask
                  ? "\nCaution: some of these queued messages may relate to your PREVIOUS task. Your LATEST message defines the CURRENT task — do not let old queued items override it. Incorporate them only if they still apply to the current task."
                  : string.Empty)
            : "";

        // Conversation turns get no RAG workspace metadata and no skill brain — both are
        // task/agent context, and injecting them is exactly what made the model treat a
        // greeting as an agent turn.
        string ragNotice = "";
        if (!toolsDisabled && VectorStore != null)
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

        var skillHeader = !toolsDisabled && !string.IsNullOrWhiteSpace(skillContext) ? $"\n\n{skillContext}" : "";

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
        if (rescueTriggered)
        {
            // P0: rescue mode is a distinct execution mode, not a boolean tweak. The ENTIRE
            // generation request is rebuilt from the mode: zero tool schema, no qwen tools
            // prelude, no plan/artifact/task-contract ceremony, no open think block — one
            // plain-answer generation carrying the latest user request and a safe task
            // summary (the WorldState header is summarized historical context, which is
            // exactly the "safe task summary" rescue should keep). Tool parsing is disabled
            // by the rescueTriggered gate at parse time, so even a stray <tool_call> in the
            // rescue output is never executed.
            sysPrompt = rescueSysMsg.Content;
            if (!string.IsNullOrWhiteSpace(worldStateHeader))
            {
                sysPrompt += worldStateHeader;
            }
            if (!string.IsNullOrWhiteSpace(CurrentTaskObjective))
            {
                sysPrompt += "\n\nTASK SUMMARY (background only — do not act on tools for it): " + CurrentTaskObjective.Trim();
            }
            sysPrompt += "\n\n[RUNTIME NOTICE] Rescue mode: tool execution is disabled for this attempt — no tool calls will be parsed or executed and no task state will change. Answer the user's latest message directly in plain text.";
        }
        else if (toolsDisabled)
        {
            // Conversation mode: minimal prompt — persona, personality, World State (background
            // history), plus user notes appended below. No tools, no task contract, no plan, no
            // queue, no RAG, no skill brain, no goal workflow.
            sysPrompt = sysPromptManager.BuildConversationalSystemPrompt(worldStateHeader, SelectedPersonality);
            if (taskLayerFailed)
            {
                // Fail-closed runtime notice: the task layer was unavailable this turn, so no
                // tools, plan, queue or task contract exist and none may be executed.
                sysPrompt += "\n\n[RUNTIME NOTICE] The task layer is temporarily unavailable this turn: task state could not be loaded, tool execution is disabled, and no plan or queue is in effect. Reply informatively from available knowledge only — do not attempt tool calls, and do not act on any earlier task.";
            }
        }
        else if (useQwenNativePrelude)
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
            var compactBase = sysPromptManager.BuildCompactSystemPrompt("", worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode, interactionMode: mode);
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
                sysPrompt = sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode, interactionMode: mode);
            }
        }
        else if (inferenceEngine.IsMixtureOfExperts)
        {
            sysPrompt = sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode, interactionMode: mode);
        }
        else
        {
            var fullPrompt = sysPromptManager.BuildCombinedPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode, interactionMode: mode, useThinkingTags: isQwenThinkingModel);
            int fullPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(fullPrompt) : contextOrchestrator.EstimateTokens(fullPrompt);
            sysPrompt = fullPromptTokens > maxTotalPromptTokens - minUserBudget
                ? sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode, interactionMode: mode)
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
            // Artifact/workbench context is task context — not injected for conversation turns.
            if (!toolsDisabled && artifacts.Count > 0)
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

        // Harness task contract: the current task (id + objective) is immutable context that
        // must survive every compaction and never be confused with earlier tasks in the same
        // chat. Injected every iteration, directly above the plan it governs.
        if (!string.IsNullOrEmpty(CurrentTaskId) && !string.IsNullOrWhiteSpace(CurrentTaskObjective))
        {
            var taskContractHeader = "\n\nHARNESS TASK CONTRACT (the current task — authoritative):\n" +
                $"  Task: {CurrentTaskId}\n" +
                $"  Objective: {CurrentTaskObjective}\n" +
                "This task defines the work. Earlier tasks in this chat are COMPLETE HISTORY — do not resume them unless this task changes.";
            sysPrompt = sysPrompt.TrimEnd() + taskContractHeader;
        }

        // Agent's current task plan / todo list, maintained through the 'plan' tool. The
        // prompt is rebuilt on EVERY loop iteration, so the model always sees its own
        // checklist (with [x] checkmarks) and its reported progress — this is what closes the
        // goal-execution feedback loop: plan → execute → update plan → re-plan → … → complete.
        try
        {
            var currentPlan = toolExecutor.GetSessionPlan(generatingSessionId);
            // Conversation turns have no plan: even a previous task's checklist must not leak
            // into a greeting's prompt (it would re-raise the old task as work).
            if (!toolsDisabled && currentPlan.Count > 0)
            {
                int planProgress = toolExecutor.GetSessionPlanProgress(generatingSessionId);
                // Task boundary: a plan is CURRENT when it belongs to the task this turn is
                // executing. With the task layer active, the executor's plan was swapped to the
                // current task on resolution, so it is current by construction — the owner
                // (message) check only applies to legacy sessions without task scoping, where
                // a plan left over from an EARLIER message in the same chat is identifiable
                // deterministically and must NOT be injected as active work. The observed
                // contamination: the model kept executing the previous task's todo list after
                // the user moved on, and the contract's "Next required action" line (derived
                // from that stale plan) was actively commanding the old task.
                string? planOwner = toolExecutor.GetSessionPlanOwner(generatingSessionId);
                bool planIsCurrentTask = !string.IsNullOrEmpty(CurrentTaskId)
                    ? true
                    : string.IsNullOrEmpty(planOwner) || planOwner == currentUserMessage;

                if (planIsCurrentTask)
                {
                    var planHeader = "\n\nCURRENT TASK PLAN (your todo list for the task in your LATEST user message — shown live to the user in the PLAN tab; keep it updated as you work and check off completed items with the 'plan' tool):\n" +
                        string.Join("\n", currentPlan.Select(l => $"  {l}")) +
                        (planProgress >= 0 ? $"\nOverall progress: {planProgress}%" : string.Empty) +
                        "\nNOTE: This plan belongs ONLY to the task in your latest user message. If your latest message is a NEW task, this plan is OBSOLETE — do NOT execute its items. Replace it with a fresh 'plan' (action=create) or clear it, then work the new task.";

                    // EXECUTION STATE continuation contract — deterministic from durable sources
                    // (plan checklist + queue), so rolling compaction can never erase the
                    // semantics of what remains REQUIRED ("D = NOT COMPLETE"). This is the
                    // model window's state, as opposed to the WorldState narrative summary.
                    var contract = ContinuationContractBuilder.Build(
                        string.Empty,
                        toolExecutor.GetSessionPlanEntries(generatingSessionId),
                        // Task-scoped (P0.7): the execution-state contract must never count
                        // queued messages from other tasks in the same session.
                        MessageQueue?.GetPending(generatingSessionId, CurrentTaskId).Count ?? 0);
                    planHeader += "\n" + ContinuationContractBuilder.Format(contract);

                    // P1-7a — the current step's allowed tools: surface the same set the Action
                    // Gate enforces at runtime. The model sees a small, deterministic obligation
                    // space instead of the full tool universe — and anything not listed is
                    // rejected before execution regardless of this text. P1.10 (review §19):
                    // task_complete is hidden while plan items remain open — completion is
                    // runtime-owned and only becomes visible when eligible.
                    try
                    {
                        // P1.8: the current step's allowed tools come from its TaskStep record
                        // (resolved once per iteration above) — never from text matching here.
                        var stepAllowedForPrompt = currentStepForTurn?.AllowedTools;
                        if (stepAllowedForPrompt != null)
                        {
                            // A non-null current step means open plan items exist → completion
                            // is not eligible yet → task_complete is not exposed (the gate still
                            // backstops it).
                            var exposedForStep = stepAllowedForPrompt
                                .Where(n => !n.Equals("task_complete", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(n => n, StringComparer.Ordinal);
                            planHeader += "\nCURRENT STEP ALLOWED TOOLS (this step may ONLY call these tools):\n" +
                                string.Join(", ", exposedForStep) +
                                "\nAny tool not listed above is REJECTED by the runtime before execution — do not call it.\n";
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Failed to resolve step allowed-tools for prompt.");
                    }

                    sysPrompt = sysPrompt.TrimEnd() + planHeader;
                }
                else
                {
                    // The checklist belongs to a task in an EARLIER user message. Present it as
                    // reference-only history, never as an obligation list: no status, no
                    // "current step", no "next required action" — those would command the old
                    // task. The model decides whether the new message steers the same task (in
                    // which case re-engaging the plan re-stamps it as current via the 'plan'
                    // tool) or starts a fresh one (in which case it must create its own plan).
                    logger.LogInformation(
                        "Plan belongs to a previous task (owner differs from the current user message); suppressing it as active work for this turn.");
                    var obsoleteHeader = "\n\nPREVIOUS TASK PLAN (checklist from an EARLIER message in this chat — NOT your current task):\n" +
                        string.Join("\n", currentPlan.Select(l => $"  {l}")) +
                        "\nThis checklist belongs to a task you already finished or moved on from. Do NOT continue, complete, or re-attempt any item above unless your LATEST user message explicitly re-engages that task. If your current task needs a checklist, create a fresh one with the 'plan' tool (action=create) — replace this one rather than executing it.";
                    sysPrompt = sysPrompt.TrimEnd() + obsoleteHeader;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load current task plan for prompt context.");
        }

        // AUTONOMOUS ACTION CONTRACT — highest-salience instruction, injected in the LAST
        // system position (immediately above the user history, directly preceding the model's
        // generation). Pins the current step and forbids the no-action failure pattern: the
        // model must not have to infer that it is supposed to act (review §31/§16).
        if (isGoalMode && !toolsDisabled)
        {
            // P1.8: the contract is rendered from the current TaskStep's ActionObligation — the
            // single owner of step semantics. ChatEngine never re-classifies step text itself
            // (no StepClassifier.Classify call lives in the loop). The obligation is resolved
            // once per iteration above; when it cannot be read, fall back to the generic
            // directive.
            string nextStepText = currentObligationForTurn?.Title
                ?? "no open plan step — verify the result and call 'task_complete'";

            bool contractProducesText = currentObligationForTurn != null &&
                currentObligationForTurn.ExpectedActionKind is
                    Klydis.Core.Tasks.StepActionKind.Summary or
                    Klydis.Core.Tasks.StepActionKind.UserInput;
            string requiredOutput = contractProducesText
                ? "REQUIRED OUTPUT THIS TURN: produce the required summary or response as visible text.\n"
                : "REQUIRED OUTPUT THIS TURN: perform the next action for the current step — inspect, write files, run commands, or verify. Use tools decisively to advance the task.\n";
            string deliverablesLine = "DELIVERABLES: code, pages, styles, scripts, and designs belong in FILES — write them with 'write_file' so the user can preview them in the PREVIEW tab.\n";

            sysPrompt = sysPrompt.TrimEnd() + "\n\n### CURRENT ACTION CONTRACT (AUTONOMOUS MODE — READ IMMEDIATELY BEFORE RESPONDING)\n" +
                "CURRENT STEP: " + nextStepText + "\n" +
                requiredOutput +
                "AUTONOMY: You own execution for this step. Reason for yourself, make sound creative and technical decisions, and use tools proactively.\n" +
                "DO NOT: greet the user, ask permission, stall, restate requirements endlessly, or describe what you would do. Execute the next action.\n" +
                deliverablesLine +
                "If the goal is finished and verified, call 'task_complete'. If the plan needs adjustments, revise it with 'plan' (action=create).";
        }

        var sysPromptMsg = new ChatMessage(ChatRole.System, sysPrompt);
        
        // Calculate system prompt size
        var sysOnlyPrompt = promptEngine.ApplyTemplate(new List<ChatMessage> { sysPromptMsg }, templateType, qwenThinking: isQwenThinkingModel);
        int sysPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(sysOnlyPrompt) : contextOrchestrator.EstimateTokens(sysOnlyPrompt);
        // Feed the EXACT system-prompt size to the idle context gauge (see EstimateCurrentContextTokensAsync).
        Interlocked.Exchange(ref _lastSystemPromptTokens, sysPromptTokens);

        // P0 audit — FINAL SYSTEM-PROMPT BUDGET RE-CHECK: the Qwen-prelude budget check above
        // runs BEFORE notes / artifacts / task contract / plan / continuation contract / action
        // contract are appended. Those optional sections can push the final system prompt past
        // the safe budget even when the prelude alone fit. History trimming cannot fix that —
        // the strict truncation loop below only evicts history and must keep the current user
        // message. When the system prompt alone exceeds maxTotalPromptTokens - minUserBudget,
        // fall back to the compact system prompt (drops the optional ceremony) so the
        // conversation-budget floor is preserved and the final prompt is guaranteed to fit.
        // Skipped in rescue mode (its prompt is deliberately small and must not be replaced)
        // and conversation mode (no optional sections are appended there).
        if (!rescueTriggered && !toolsDisabled && sysPromptTokens > maxTotalPromptTokens - minUserBudget)
        {
            logger.LogWarning("Final system prompt ({SysTokens} tokens) exceeds the prompt budget ({Budget}) after optional sections were appended; falling back to the compact system prompt so the conversation budget is preserved.",
                sysPromptTokens, maxTotalPromptTokens - minUserBudget);
            NoteLesson("sysprompt_over_budget", $"Final system prompt ({sysPromptTokens} tokens) exceeded the budget after optional sections; fell back to the compact prompt.");
            sysPrompt = sysPromptManager.BuildCompactSystemPrompt(toolsSchema, worldStateHeader, queueNotice, ragNotice, skillHeader, lessonsSection, personalityMode: SelectedPersonality, isGoalMode: isGoalMode, interactionMode: mode);
            sysPromptMsg = new ChatMessage(ChatRole.System, sysPrompt);
            sysOnlyPrompt = promptEngine.ApplyTemplate(new List<ChatMessage> { sysPromptMsg }, templateType, qwenThinking: isQwenThinkingModel);
            sysPromptTokens = inferenceEngine.IsModelLoaded ? inferenceEngine.GetTokenCount(sysOnlyPrompt) : contextOrchestrator.EstimateTokens(sysOnlyPrompt);
            Interlocked.Exchange(ref _lastSystemPromptTokens, sysPromptTokens);
        }

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

        // The session's FIRST message is force-pinned (budget-reserved + never evicted) ONLY
        // when it belongs to the CURRENT turn: either it IS the current user message (a fresh
        // goal) or this is a legacy non-task goal session (no task layer to carry the
        // objective). Previously activeHistory[0] was pinned unconditionally, so a session
        // that opened with "good evening" re-inserted that greeting at the top of EVERY
        // future prompt — including "build a landing page" turns — permanently re-priming
        // the model with the old conversational context. That is the repeated-response
        // mechanism behind the observed "the model keeps answering like it's still greeting".
        // Unpinned, the first message participates in normal history retention instead, and
        // the task contract (objective) carries the real goal.
        bool pinInitialGoal = initialUserMsg != null &&
            (initialUserMsg.Content == currentUserMessage ||
             (_taskManager == null && isGoalMode));

        // Reserve budget up front for the user's initial prompt goal when it is pinned.
        if (pinInitialGoal)
        {
            currentTokens += initialUserTokens;
        }

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

        // Iterate backwards from the most recent history message, skipping the already-reserved
        // current user message. When the initial goal is pinned, index 0 is also skipped (it is
        // re-inserted below); otherwise index 0 participates in normal retention so a stale
        // first message can age out under budget pressure like any other message. Older
        // messages are dropped first when the budget runs out, so the tail (tool continuations
        // after the user's message) and the current message itself always survive.
        int messagesBeforeCurrent = 0;
        for (int i = activeHistory.Count - 1; i >= (pinInitialGoal ? 1 : 0); i--)
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

        // Preserve the user's initial prompt goal at index 0 ONLY when pinned (see above);
        // otherwise the first message stays wherever the backward pass retained it, and can
        // age out like any other history. The current user message is always placed at its
        // chronological position (right before any retained tool-continuation messages).
        if (pinInitialGoal && initialUserMsg != null)
        {
            activeMessages.Insert(0, initialUserMsg);
        }
        // The current message sits right after the retained messages that preceded it. When
        // the initial goal is pinned it occupies slot 0, so the position is 1 + count of
        // retained messages before the current one; otherwise it is just that count.
        if (currentUserMsg != null)
        {
            activeMessages.Insert((pinInitialGoal ? 1 : 0) + messagesBeforeCurrent, currentUserMsg);
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
        // least the current user message intact (index 0 is never removed). The old
        // guard (Count > 1) stripped everything down to the initial message, silently dropping
        // the message the user just sent once the system prompt overflowed the budget — the
        // model then answered the session's first message on every turn.
        //
        // The current user message is inserted at 1 + messagesBeforeCurrent (right before any
        // retained tool continuations), so it is NOT necessarily the last message. The removal
        // loop must never evict it: once the oldest remaining message would be the current user
        // message, fall back to dropping the OLDEST tool continuation instead (keeping the
        // newest tool result, which the model needs for its next step).
        int currentUserPos = currentUserMsg != null ? (pinInitialGoal ? 1 : 0) + messagesBeforeCurrent : -1;
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
            // main loop: never evict the current user message (see the currentUserPos logic
            // above — located by identity here, since value equality would match an earlier
            // identical message when the user repeats themselves).
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

        // Per-model/per-step thinking budget (P0 audit): the model profile's MaxStepThinkingTokens
        // is the per-step cap for THIS model (4096 for native-thinking models). Ordinary text /
        // Reason steps keep the profile default; complex planning and artifact-implementation
        // steps get a larger cap. The inference engine enforces the cap only while the model is
        // inside a thinking block (see InferenceEngine.MaxThinkingTokensPerGenerationOverride).
        // Null means the engine's context-derived default applies (non-thinking models or no
        // profile). Set before every generation so a step change this iteration is reflected.
        int? stepThinkingCap = modelProfile?.MaxStepThinkingTokens;
        if (stepThinkingCap is > 0 && currentStepForTurn != null)
        {
            stepThinkingCap = currentStepForTurn.ExpectedActionKind switch
            {
                Klydis.Core.Tasks.StepActionKind.Plan
                    or Klydis.Core.Tasks.StepActionKind.FileMutation
                    or Klydis.Core.Tasks.StepActionKind.CommandExecution
                    or Klydis.Core.Tasks.StepActionKind.TerminalInteraction
                    or Klydis.Core.Tasks.StepActionKind.Verification => Math.Max(stepThinkingCap.Value, 8192),
                _ => stepThinkingCap
            };
        }
        // The override lives on the concrete engine (not the interface) so fakes and tests stay
        // stable; the real engine enforces the cap while the model is inside a think block.
        if (inferenceEngine is InferenceEngine realEngine)
        {
            realEngine.MaxThinkingTokensPerGenerationOverride = stepThinkingCap is > 0 ? stepThinkingCap : null;
        }

        // Stream tokens
        bool generationStalled = false;
        var tokenStream = inferenceEngine.StreamTokensAsync(prompt, stopTokens, sysPromptTokens, stallCts.Token);
        await using (var tokenEnumerator = tokenStream.GetAsyncEnumerator(stallCts.Token))
        {
            while (true)
            {
                string token;
                try
                {
                    if (!await tokenEnumerator.MoveNextAsync()) break;
                    token = tokenEnumerator.Current;
                }
                catch (OperationCanceledException) when (stallWatchdogFired && !ct.IsCancellationRequested)
                {
                    // The stall watchdog (not the user) cancelled this generation after
                    // TurnStallThreshold of absolute silence. Surface a clear notice and end the
                    // turn so the UI's finally block runs and the Working indicator clears —
                    // previously this parked forever with zero diagnostics.
                    generationStalled = true;
                    break;
                }

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
                    lastTurnActivityUtc = DateTime.UtcNow;
                    yield return evt;
                }
            }
        }

        if (generationStalled)
        {
            logger.LogWarning("Turn generation cancelled by the stall watchdog after {StallMinutes:F1} minutes without stream activity.", TurnStallThreshold.TotalMinutes);
            yield return new ChatStreamEvent(ChatStreamEventType.Error,
                $"⚠ The model stopped responding (no output for over {TurnStallThreshold.TotalHours:F1} hours). The turn was ended so the app can recover — send your message again to continue.");
            break;
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

        // Per-generation inference-boundary instrumentation (reviewer request): one line per
        // generation tying the session/task/mode/user to the KV reuse decision (from the
        // engine's GenerationContext line) so a "new user turn running on the previous turn's
        // context" failure is visible at a glance instead of hiding in debug logs.
        logger.LogInformation(
            "TurnInfo: session={SessionId} task={TaskId} mode={Mode} user=\"{User}\" promptTokens={PromptTokens} promptHash={PromptHash} kv={KvDecision} prefixChars={Prefix}",
            generatingSessionId,
            CurrentTaskId ?? "—",
            mode,
            currentUserMessage.Length > 80 ? currentUserMessage.Substring(0, 80) + "…" : currentUserMessage,
            finalPromptTokens,
            ComputePromptHash(prompt),
            inferenceEngine.LastGenerationContextDecision,
            inferenceEngine.LastGenerationPrefixLength);

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
                    string? loopStepText = null;
                    if (mode == InteractionMode.Autonomous)
                    {
                        try
                        {
                            loopStepText = toolExecutor.GetSessionPlanEntries(generatingSessionId).FirstOrDefault(e => !e.Done)?.Text;
                        }
                        catch (Exception)
                        {
                            loopStepText = null;
                        }
                    }
                    loopCorrection = BuildSelfCorrectionInstruction(loopInfo.Reason, selfCorrectionsThisTurn, mode, loopStepText);
                    NoteLesson($"loop_detector_{loopInfo.Reason}",
                        $"Model entered a degenerate '{loopInfo.Reason}' loop; escalating corrective instruction injected (correction {selfCorrectionsThisTurn} of {MaxSelfCorrectionsPerTurn}).");
                }
                else if (!rescueTriggered)
                {
                    // Corrections exhausted and the model is still looping — mark that a rescue
                    // generation is wanted (activated after the truncated response is stored).
                    rescueRequested = true;
                }
            }

            // Cross-turn repetition detection: detect multi-turn repetition loops that span across generations
            if (loopInfo == null && !string.IsNullOrWhiteSpace(fullResponse))
            {
                var pastGenerations = activeHistory
                    .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))
                    .Select(m => m.Content!)
                    .ToList();
                if (Klydis.Core.Chat.GenerationLoopDetector.DetectCrossTurnRepetition(pastGenerations, fullResponse))
                {
                    logger.LogWarning("Cross-turn repetitive generation detected. Applying corrective redirect.");
                    if (selfCorrectionsThisTurn < MaxSelfCorrectionsPerTurn)
                    {
                        selfCorrectionsThisTurn++;
                        loopCorrection = "[SYSTEM — REPETITION DETECTED] You are repeating previous outputs without advancing state. Take a completely different action, inspect available evidence, or report UNKNOWN.";
                    }
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

            // STORAGE GATE: decide whether this assistant response should be persisted.
            // A thinking-only response (raw text exists but ALL of it was inside think blocks,
            // producing zero visible tokens and no tool-call tags) must NOT be stored:
            //   - it inserts an unclosed <think> block into conversation history
            //   - the next retry prompt inherits that broken block
            //   - the model repeats the same thinking-only behavior
            //   - context grows on every retry with useless reasoning
            bool hasToolCallMarkers = fullResponse.Contains("<tool_call", StringComparison.OrdinalIgnoreCase) ||
                                     fullResponse.Contains("<|tool_call", StringComparison.OrdinalIgnoreCase) ||
                                     fullResponse.Contains("<function=", StringComparison.OrdinalIgnoreCase) ||
                                     fullResponse.Contains("function=", StringComparison.OrdinalIgnoreCase);

            bool isThinkingOnlyResponse = !string.IsNullOrWhiteSpace(fullResponse)
                && visibleTextBuilder.Length == 0
                && !hasToolCallMarkers
                && (
                    // Qwen thinking model that never closed its think block
                    (isQwenThinkingModel && !fullResponse.Contains("</think>", StringComparison.OrdinalIgnoreCase)
                                         && !fullResponse.Contains("<|/think|>", StringComparison.OrdinalIgnoreCase)
                                         && !fullResponse.Contains("</thought>", StringComparison.OrdinalIgnoreCase))
                    // Or stream parser tracked that the stream remained inside think mode
                    || streamParser.IsUnclosedThink
                    || (streamParser.HasThinkingTokens && !streamParser.HasVisibleTokens)
                    // Or clean history text (stripped of think tags) is completely empty
                    || string.IsNullOrWhiteSpace(cleanHistoryResponse)
                );

            if (string.IsNullOrWhiteSpace(fullResponse))
            {
                // The entire output was degenerate/empty. Storing an empty assistant message
                // (or a bare "<think>\n" prefix with no content) pollutes history and confuses
                // later turns — it is skipped. The self-correction / rescue path below carries
                // the turn forward. (The old guard only skipped when loopTruncated was set, so
                // plain empty generations leaked empty assistant rows into the session.)
            }
            else if (isThinkingOnlyResponse)
            {
                // The model produced raw tokens but ALL of them were classified as thinking
                // (no visible text, no tool-call tags in the cleaned response). Storing this
                // would insert an unclosed <think> block into history, poisoning every
                // subsequent prompt and creating a self-reinforcing retry failure. Skip
                // storage entirely — the self-correction / rescue path below will handle
                // recovery. The raw thinking content is logged for diagnostics only.
                logger.LogWarning(
                    "Thinking-only response detected (raw length {RawLen}, visible length 0, clean length {CleanLen}). " +
                    "Skipping history persistence to prevent context poisoning from unclosed think block.",
                    fullResponse.Length, cleanHistoryResponse?.Length ?? 0);
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
                // Runtime-control channel: self-corrections are the HARNESS talking, never the
                // user — a ChatRole.User injection made models reason about the correction as
                // user intent (observed in production exports). Runtime messages are ephemeral
                // (in-memory only) and render as an explicit [RUNTIME CONTROL] directive.
                var correctionMsg = new ChatMessage(ChatRole.Runtime, loopCorrection);
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
                // P0: rescue latches the ENTIRE tool path off — no native prelude, no grammar
                // constraint, no tool schema, no parsing (the per-iteration gates below consult
                // rescueTriggered). Setting only the message/thinking booleans left tool
                // definitions in the prompt and the parse path active despite the plain-answer
                // instruction.
                useQwenNativePrelude = false;
                inferenceEngine.EnableToolGrammarConstrainedDecoding = false;
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
            // Conversation mode: no tools exist, so tool tags (if the model emits any) are
            // never parsed or executed — the whole output is delivered as the reply. The same
            // gate applies when the task layer failed (fail-closed: nothing may execute).
            var toolCallRequests = (toolsDisabled || rescueTriggered)
                ? new List<ToolCallRequest>()
                : ParseToolCalls(visibleResponse);

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
                bool replayLedgerUnavailable = false;
                bool validationEscalated = false;
                int completionRejectionsThisTurn = 0;

                // P1.7a/P1.8 — ACTION GATE setup: deterministic pre-execution validation. The
                // registered surface and the current step's execution contract (TaskStep →
                // ActionObligation, produced by the single StepClassifier) are resolved ONCE
                // per turn; every request is validated against them before execution.
                // Rejection is a RUNTIME decision — prompt text never is.
                var registeredToolDefs = await toolExecutor.GetToolDefinitionsAsync();
                TaskStep? currentTaskStep = null;
                string? currentStepText = null;
                try
                {
                    currentTaskStep = Klydis.Core.Tasks.TaskStepBuilder.CurrentStep(
                        Klydis.Core.Tasks.TaskStepBuilder.Build(
                            toolExecutor.GetSessionPlanEntries(generatingSessionId), CurrentTaskId));
                    currentStepText = currentTaskStep?.Title;
                }
                catch (Exception ex)
                {
                    // No plan state is fine — the existence gate still applies.
                    logger.LogDebug(ex, "Failed to read current step for action gate.");
                }
                var stepAllowedTools = currentTaskStep?.AllowedTools;
                int actionGateRejectionsThisTurn = 0;
                int turnActionOrdinal = 0;
                string? activeRunId = _runtime?.GetActiveRunId(CurrentTaskId ?? string.Empty);
                // P1.12: the replay ledger is RUN-scoped, not generation-scoped — reset only
                // when the active run changes so a replay across turns (context reset, model
                // retry, replan, user reconnect) is still caught. On a fresh run it is SEEDED
                // from the durable action ledger (review §9): actions a previous process
                // executed (Succeeded) or may have landed (Unknown) must still be rejected
                // after a restart — recovery never re-runs side effects just because process
                // memory reset.
                if (!string.Equals(activeRunId, _runExecutedActionsRunId, StringComparison.Ordinal))
                {
                    _runExecutedActions.Clear();
                    _fingerprintTracker.Reset();
                    if (_runtime != null && !string.IsNullOrEmpty(CurrentTaskId))
                    {
                        try
                        {
                            var durableKeys = await _runtime.GetExecutedReplayKeysAsync(CurrentTaskId);
                            // Only a SUCCESSFUL hydration marks the run as seeded — a failed
                            // hydration must be retried by the next turn, never treated as
                            // "no replay keys exist".
                            _runExecutedActionsRunId = activeRunId;
                            foreach (var k in durableKeys)
                            {
                                _runExecutedActions.Add(k);
                            }
                            if (durableKeys.Count > 0)
                            {
                                logger.LogDebug("Hydrated replay protection for task {TaskId} from the durable action ledger: {Count} replay keys.",
                                    CurrentTaskId, durableKeys.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            // P0 FAIL-CLOSED: a replay-ledger read failure must never mean
                            // "nothing executed" — that would let a side-effecting action run
                            // a second time after a storage failure. Refuse all tool
                            // execution this turn and surface a runtime notice; the run
                            // stays open and the next user turn retries hydration.
                            logger.LogError(ex, "Replay-ledger hydration FAILED for task {TaskId}; refusing tool execution this turn (fail-closed).", CurrentTaskId);
                            replayLedgerUnavailable = true;
                        }
                    }
                    else
                    {
                        _runExecutedActionsRunId = activeRunId;
                    }
                }

                if (replayLedgerUnavailable)
                {
                    string notice = "[SYSTEM — ACTION LEDGER UNAVAILABLE] The durable action ledger could not be read. Tool execution is disabled for this turn because an earlier action may have executed and must not run twice. Try again in a new message.";
                    var noticeObj = new ChatMessage(ChatRole.Tool, notice, "system");
                    AddToSessionHistory(activeHistory, noticeObj, generatingSessionId);
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, notice);
                    break;
                }

                foreach (var req in toolCallRequests)
                {
                    turnActionOrdinal++;
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

                    // P1.7a — the deterministic validation gate: a rejected action is NEVER
                    // executed and never creates tool state — the model cannot invent tools,
                    // cannot call tools the step forbids, cannot omit required arguments, and
                    // cannot invoke another tool through run_command. The rejection is
                    // injected as the tool result so the next generation sees exactly what it
                    // MAY call.
                    // P1.14 — the ActionValidator adds SEMANTIC validation: a task_complete
                    // claim is rejected BEFORE execution while completion eligibility is
                    // false (open items / unsatisfied verification / stale evidence /
                    // unresolved failures). Eligibility is the gate's evidence dimension —
                    // the model is never allowed to "finish" a task the evidence says is
                    // unfinished.
                    bool isCompletionRequest = req.Name.Equals("task_complete", StringComparison.OrdinalIgnoreCase);
                    bool completionEligibleNow = !isCompletionRequest;
                    string? completionIneligibilityReason = null;
                    if (isCompletionRequest && _runtime != null && !string.IsNullOrEmpty(CurrentTaskId))
                    {
                        try
                        {
                            var planForElig = toolExecutor.GetSessionPlanEntries(generatingSessionId);
                            var eligibility = _runtime.BuildCompletionEligibility(CurrentTaskId, planForElig);
                            var eligVerdict = Klydis.Core.Tasks.AgentSupervisor.EvaluateCompletion(
                                planForElig.Where(e => !e.Done).Select(e => e.Text).ToList(), eligibility);
                            completionEligibleNow = eligVerdict.Accepted;
                            completionIneligibilityReason = eligVerdict.Reason;
                        }
                        catch (Exception ex)
                        {
                            // Eligibility unreadable — let the claim gate decide (it fails closed).
                            logger.LogDebug(ex, "Completion eligibility could not be evaluated pre-execution; the claim gate will decide.");
                            completionEligibleNow = true;
                        }
                    }
                    var gateVerdict = Klydis.Core.Tasks.ActionValidator.ValidateForStep(
                        req, registeredToolDefs,
                        currentTaskStep == null ? null : Klydis.Core.Tasks.ActionObligation.FromStep(currentTaskStep),
                        new Klydis.Core.Tasks.ActionValidationContext(
                            CompletionIsEligible: completionEligibleNow,
                            CompletionIneligibilityReason: completionIneligibilityReason,
                            RunAlreadyExecuted: _runExecutedActions,
                            WorkspaceRoot: _runtime?.WorkspaceRoot));
                    if (!gateVerdict.Allowed)
                    {
                        actionGateRejectionsThisTurn++;
                        string actionId = Klydis.Core.Tasks.ActionGate.ComputeActionId(
                            req, CurrentTaskId, activeRunId, turnActionOrdinal);
                        string gateErrorCode = Klydis.Core.Tasks.ActionGate.ErrorCode(gateVerdict.Error!.Value);
                        var fp = _fingerprintTracker.RecordFailure(req.Name, gateErrorCode, req.Arguments, currentStepText);
                        string rejection = fp.IsStrategyBlocked
                            ? _fingerprintTracker.FormatBlockedFeedback(req.Name, gateErrorCode)
                            : BuildActionGateRejection(actionId, req, gateVerdict);
                        logger.LogWarning(
                            "ACTION_GATE_REJECTED actionId={ActionId} task={TaskId} run={RunId} step={Step} " +
                            "tool={Tool} code={Code} reason={Reason} allowed={Allowed}",
                            actionId, CurrentTaskId ?? "—", activeRunId ?? "—", currentStepText ?? "—",
                            req.Name, gateErrorCode, gateVerdict.Reason, gateVerdict.AllowedToolsSummary ?? "—");
                        NoteLesson("action_gate", $"Action rejected by the deterministic gate: tool={req.Name} code={gateErrorCode}");
                        var gateRejObj = new ChatMessage(ChatRole.Tool, rejection, req.Name);
                        AddToSessionHistory(activeHistory, gateRejObj, generatingSessionId);
                        // P0: the rejection is engine-internal feedback for THIS turn only — it is
                        // never persisted as a conversational message (see IsEngineInjectedMessage).
                        // Previously it landed in the store as a Tool row and re-entered later
                        // prompts, making the model re-litigate harness rules instead of working.
                        yield return new ChatStreamEvent(ChatStreamEventType.Error, rejection);
                        if (actionGateRejectionsThisTurn >= 4)
                        {
                            logger.LogWarning("Action gate rejected {Count} actions this turn; forcing turn termination.", actionGateRejectionsThisTurn);
                            forceTurnTermination = true;
                            break;
                        }
                        continue;
                    }

                    // req.Arguments is a non-nullable IDictionary by declaration; the null-
                    // forgiving operator silences the analyzer's over-flag of the interface-
                    // typed value being boxed into the object slot (CS8601).
                    yield return new ChatStreamEvent(ChatStreamEventType.ToolCall, req.Name, new Dictionary<string, object> { ["Arguments"] = req.Arguments! });

                    // Tool-call boundaries reset the stall clock, and the watchdog is paused
                    // for the duration: a long tool run is legitimate work bounded by the
                    // tool's own dispatch timeout (crawl_url may legitimately run 10 minutes),
                    // never a stall. The flag is cleared in finally so a throwing tool cannot
                    // leave the watchdog permanently disabled.
                    lastTurnActivityUtc = DateTime.UtcNow;
                    toolCallInProgress = true;
                    ToolResult result;
                    // P1.12/review §9: the durable action ledger — every executed tool call is
                    // recorded InProgress BEFORE execution and completed AFTER, with its replay
                    // key, side-effect level, arguments and result. A process death mid-call
                    // leaves the row InProgress, which run recovery marks Unknown (never
                    // silently Succeeded/Failed). The action id correlates the start with the
                    // completion record and any gate rejection for the same call.
                    string? runActionId = null;
                    Klydis.Core.Tasks.ActionExecutionStatus actionFinalStatus = Klydis.Core.Tasks.ActionExecutionStatus.Cancelled;
                    string? actionResultPreview = null;
                    string? actionError = null;
                    try
                    {
                        // Stamp the durable action with the executing model + protocol so the
                        // capability analyzer can attribute every action to a (model, protocol)
                        // pair — per-model telemetry is built from these rows (agent-intelligence
                        // stage §3). Model id is the stable file-name identity, matching the
                        // adaptive-learning service's model naming.
                        var executingProfile = CurrentModelProfile;
                        runActionId = _runtime?.RecordRunActionStart(
                            CurrentTaskId, activeRunId, currentTaskStep?.StepId,
                            _runtime?.GetActiveTurnId(CurrentTaskId ?? string.Empty) ?? activeRunId ?? "turn",
                            req, turnActionOrdinal,
                            modelId: executingProfile == null
                                ? null
                                : Klydis.Core.Learning.AdaptiveLearningService.DeriveModelName(executingProfile.ModelPath),
                            protocolKey: executingProfile == null
                                ? null
                                : Klydis.Core.Protocol.ProtocolRegistry.ResolveProtocolKey(executingProfile) ?? "legacy");
                        if (_runtime != null && string.IsNullOrEmpty(runActionId))
                        {
                            // P0: prepare-before-execute. The durable InProgress record could
                            // not be written, so executing this action would create a real side
                            // effect with no durable record. Fail closed: do NOT execute;
                            // surface the cause and continue the loop. (The old path executed
                            // anyway, so a storage failure produced side effects the ledger
                            // never recorded.)
                            logger.LogError("Durable action record could not be written for tool={Tool} task={Task}; the action was NOT executed (fail-closed).", req.Name, CurrentTaskId ?? "—");
                            NoteLesson("durable_action_write_failed", $"Durable action record write failed for tool={req.Name}; action was NOT executed (fail-closed).");
                            var failClosedMsg = new ChatMessage(ChatRole.Tool,
                                "[Tool Error: The action could not be durably recorded (storage/persistence failure), so it was NOT executed. The runtime will not run an action it cannot record. Retry once storage is healthy.]",
                                req.Name);
                            AddToSessionHistory(activeHistory, failClosedMsg, generatingSessionId);
                            yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ The action was blocked because its durable execution record could not be written. It was NOT executed.");
                            continue;
                        }
                        result = await toolExecutor.ExecuteToolAsync(req, generatingSessionId, stallCts.Token, inferenceEngine.CurrentModelPath);
                        actionFinalStatus = result.Success
                            ? Klydis.Core.Tasks.ActionExecutionStatus.Succeeded
                            : Klydis.Core.Tasks.ActionExecutionStatus.Failed;
                        actionResultPreview = result.Output;
                        actionError = result.Error;
                        // P1.8: record the factual tool execution for the state delta.
                        turnState.RecordTool(req.Name, result.Success);
                        // P1.8: a successful file write/edit is a factual FILE change — the
                        // delta records it so file mutation is observable progress, not just a
                        // tool that ran.
                        if (result.Success &&
                            (req.Name.Equals("write_file", StringComparison.OrdinalIgnoreCase) ||
                             req.Name.Equals("edit_file", StringComparison.OrdinalIgnoreCase)))
                        {
                            string? changedPath = null;
                            if (req.Arguments != null && req.Arguments.TryGetValue("path", out var pathArg))
                            {
                                changedPath = ToolExecutor.UnwrapJsonElement(pathArg)?.ToString();
                            }
                            turnState.RecordFileChanged(changedPath ?? req.Name);
                            // P1.12: a file change bumps the run's workspace version — every
                            // earlier build/preview evidence entry is now STALE (evidence
                            // verifies a specific file state, so it must not survive edits).
                            _runtime?.NoteRunFileChanged(CurrentTaskId ?? string.Empty);
                        }

                        // P1.10/P1.12: typed evidence. The kind tells the runtime whether the
                        // result actually verifies (a build/test/preview result) or is weak
                        // inspection (read_file/FileExists); the command TEXT decides, so
                        // "run_command succeeded" is never automatically "the application
                        // builds". The turn delta records evidence on Verification steps (the
                        // supervisor's rule 8); the RUN LEDGER records verification-relevant
                        // executions on ANY step (success or failure) so the completion gate
                        // sees the run's full verification history.
                        if (currentTaskStep?.ExpectedActionKind == Klydis.Core.Tasks.StepActionKind.Verification ||
                            IsRunVerificationRelevant(req.Name))
                        {
                            var (evidenceKind, subject) = ClassifyEvidenceResult(req.Name, req.Arguments);
                            var recordedKind = result.Success ? evidenceKind : ToFailureEvidenceKind(evidenceKind);
                            string description = $"{req.Name} {(result.Success ? "succeeded" : "failed")}";
                            if (currentTaskStep?.ExpectedActionKind == Klydis.Core.Tasks.StepActionKind.Verification)
                            {
                                turnState.RecordEvidence(recordedKind, description,
                                    subject, req.Name, currentTaskStep.StepId);
                            }
                            if (_runtime != null)
                            {
                                // The real exit code rides on the evidence (review §6–§7):
                                // BuildPassed is only meaningful when the command actually
                                // exited 0, and predicates can demand it.
                                _runtime.RecordRunEvidence(CurrentTaskId ?? string.Empty,
                                    new Klydis.Core.Tasks.Evidence(recordedKind, description,
                                        DateTime.UtcNow, subject, req.Name, currentTaskStep?.StepId,
                                        ExitCode: result.ExitCode),
                                    runId: activeRunId, actionId: runActionId);
                            }
                        }
                        // P1.12: a SUCCESSFUL side-effect-bearing action is now part of this
                        // run's executed set — the gate will reject a replay of the same
                        // action instead of duplicating its effects. Read-only tools are not
                        // recorded (re-reading is safe and legitimate).
                        if (result.Success &&
                            Klydis.Core.Tasks.ToolSideEffectClassifier.Classify(req.Name) !=
                                Klydis.Core.Tasks.ToolSideEffectLevel.ReadOnly)
                        {
                            _runExecutedActions.Add(Klydis.Core.Tasks.ActionGate.ComputeReplayKey(req));
                        }
                    }
                    finally
                    {
                        // Complete the durable action record: Succeeded/Failed from the result,
                        // or Cancelled when execution threw (the row is never left InProgress
                        // for an in-process outcome we do know).
                        if (runActionId != null && _runtime != null)
                        {
                            try
                            {
                                _runtime.RecordRunActionComplete(runActionId, actionFinalStatus,
                                    actionResultPreview, actionError);
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Failed to complete durable action record {ActionId}.", runActionId);
                            }
                        }
                        toolCallInProgress = false;
                        lastTurnActivityUtc = DateTime.UtcNow;
                    }

                    // SUPERVISOR GATE — task_complete: the model's completion claim is an
                    // INPUT, never authoritative. The deterministic verifier (AgentSupervisor)
                    // checks the plan checklist; accepted ⇒ the task is sealed Completed and
                    // the turn ends; rejected ⇒ the reason is injected as the tool result and
                    // the loop continues so the model finishes the open work.
                    if (req.Name.Equals("task_complete", StringComparison.OrdinalIgnoreCase)
                        && _runtime != null && !string.IsNullOrEmpty(CurrentTaskId))
                    {
                        string? claimSummary = null;
                        if (req.Arguments != null && req.Arguments.TryGetValue("summary", out var summaryObj))
                        {
                            claimSummary = ToolExecutor.UnwrapJsonElement(summaryObj)?.ToString();
                        }

                        var planForGate = toolExecutor.GetSessionPlanEntries(generatingSessionId);
                        var (accepted, rejectionReason) = await _runtime.EvaluateCompletionClaimAsync(CurrentTaskId, planForGate, claimSummary);

                        if (accepted)
                        {
                            _goalCompletedThisTurn = true;
                            // P1.15: the acceptance is on the SAME decision ledger as every
                            // other supervisor decision — CompleteTask is dispatched (sealed
                            // durably by EvaluateCompletionClaimAsync) and recorded, so the
                            // audit trail shows exactly when and why the task was sealed.
                            try
                            {
                                _runtime.RecordRunDecision(CurrentTaskId!, new Klydis.Core.Tasks.SupervisorDecision(
                                    Klydis.Core.Tasks.ExecutionDecision.CompleteTask,
                                    Klydis.Core.Chat.ContinuationReason.CompletionAccepted));
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Failed to record the accepted completion decision for task {TaskId}.", CurrentTaskId);
                            }
                            logger.LogInformation("Supervisor accepted task_complete for task {TaskId}; task sealed Completed.", CurrentTaskId);
                            // CurrentTaskId is non-null here (guarded by the enclosing
                            // condition), but the compiler cannot narrow a field, so suppress
                            // the nullable-into-object assignment warning explicitly.
                            yield return new ChatStreamEvent(ChatStreamEventType.GoalComplete,
                                claimSummary ?? "Task completed.",
                                new Dictionary<string, object> { ["TaskId"] = CurrentTaskId! });
                            break; // exit the tool loop; the goalCompletedThisTurn check below ends the turn
                        }

                        completionRejectionsThisTurn++;
                        completionRejectionsThisTurnTotal++;
                        string gateRejection = $"[SYSTEM — COMPLETION CLAIM REJECTED BY DETERMINISTIC VERIFIER]\nYour task_complete call was NOT accepted as completion. The harness verified the plan checklist and found open work:\n  {rejectionReason}\nReal 'done' requires every plan item to be checked off. Finish the open item(s) above, verify your work, then call task_complete again only when the checklist is genuinely empty.\n";
                        logger.LogWarning("Supervisor REJECTED task_complete for task {TaskId} (rejection {N}): {Reason}", CurrentTaskId, completionRejectionsThisTurn, rejectionReason);
                        var gateRejObj = new ChatMessage(ChatRole.Tool, gateRejection, "task_complete");
                        AddToSessionHistory(activeHistory, gateRejObj, generatingSessionId);
                        // P0: the rejection is runtime-control feedback for the current turn only —
                        // never persisted (see IsEngineInjectedMessage), so a stale rejection cannot
                        // re-enter a later prompt as an ordinary tool result.
                        yield return new ChatStreamEvent(ChatStreamEventType.CompletionRejected, rejectionReason ?? "Completion claim rejected");
                        continue; // skip the ordinary tool-result handling for this call
                    }

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

                    // Blueprint TODO 092: structured compiler/test diagnostics. When a build/test
                    // command produced error/warning output, append a compact structured block
                    // (file:line:col + code + message) so the model acts on precise locations
                    // instead of parsing raw log walls. Gated to run_command and skipped for
                    // offloaded ("context budget") directives; clean output parses to nothing and
                    // is untouched.
                    if (req.Name == "run_command" &&
                        !toolOutput.StartsWith("[Tool Output Exceeded Context Budget]", StringComparison.Ordinal))
                    {
                        try
                        {
                            var diags = Klydis.Core.Diagnostics.DiagnosticsParser.Parse(toolOutput);
                            string structured = Klydis.Core.Diagnostics.DiagnosticsParser.FormatForContext(diags);
                            if (!string.IsNullOrWhiteSpace(structured))
                            {
                                toolOutput += "\n\n" + structured;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Failed to parse compiler diagnostics from tool output.");
                        }
                    }

                    // P0.7 queue isolation: after a tool call, only messages belonging to the
                    // CURRENT task may be surfaced to the model. The session-wide read here
                    // silently reintroduced cross-task contamination — queued messages from an
                    // earlier task were offered as obligations of this task's turn.
                    var currentPendingQueue = MessageQueue?.GetPending(generatingSessionId, CurrentTaskId);
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

                if (_goalCompletedThisTurn)
                {
                    // Supervisor sealed the task — end the turn (the run is closed as
                    // Completed in StreamResponseAsync's finally).
                    break;
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
                    ? Klydis.Core.Protocol.ActionDialectParser.FirstNonEmpty(attemptedName, 1, 2, 3, 4, 5)
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

                // End-reason classification: a genuine budget/mid-stream cut (the harness ended
                // the stream) is truncation and must auto-continue. A chunk the MODEL ended
                // itself (stop token) mid-sentence, with no tool call, is the model declining
                // to continue — repeated declines are a degenerate pattern, not truncation.
                bool endedOnOwnStopToken = !hitOutputCap && !cutShortMidStream &&
                                           !inferenceEngine.LastGenerationWasCancelled &&
                                           inferenceEngine.LastGenerationLoopInfo == null;
                bool declinedMidSentence = endedOnOwnStopToken && isTruncatedMidGeneration && !visibleEmpty;
                eosDeclinesThisTurn = declinedMidSentence ? eosDeclinesThisTurn + 1 : 0;

                // Explicit continuation reason (ContinuationReason): the loop's decisions are
                // diagnosable events, never the model's own narrative of why it stopped. A
                // harness-ended stream (output cap / mid-stream cut) is genuine truncation and
                // auto-continues; a chunk the model ended itself mid-sentence is the model
                // declining to continue; anything else that resumes is step-incomplete work.
                var continuationReason = hitOutputCap || cutShortMidStream
                    ? ContinuationReason.GenerationTruncated
                    : endedOnOwnStopToken
                        ? ContinuationReason.ModelEndedEarly
                        : ContinuationReason.StepIncomplete;

                // NO-ACTION detection (shared by the supervisor outcome and the repair guard
                // below): in Autonomous mode, progress is measured by DURABLE STATE CHANGE —
                // a tool executed, a file changed, the plan mutated — never by how
                // substantive the text looks. A text-only response is a protocol failure when
                // it is a refusal/filler (greetings, permission seeking, "I am a text-only
                // agent" — the live export's pattern) OR when plan steps remain open,
                // REGARDLESS of length, markdown structure, or code fences. Length, headers,
                // bullets, and fences are not evidence of progress: that heuristic let the
                // observed failure through, where the model answered a build request with a
                // long wireframe/design essay (headings, bullets, code blocks, hundreds of
                // characters) while changing zero task state. Only the completion gate and
                // the plan state decide what counts — a text-only response with open steps is
                // repaired so the model must execute instead of narrate.
                bool refusalLike = ToolActionParser.IsActionRefusal(visibleResponse);
                bool openStepsRemain;
                try
                {
                    openStepsRemain = toolExecutor.GetSessionPlanEntries(generatingSessionId).Any(e => !e.Done);
                }
                catch (Exception)
                {
                    openStepsRemain = false;
                }
                // STEP-AWARE NO-ACTION DETECTION (P1.8): a text-only response is a protocol
                // failure ONLY on steps whose contract demands an executable action. Steps whose
                // deliverable IS text (Reason — "capture the requirements and creative
                // direction", Summary, UserInput) explicitly demand text from the model; the
                // CURRENT ACTION CONTRACT even forbids workspace tools on them. Repairing that
                // text as "no action" is a contradiction that traps the model: it produces the
                // required deliverable, gets repaired with a tool-call demand it cannot legally
                // satisfy (no workspace tools allowed), and stalls into empty/thinking-only
                // output — the observed qwen 3.6 loop of "replied without performing an action"
                // followed by "empty response". A refusal/filler is still repaired on every
                // step kind.
                bool stepProducesText = currentStepForTurn != null &&
                    currentStepForTurn.ExpectedActionKind is
                        Klydis.Core.Tasks.StepActionKind.Reason or
                        Klydis.Core.Tasks.StepActionKind.Summary or
                        Klydis.Core.Tasks.StepActionKind.UserInput;
                // A Reason step's text must be SUBSTANTIVE to count as the deliverable — a
                // short commitment is repaired exactly like the refusal case. Summary/UserInput
                // text is the deliverable regardless of length.
                bool substantiveDeliverable = currentStepForTurn?.ExpectedActionKind != Klydis.Core.Tasks.StepActionKind.Reason ||
                    visibleResponse.Trim().Length >= MinTextDeliverableLength;
                bool noActionProducedThisTurn = isGoalMode && !visibleEmpty && !toolsSuspendedForTurn && !rescueTriggered &&
                    (refusalLike || (openStepsRemain && (!stepProducesText || !substantiveDeliverable)));

                // Supervisor: classify the generation outcome and execute the runtime's
                // decision through the SINGLE dispatcher (P1.15). The decision is owned by
                // the supervisor from durable facts; the dispatcher executes its durable half
                // (task-state transitions, completion seal) and returns the directive the
                // branch mechanics below render — so the loop's choices are explicit, logged,
                // and testable instead of being scattered conditions.
                // P1.15 (Phase A): the supervisor's dispatch directive is hoisted OUTSIDE the
                // task-layer guard and the try-with-catch — the dispatch below contains yield
                // expressions (iterators cannot yield inside one), and the loop's continuation
                // machinery further below must honor the directive (e.g. only inject a
                // truncation continuation when the directive asks for it), so it must be
                // visible there too.
                Klydis.Core.Tasks.DispatchDirective? dispatchDirective = null;
                // P0: if the supervisor could not durably record/apply its decision, the loop
                // must NOT continue executing tools on unverified runtime state. Flips the
                // turn into a structured stop below (outside the try-with-catch).
                bool supervisorDispatchFailed = false;
                if (_runtime != null && !string.IsNullOrEmpty(CurrentTaskId))
                {
                    try
                    {
                        var outcome = _runtime.ClassifyGeneration(
                            hitOutputCap, cutShortMidStream,
                            inferenceEngine.LastGenerationWasCancelled,
                            endedOnOwnStopToken,
                            inferenceEngine.LastGenerationPromptFilledWindow,
                            visibleEmpty,
                            noActionProduced: noActionProducedThisTurn);
                        var planNow = toolExecutor.GetSessionPlanEntries(generatingSessionId);
                        var pendingNow = MessageQueue?.GetPending(generatingSessionId, CurrentTaskId).Count ?? 0;

                        // P1.12/review §3: persist the typed TaskStep mirror when the plan
                        // changed since the last persist, so step metadata (kind, allowed
                        // tools, criteria, status) survives restarts without re-deriving from
                        // English text.
                        var planSig = string.Join("\n", planNow.Select(e => (e.Done ? "[x] " : "[ ] ") + e.Text));
                        if (planSig != lastPersistedPlanSig)
                        {
                            lastPersistedPlanSig = planSig;
                            await _runtime.PersistStepsAsync(CurrentTaskId, planNow);
                        }

                        // P1.8: plan progress this turn is factual evidence — a step checked
                        // off is progress; text is not.
                        if (planAtTurnStart != null)
                        {
                            int doneBefore = planAtTurnStart.Count(e => e.Done);
                            int doneNow = planNow.Count(e => e.Done);
                            if (doneNow > doneBefore)
                            {
                                foreach (var done in planNow.Where(p => p.Done &&
                                             planAtTurnStart.Any(b => b.Text == p.Text && !b.Done)))
                                {
                                    turnState.RecordStepCompleted(done.Text);
                                }
                                turnState.RecordPlanChange($"plan advanced: {doneBefore} -> {doneNow} of {planNow.Count} done");
                            }
                        }

                        var currentStep = Klydis.Core.Tasks.TaskStepBuilder.CurrentStep(
                            Klydis.Core.Tasks.TaskStepBuilder.Build(planNow, CurrentTaskId));
                        // P0: feed the supervisor a REAL consecutive-stalled count instead of a
                        // hardcoded 0. Progress is the factual state delta only (tool executed,
                        // file/plan/evidence changed) — the model generating more text never
                        // resets the clock. See _stalledTurnsByTask.
                        var stateDeltaNow = turnState.Build();
                        string stallKey = CurrentTaskId ?? generatingSessionId;
                        bool madeProgressNow = !stateDeltaNow.IsEmpty;
                        int stalledNow = madeProgressNow
                            ? 0
                            : (_stalledTurnsByTask.TryGetValue(stallKey, out var priorStalled) ? priorStalled : 0) + 1;
                        _stalledTurnsByTask[stallKey] = stalledNow;
                        var snapshot = new Klydis.Core.Tasks.TaskExecutionSnapshot(
                            CurrentTaskId,
                            CurrentTaskObjective,
                            _runtime?.GetActiveRunId(CurrentTaskId ?? string.Empty),
                            currentStep,
                            planNow,
                            pendingNow,
                            outcome,
                            stateDeltaNow,
                            CompletionRejections: completionRejectionsThisTurnTotal,
                            ConsecutiveStalledTurns: stalledNow,
                            CompletionClaimAccepted: _goalCompletedThisTurn);
                        // _runtime is a captured field; the enclosing null guard cannot be
                        // narrowed across awaits, so suppress explicitly (the guard holds).
                        var decision = await _runtime!.DecideAfterTurnAsync(snapshot, maxCompletionRejections: 3);
                        logger.LogInformation(
                            "Supervisor: outcome={Outcome} decision={Decision} reason={Reason} nextStep={NextStep}",
                            outcome, decision.Decision, decision.Reason, decision.NextStepId ?? "—");
                        // P1.15 — the decision is DISPATCHED, not merely logged. The runtime
                        // records the decision against the run and executes the durable half
                        // (task-state transitions, completion seal); the returned directive
                        // is what the loop renders below. Every decision — ContinueStep,
                        // RepairProtocol, Verify, Replan, Pause, FailTask, AwaitUser,
                        // CompleteTask — flows through this single dispatcher; there is no
                        // second branch tree in ChatEngine.
                        dispatchDirective = await _runtime!.DispatchAsync(decision, snapshot);
                    }
                    catch (Exception ex)
                    {
                        // P0: dispatch is where the runtime records or applies authoritative
                        // state transitions (decision ledger, task-state changes, completion
                        // seal, run pause). If it failed we do not know whether the decision
                        // was persisted — continuing the loop would execute the next tool on
                        // state whose durability is unknown. Stop the turn; the task is left
                        // for reconciliation rather than blindly continued.
                        logger.LogError(ex, "Supervisor decision/dispatch failed; stopping the turn to avoid executing tools on unverified runtime state.");
                        supervisorDispatchFailed = true;
                    }

                    // Stop the turn on supervisor dispatch failure BEFORE any further
                    // generation/tool iteration: a structured, user-visible termination is
                    // safer than continuing on an unknown state transition.
                    if (supervisorDispatchFailed)
                    {
                        yield return new ChatStreamEvent(ChatStreamEventType.Error,
                            "⚠ The supervisor could not durably record its decision (runtime state unavailable). This turn stopped before any further tool execution — no tools ran after the failure. Retry after state reconciliation.");
                        yield break;
                    }

                    // Execute the directive: inject repair/replan/verification instructions
                    // and regenerate, end the turn with a structured notice (pause / await /
                    // fail), seal harness-verified completion, or continue the loop. The
                    // generation/streaming mechanics below are the RENDER of ContinueStep;
                    // they no longer decide what happens next. (Yield expressions must live
                    // OUTSIDE the try-with-catch above: iterators cannot yield inside one.)
                    if (dispatchDirective is { } dd)
                    {
                        bool endTurn = false;
                        switch (dd.Kind)
                        {
                            case Klydis.Core.Tasks.DispatchDirectiveKind.InjectReplan:
                                // Stagnation: the approach is not making progress — revise
                                // the plan instead of repeating the failing steps.
                                AddToSessionHistory(activeHistory, new ChatMessage(ChatRole.Runtime, dd.Message!), generatingSessionId);
                                // P0: the replan directive is runtime-control feedback for the
                                // current turn only — never persisted as conversational history.
                                // A replan is itself a state change, so it resets the stall clock.
                                _stalledTurnsByTask[CurrentTaskId ?? generatingSessionId] = 0;
                                NoteLesson("supervisor_replan", "Supervisor decided Replan; replan directive injected.");
                                yield return new ChatStreamEvent(ChatStreamEventType.Error, "↻ The supervisor ordered a replan (no progress) — plan revision injected.");
                                continue;

                            case Klydis.Core.Tasks.DispatchDirectiveKind.InjectRepair:
                            case Klydis.Core.Tasks.DispatchDirectiveKind.InjectVerificationInstruction:
                            {
                                // The supervisor's RepairProtocol / Verify decisions: a
                                // step-aware instruction injected as runtime guidance for the
                                // NEXT generation only (never persisted — see
                                // IsEngineInjectedMessage). The supervisor cannot see the
                                // loop's rescue/budget state, so the loop keeps its legacy
                                // bound here — otherwise a model that keeps failing the same
                                // way would churn full re-prefills all the way to
                                // MAX_ITERATIONS.
                                if (rescueTriggered)
                                {
                                    // The repair loop already exhausted its budget and the
                                    // rescue attempt produced this answer — deliver it
                                    // instead of re-entering the repair loop (mirrors the
                                    // legacy no-action guard's rescue exemption).
                                    endTurn = true;
                                    break;
                                }
                                string instruction = dd.Message!;
                                AddToSessionHistory(activeHistory, new ChatMessage(ChatRole.Runtime, instruction), generatingSessionId);
                                if (dd.Kind == Klydis.Core.Tasks.DispatchDirectiveKind.InjectVerificationInstruction)
                                {
                                    NoteLesson("supervisor_verify", "Supervisor decided Verify; verification instruction injected.");
                                }
                                else
                                {
                                    NoteLesson("supervisor_repair", $"Supervisor decided RepairProtocol ({dd.Reason}); repair directive injected.");
                                }
                                if (visibleEmpty)
                                {
                                    // Empty generations count against the self-correction
                                    // budget; exhausted ⇒ rescue mode (one plain direct
                                    // answer), exactly like the legacy empty-response
                                    // cascade.
                                    selfCorrectionsThisTurn++;
                                    if (selfCorrectionsThisTurn >= MaxSelfCorrectionsPerTurn)
                                    {
                                        rescueTriggered = true;
                                        isQwenThinkingModel = false;
                                        // P0: latch the tool path off with rescue (see the
                                        // loop-rescue activation for why the whole surface must
                                        // change, not just the message).
                                        useQwenNativePrelude = false;
                                        inferenceEngine.EnableToolGrammarConstrainedDecoding = false;
                                        sysPromptMsg = rescueSysMsg;
                                        NoteLesson("rescue_mode_supervisor_repair", "Supervisor repairs exhausted on empty output; rescue mode forced (plain direct answer).");
                                        logger.LogWarning("Supervisor repairs exhausted ({Max}) on empty output; switching to rescue mode.", MaxSelfCorrectionsPerTurn);
                                        yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ The model keeps producing empty responses — one final attempt with a plain direct answer…");
                                        continue;
                                    }
                                }
                                else
                                {
                                    // Text-but-no-action generations count against the
                                    // no-action repair budget; exhausted ⇒ a terminal
                                    // diagnostic with the task left active (legacy
                                    // behavior).
                                    noActionRepairsThisTurn++;
                                    if (noActionRepairsThisTurn >= MaxNoActionRepairs)
                                    {
                                        NoteLesson("no_action_exhausted_supervisor", "Supervisor no-action/verification repair budget exhausted; turn ended with a diagnostic and the task left active.");
                                        logger.LogWarning("Supervisor repair budget exhausted after {Max} repairs; ending the turn with a diagnostic (task remains active).", MaxNoActionRepairs);
                                        yield return new ChatStreamEvent(ChatStreamEventType.Error,
                                            dd.Kind == Klydis.Core.Tasks.DispatchDirectiveKind.InjectVerificationInstruction
                                                ? "⚠ The runtime verification gate could not be satisfied this turn. The task remains active — run the verification (build/tests/preview) in a follow-up message."
                                                : "⚠ The model kept responding without performing any action. The task remains active — try rephrasing, switching the model, or starting a new chat.");
                                        endTurn = true;
                                        break;
                                    }
                                }
                                yield return new ChatStreamEvent(ChatStreamEventType.Error,
                                    dd.Kind == Klydis.Core.Tasks.DispatchDirectiveKind.InjectVerificationInstruction
                                        ? "✔ Verification still required — verification instruction injected."
                                        : "⚠ The supervisor detected a protocol failure — re-engaging the model on the current step…");
                                continue;
                            }

                            case Klydis.Core.Tasks.DispatchDirectiveKind.EndTurnNotice:
                                // Pause / AwaitUser — the turn ends with a structured notice
                                // and the task stays open (resumable).
                                yield return new ChatStreamEvent(ChatStreamEventType.Error, dd.Message!);
                                endTurn = true;
                                break;

                            case Klydis.Core.Tasks.DispatchDirectiveKind.MarkFailed:
                                // FailTask — the runtime transitioned the task to Failed; the
                                // turn ends with the diagnostic.
                                yield return new ChatStreamEvent(ChatStreamEventType.Error, dd.Message!);
                                endTurn = true;
                                break;

                            case Klydis.Core.Tasks.DispatchDirectiveKind.SealCompletion:
                                // The runtime already sealed the task Completed; the directive
                                // is the completion event. Ends the run as Completed in the
                                // turn's finally below.
                                _goalCompletedThisTurn = true;
                                yield return new ChatStreamEvent(ChatStreamEventType.GoalComplete,
                                    dd.Message ?? "Task completed.",
                                    new Dictionary<string, object> { ["TaskId"] = CurrentTaskId! });
                                endTurn = true;
                                break;

                            default:
                                // ContinueLoop: the loop's continuation/end machinery below
                                // is the RENDER of the directive — the truncation continuation
                                // runs only when the directive carries the instruction flag.
                                break;
                        }
                        if (endTurn)
                        {
                            break;
                        }
                    }
                }

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
                // P1.15: when the task-layer supervisor is active, the truncation continuation
                // is the RENDER of the ContinueStep directive — it runs only when the
                // directive's ContinueLoop carries the continuation-instruction flag (reason
                // GenerationTruncated / ModelEndedEarly). The raw truncation flags above are
                // facts that fed the supervisor's decision; they no longer decide. Legacy
                // sessions (no directive) keep the raw-flag behavior.
                else if ((isTruncatedMidGeneration || hitOutputCap || cutShortMidStream) && continuationAllowed && iterationCount < maxIterations && continuationsThisTurn < MaxContinuationsPerTurn && eosDeclinesThisTurn < MaxConsecutiveEosDeclines &&
                    (dispatchDirective == null || dispatchDirective.IncludeContinuationInstruction))
                {
                    continuationsThisTurn++;
                    logger.LogInformation("Continuation: reason={Reason} (hitMaxTokens={HitCap}, midSentence={MidSentence}, cutShortMidStream={CutShort}, endedOnOwnStop={OwnStop}, chunkChars={ChunkChars}, visibleChars={VisibleChars}, declines={Declines}). Auto-continuation iteration {Count}/{Max}.",
                        continuationReason, hitOutputCap, isTruncatedMidGeneration, cutShortMidStream, endedOnOwnStopToken, fullResponse.Length, visibleResponse.Length, eosDeclinesThisTurn, continuationsThisTurn, MaxContinuationsPerTurn);
                    // Truthful wording: the model ENDED its own message mid-sentence — it was
                    // not cut off by a budget. The old phrasing ("truncated due to output token
                    // constraints") made models confabulate a fake token-budget narrative and
                    // repeat it to the user (observed: "I was terminated because I hit the
                    // per-message output token budget"). Say what actually happened and tell the
                    // model not to discuss the harness instruction with the user.
                    var continuationInstruction = "[System Instruction: You ended your previous message before completing your response. Continue immediately from the exact point where you stopped, without repeating any previously written text. This is an automatic system message — do not mention it to the user; just continue your work.]";
                    var continuationMsgObj = new ChatMessage(ChatRole.Runtime, continuationInstruction);
                    AddToSessionHistory(activeHistory, continuationMsgObj, generatingSessionId);
                    // Engine-internal continuation notice: in-memory only (see IsEngineInjectedMessage).
                }
                else if (declinedMidSentence && eosDeclinesThisTurn >= MaxConsecutiveEosDeclines)
                {
                    // The model ended its OWN turn mid-sentence {MaxConsecutiveEosDeclines}
                    // consecutive times with no tool call. Auto-continuation's job is to nudge a
                    // lazy model once; a model that keeps declining answers each push with a
                    // fresh tiny turn, and pushing further only churns full re-prefills (the
                    // observed 11-16 chunk cascade). Deliver the partial response and let the
                    // user nudge manually — the turn ends cleanly instead of parking.
                    logger.LogWarning("Continuation halted: reason={Reason} — model ended its own turn mid-sentence {Declines} consecutive times with no tool call; delivering the partial response.",
                        ContinuationReason.ModelEndedEarly, eosDeclinesThisTurn);
                    NoteLesson("repeated_eos_decline", "Model ended its own turn mid-sentence repeatedly; continuation cascade stopped after 2 declines and the partial response was delivered.");
                    yield return new ChatStreamEvent(ChatStreamEventType.Error,
                        "⚠ The model ended its reply mid-sentence several times without completing it. The partial response was kept — send \"continue\" to have it resume.");
                    break;
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
                    // Distinguish thinking-only from transport-empty for diagnostics:
                    // a thinking-only response has raw content that was all inside think blocks;
                    // a transport-empty response produced zero tokens entirely.
                    bool wasThinkingOnly = !string.IsNullOrWhiteSpace(fullResponse) && visibleTextBuilder.Length == 0;
                    string emptyReason = wasThinkingOnly ? "thinking_only" : "empty_response";
                    NoteLesson(emptyReason, $"Model produced {(wasThinkingOnly ? "internal reasoning but no visible answer" : "an empty visible response")}; self-correction injected (correction {selfCorrectionsThisTurn}).");
                    logger.LogWarning("Model produced {Reason}. Injecting self-correction (correction {Count} of {Max} this turn).",
                        wasThinkingOnly ? "internal reasoning but no visible answer" : "an empty visible response",
                        selfCorrectionsThisTurn, MaxSelfCorrectionsPerTurn);

                    if (wasThinkingOnly)
                    {
                        // Critical fix: The model already performed its thinking pass.
                        // Disabling isQwenThinkingModel prevents the prompt template from appending "<think>\n" AGAIN,
                        // which would trap the model in a perpetual reasoning loop across all retry attempts.
                        isQwenThinkingModel = false;
                    }

                    // Step-aware correction (P1.8): an empty generation (typically a qwen
                    // thinking model that only reasoned and never closed its think block) needs
                    // to know WHAT to produce, not just that it produced nothing — otherwise it
                    // re-enters the same reasoning-only stall. The directive re-states the
                    // current step's obligation (text deliverable or allowed tools).
                    string emptyStepDirective = currentStepForTurn?.Title != null
                        ? " The current step is: '" + currentStepForTurn.Title + "'. " + BuildAutonomousDirective(currentStepForTurn.Title)
                        : string.Empty;
                    // The channel warning matters for qwen thinking models: they reason inside
                    // the open think block and can emit ZERO visible text while having actually
                    // produced the deliverable internally. The correction tells them to
                    // produce visible text directly without opening another think block.
                    var emptyCorrection = wasThinkingOnly
                        ? "[System Self-Correction: Your previous response was EMPTY — you produced internal reasoning but no visible answer (reasoning alone does not count). Close your reasoning and produce the required output directly now without <think> tags." + emptyStepDirective + " If you produced the deliverable inside your thinking, WRITE IT OUT as visible reply text now — visible text is the only thing that reaches the user.]"
                        : "[System Self-Correction: Your previous response was EMPTY — you produced no actual visible content (reasoning alone does not count). Close your reasoning and produce the required output now." + emptyStepDirective + " If you produced the deliverable only inside your thinking, WRITE IT OUT as visible reply text now — visible text is the only thing that reaches the user. Do not just close tags or emit whitespace.]";
                    var emptyMsgObj = new ChatMessage(ChatRole.Runtime, emptyCorrection);
                    AddToSessionHistory(activeHistory, emptyMsgObj, generatingSessionId);
                    // Engine-internal correction: in-memory only (see IsEngineInjectedMessage).
                    yield return new ChatStreamEvent(ChatStreamEventType.Error,
                        wasThinkingOnly
                            ? "⚠ Model produced internal reasoning but no visible answer — self-correcting…"
                            : "⚠ Model produced an empty response — self-correcting…");
                }
                else if (visibleEmpty && !rescueTriggered)
                {
                    // Empty responses exhausted the correction budget — rescue mode: plain
                    // direct answer without tools or thinking blocks (see the rescue activation
                    // above). One final attempt so the user is never left with nothing.
                    rescueTriggered = true;
                    isQwenThinkingModel = false;
                    // P0: latch the tool path off with rescue (see the loop-rescue activation).
                    useQwenNativePrelude = false;
                    inferenceEngine.EnableToolGrammarConstrainedDecoding = false;
                    sysPromptMsg = rescueSysMsg;
                    logger.LogWarning("Empty-response corrections exhausted ({Max}). Switching to rescue mode: plain direct answer without tools or thinking blocks.", MaxSelfCorrectionsPerTurn);
                    NoteLesson("rescue_mode_empty", "Rescue mode triggered after repeated failures to produce visible output; plain direct answer forced.");
                    yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ Model produced internal reasoning but keeps failing to produce a visible answer — one final attempt with a plain direct answer…");
                    continue;
                }
                else
                {
                    // No tool calls, no tool call tag attempted, and the response is complete.

                    // AUTONOMOUS NO-ACTION GUARD (protocol reliability, review §11–§14): a
                    // refusal/filler or short no-progress commitment in Autonomous mode is a
                    // protocol failure, NOT a completed turn — the model understands the
                    // request yet refuses to enter the tool protocol (the observed "Good
                    // morning! Please tell me what you want next" pattern). Repair: inject a
                    // COMPACT action-required instruction and regenerate, bounded. Pure text is
                    // only accepted after the budget is exhausted — and even then with an
                    // explicit diagnostic, never silently as successful task progress.
                    if (noActionProducedThisTurn &&
                        noActionRepairsThisTurn < MaxNoActionRepairs &&
                        ToolActionParser.Classify(visibleResponse) == ToolActionKind.NoAction)
                    {
                        noActionRepairsThisTurn++;
                        logger.LogWarning("Autonomous no-action response (repair {Repair}/{Max}): the model produced text but no tool action; injecting the action-required repair instruction.",
                            noActionRepairsThisTurn, MaxNoActionRepairs);
                        NoteLesson("no_action_produced", $"Autonomous turn produced no tool action; action-required repair injected (repair {noActionRepairsThisTurn}).");

                        string? repairStepText = currentStepForTurn?.Title;
                        if (string.IsNullOrWhiteSpace(repairStepText))
                        {
                            try
                            {
                                repairStepText = toolExecutor.GetSessionPlanEntries(generatingSessionId).FirstOrDefault(e => !e.Done)?.Text
                                    ?? "finish and verify the remaining work";
                            }
                            catch (Exception)
                            {
                                repairStepText = "finish and verify the remaining work";
                            }
                        }
                        var noActionMsg = "[System Instruction: The current task is active and incomplete. Your previous response produced only text/questions — no tool action, no file change, no state change.\n" +
                            "CRITICAL AUTONOMY DIRECTIVE: Do NOT ask clarifying questions, do NOT ask for brand/palette/budget details, and do NOT request confirmation before starting.\n" +
                            "The user expects you to BUILD IMMEDIATELY. Pick modern, tasteful design choices and execute the next tool action (write_file, edit_file, run_command, or list_directory) NOW.\n" +
                            "CURRENT STEP: " + repairStepText + "\n" +
                            BuildAutonomousDirective(repairStepText) + "\n" +
                            "Do NOT greet the user. Do NOT ask what to do next. Do NOT describe what you would do — execute a tool call immediately.]";
                        var noActionMsgObj = new ChatMessage(ChatRole.Runtime, noActionMsg);
                        AddToSessionHistory(activeHistory, noActionMsgObj, generatingSessionId);
                        yield return new ChatStreamEvent(ChatStreamEventType.Error, "⚠ The supervisor detected an inactive response — directing model to execute tools immediately…");
                        continue;
                    }

                    if (noActionRepairsThisTurn >= MaxNoActionRepairs && noActionRepairsThisTurn > 0)
                    {
                        NoteLesson("no_action_exhausted", "Autonomous no-action repair budget exhausted; turn ended with a diagnostic and the task left active.");
                        logger.LogWarning("Autonomous no-action repair budget exhausted after {Max} repairs; ending the turn with a diagnostic (task remains active).", MaxNoActionRepairs);
                        yield return new ChatStreamEvent(ChatStreamEventType.Error,
                            "⚠ The model kept responding without performing any action. The task remains active — try rephrasing, switching the model, or starting a new chat.");
                        break;
                    }

                    // AUTONOMOUS MULTI-STEP LONG-HORIZON CONTINUATION:
                    // In autonomous / goal mode, if the model completed a step and open plan steps remain,
                    // automatically advance the plan and continue the agentic loop without stopping after a single message!
                    if ((isGoalMode || mode == InteractionMode.Autonomous) && openStepsRemain && currentStepForTurn != null)
                    {
                        await toolExecutor.AdvancePlanItemDoneAsync(generatingSessionId, currentStepForTurn.Title);
                        var remainingPlan = toolExecutor.GetSessionPlanEntries(generatingSessionId);
                        var nextStep = remainingPlan.FirstOrDefault(e => !e.Done);
                        if (nextStep != null)
                        {
                            logger.LogInformation("Autonomous loop auto-advancing from completed step '{Current}' to next step '{Next}'.",
                                currentStepForTurn.Title, nextStep.Text);
                            var advanceMsg = $"[Autonomous Execution: Step '{currentStepForTurn.Title}' completed. Proceeding immediately to Next Step: '{nextStep.Text}'. Reason independently, execute the required tools, and continue building the deliverable.]";
                            AddToSessionHistory(activeHistory, new ChatMessage(ChatRole.Runtime, advanceMsg), generatingSessionId);
                            yield return new ChatStreamEvent(ChatStreamEventType.Error, $"✔ Step completed — auto-advancing to: {nextStep.Text}…");
                            continue;
                        }
                        else
                        {
                            // All steps completed! Prompt model to verify and call task_complete.
                            var completePrompt = "[Autonomous Execution: All plan steps are complete. Perform final verification if needed, and call tool 'task_complete' with a summary of the completed work.]";
                            AddToSessionHistory(activeHistory, new ChatMessage(ChatRole.Runtime, completePrompt), generatingSessionId);
                            yield return new ChatStreamEvent(ChatStreamEventType.Error, "✔ All steps complete — finalizing and verifying deliverable…");
                            continue;
                        }
                    }

                    break;
                }
            }
        }

        if (iterationCount >= maxIterations)
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
                "⚠ The model produced internal reasoning but no visible response after exhausting self-correction and rescue attempts. Please try rephrasing your request or adjusting the generation settings.");
        }

        yield return new ChatStreamEvent(ChatStreamEventType.StreamEnd, "");
    }

    /// <summary>
    /// Builds the corrective instruction injected into history when a degenerate loop is
    /// detected. The message is reason-specific so the model knows exactly what it did wrong
    /// (tag spam vs token stutter vs phrase cycling) and how to recover.
    /// </summary>
    private static string BuildSelfCorrectionInstruction(string reason, int attempt, InteractionMode mode = InteractionMode.Conversation, string? currentStep = null)
    {
        // In Autonomous mode the correction must NEVER disable the tool protocol: "no tool
        // calls" as a loop correction would strip away the very capability the task requires
        // (the observed contradiction — the runtime ordered the model to stop calling tools
        // while the current step demanded a write_file). The correction instead re-states the
        // current step and demands ONE executable action. The "current step" is read from the
        // live plan (the same source the no-action repair uses).
        //
        // Not every step demands a tool call: a verification step needs evidence (build/test/
        // inspection) and a summarize step needs a textual deliverable. For those, demanding a
        // raw tool call is just as wrong as banning tools on an implementation step — the
        // directive follows the step's expected action instead of assuming tool_call.
        string autonomousDirective = BuildAutonomousDirective(currentStep);

        return attempt >= 3
            ? mode == InteractionMode.Autonomous
                ? $"[System Self-Correction: FINAL WARNING — you are STILL repeating the same output in a loop. STOP repeating immediately. {autonomousDirective} No thinking tags, no filler.]"
                : "[System Self-Correction: FINAL WARNING — you are STILL repeating the same output in a loop. STOP repeating immediately. Re-read the user's latest message and fulfill it with NEW content that does not repeat anything you already wrote. No thinking tags, no tool calls, no filler.]"
            : attempt == 2
                ? mode == InteractionMode.Autonomous
                    ? $"[System Self-Correction: You are STILL looping on the same output. STOP generating repetitive text immediately. {autonomousDirective} No thinking tags, no preamble. Your previous looped text has been discarded.]"
                    : "[System Self-Correction: You are STILL looping on the same output. STOP generating repetitive text immediately. Re-read the user's original message and continue the task with NEW, non-repeating content — no thinking tags, no tool calls, no preamble. Your previous looped text has been discarded.]"
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
    }

    /// <summary>
    /// Detects a user request to take a creative lead ("TAKE A CREATIVE LEAD", "don't expect
    /// details from me", "your call", ...). These requests flip the UnknownPolicy to
    /// CreativeProposal: missing details become clearly-labeled proposals, never blockers —
    /// the model must not ask for what the user explicitly delegated. Detection is
    /// conservative: only explicit creative-freedom signals match; everything else keeps the
    /// ask-first default.
    /// </summary>
    private static bool IsCreativeLeadRequest(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        string t = userMessage.ToLowerInvariant();
        return t.Contains("creative lead") ||
               t.Contains("creative freedom") ||
               t.Contains("creative control") ||
               t.Contains("don't expect details") ||
               t.Contains("dont expect details") ||
               t.Contains("do not expect details") ||
               t.Contains("your call") ||
               t.Contains("you decide") ||
               t.Contains("use your judgment") ||
               t.Contains("take the lead");
    }

    /// <summary>
    /// Builds the action directive for an autonomous loop correction from the current step
    /// text. The directive follows the step's expected action instead of assuming a tool
    /// call: implementation/inspection steps demand ONE tool call; verification steps demand
    /// evidence (build, tests, inspection); summary/presentation steps demand the final
    /// deliverable as text.
    ///
    /// TEMPORARY BRIDGE (P0): this derives execution semantics from English step text.
    /// P1 replaces it with data-driven TaskStep contracts (ExpectedAction / AllowedTools /
    /// VerificationCriteria / CompletionCondition) so the directive becomes a pure switch on
    /// TaskStep.ExpectedAction with no phrase matching. Deliberately conservative: only
    /// explicit verification/summarization markers switch the directive, everything else
    /// defaults to the tool-call demand.
    /// </summary>
    /// <summary>
    /// Maps a successful tool execution on a Verification step to the typed evidence it
    /// actually produced (P1.10). For commands the ARGUMENTS decide — the same tool name can
    /// produce BuildPassed ("npm run build") or CommandSucceeded ("echo hello"); for file
    /// tools the subject is the path. Build/test/preview/screenshot results are verifying
    /// evidence; static inspection and plain command success are not — "a tool ran" is never
    /// "the thing was verified".
    /// </summary>
    private static (Klydis.Core.Tasks.EvidenceKind Kind, string? Subject) ClassifyEvidenceResult(
        string toolName, IDictionary<string, object>? args)
    {
        string? subject = null;
        string? command = null;
        if (args != null)
        {
            foreach (var key in new[] { "path", "file", "target" })
            {
                if (args.TryGetValue(key, out var pathArg))
                {
                    subject = ToolExecutor.UnwrapJsonElement(pathArg)?.ToString();
                    if (!string.IsNullOrWhiteSpace(subject)) break;
                }
            }
            foreach (var key in new[] { "command", "arguments", "cmd", "name" })
            {
                if (args.TryGetValue(key, out var cmdArg))
                {
                    command = ToolExecutor.UnwrapJsonElement(cmdArg)?.ToString();
                    if (!string.IsNullOrWhiteSpace(command)) break;
                }
            }
        }

        switch (toolName.ToLowerInvariant())
        {
            case "read_file":
            case "view_file":
            case "list_directory":
            case "list_dir":
            case "search_files":
            case "grep_search":
                return (Klydis.Core.Tasks.EvidenceKind.FileExists, subject);
            case "write_file":
            case "edit_file":
            case "create_file":
            case "delete_file":
                return (Klydis.Core.Tasks.EvidenceKind.FileChanged, subject);
            case "screenshot":
            case "capture_screenshot":
                return (Klydis.Core.Tasks.EvidenceKind.ScreenshotCaptured, subject);
        }

        // Command tools: the COMMAND TEXT decides the evidence kind. If arguments were
        // unreadable, fall back to the tool name.
        string probe = (command ?? toolName).ToLowerInvariant();
        if (probe.Contains("build") || probe.Contains("compile"))
        {
            return (Klydis.Core.Tasks.EvidenceKind.BuildPassed, command ?? subject);
        }
        if (probe.Contains("test") || probe.Contains("pytest") || probe.Contains("mocha"))
        {
            return (Klydis.Core.Tasks.EvidenceKind.TestPassed, command ?? subject);
        }
        if (probe.Contains("preview") || probe.Contains("dev") || probe.Contains("serve"))
        {
            return (Klydis.Core.Tasks.EvidenceKind.PreviewLoaded, command ?? subject);
        }
        return (Klydis.Core.Tasks.EvidenceKind.CommandSucceeded, command ?? subject);
    }

    /// <summary>
    /// True when a tool execution can produce verification-relevant evidence for the RUN
    /// ledger (P1.12) regardless of the step it ran on — commands, builds, tests, previews,
    /// screenshots. A build run during an implementation step still counts for the
    /// completion gate; weak inspection (read_file) does not flood the ledger.
    /// </summary>
    private static bool IsRunVerificationRelevant(string toolName)
    {
        string t = toolName.ToLowerInvariant();
        if (t is "run_command" or "run_command_nowait" or "screenshot" or "capture_screenshot")
        {
            return true;
        }
        return t.Contains("build") || t.Contains("test") || t.Contains("preview") ||
               t.Contains("pytest") || t.Contains("mocha") || t.Contains("compile");
    }

    /// <summary>The failure counterpart of a success evidence kind (P1.10) — a failed build
    /// records BuildFailed, never BuildPassed, so unresolved failures block completion.</summary>
    private static Klydis.Core.Tasks.EvidenceKind ToFailureEvidenceKind(Klydis.Core.Tasks.EvidenceKind kind)
        => kind switch
        {
            Klydis.Core.Tasks.EvidenceKind.BuildPassed => Klydis.Core.Tasks.EvidenceKind.BuildFailed,
            Klydis.Core.Tasks.EvidenceKind.TestPassed => Klydis.Core.Tasks.EvidenceKind.TestFailed,
            Klydis.Core.Tasks.EvidenceKind.PreviewLoaded or Klydis.Core.Tasks.EvidenceKind.PreviewStarted =>
                Klydis.Core.Tasks.EvidenceKind.PreviewFailed,
            _ => Klydis.Core.Tasks.EvidenceKind.CommandFailed
        };

    private static string BuildAutonomousDirective(string? currentStep)
    {
        if (string.IsNullOrWhiteSpace(currentStep))
        {
            return "Execute exactly ONE tool call that advances the current task. Do NOT plan, do NOT narrate, do NOT re-enter reasoning — execute.";
        }

        // P1.8: the directive is derived from the step's ACTION OBLIGATION — the classifier
        // (single owner of step semantics) produces the expected action kind and allowed
        // tools; this method only renders them. No phrase matching lives here anymore.
        var step = Klydis.Core.Tasks.TaskStepBuilder.FromPlanEntry(
            new ToolExecutor.PlanEntry(currentStep, false), 0, null);
        var obligation = Klydis.Core.Tasks.ActionObligation.FromStep(step)!;
        string tools = obligation.AllowedTools == null
            ? "any registered tool (existence-checked by the runtime)"
            : string.Join(", ", obligation.AllowedTools.OrderBy(n => n, StringComparer.Ordinal));

        return obligation.ExpectedActionKind switch
        {
            Klydis.Core.Tasks.StepActionKind.Summary =>
                $"The current step '{currentStep}' requires the final deliverable. Produce it NOW — write it to a file if it is code or a document ('write_file'), then present the result. Allowed tools for this step: [{tools}]. Do NOT plan, do NOT narrate, do NOT re-enter reasoning — deliver.",
            Klydis.Core.Tasks.StepActionKind.Verification =>
                $"The current step '{currentStep}' requires verification EVIDENCE. Execute the verification now — run the build/tests or inspect the files and report the factual result. Allowed tools for this step: [{tools}]. Do NOT plan, do NOT narrate, do NOT re-enter reasoning — verify.",
            // Reason/UserInput steps deliver TEXT — no workspace tool exists for them, so the
            // directive must demand the text deliverable, never a tool call (demanding one
            // traps the model: the gate rejects every workspace tool, and it stalls into
            // empty/thinking-only output — the observed qwen 3.6 loop).
            Klydis.Core.Tasks.StepActionKind.Reason or Klydis.Core.Tasks.StepActionKind.UserInput =>
                $"The current step '{currentStep}' requires a TEXT deliverable (design direction / requirements / reasoning) — produce it now as text, clearly labeled PROPOSED where you are proposing. No workspace tool is permitted for this step, so do NOT call a tool. Do NOT plan, do NOT narrate — deliver the content.",
            _ =>
                $"Execute exactly ONE tool call that advances the current step '{currentStep}'. Allowed tools for this step: [{tools}] — any tool not listed is REJECTED by the runtime before execution. Do NOT plan, do NOT narrate, do NOT re-enter reasoning — execute."
        };
    }

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

    /// <summary>
    /// The P1.7a action-gate rejection injected as the tool result. Structured and concrete:
    /// the action identity, the machine-searchable error code, the step, the tools that WERE
    /// allowed, and the reason — so the model's next action is a corrected call, not another
    /// guess.
    /// </summary>
    private static string BuildActionGateRejection(string actionId, ToolCallRequest req, Klydis.Core.Tasks.ActionGateVerdict verdict)
    {
        var code = Klydis.Core.Tasks.ActionGate.ErrorCode(verdict.Error!.Value);
        var sb = new StringBuilder();
        sb.AppendLine("[SYSTEM — ACTION REJECTED BY ACTION GATE]");
        sb.AppendLine("Your action was NOT executed and produced no tool result.");
        sb.AppendLine($"  ActionId: {actionId}");
        sb.AppendLine($"  Tool: {req.Name}");
        sb.AppendLine($"  Error: {code}");
        if (!string.IsNullOrWhiteSpace(verdict.CurrentStep))
        {
            sb.AppendLine($"  Current step: {verdict.CurrentStep}");
        }
        if (!string.IsNullOrWhiteSpace(verdict.AllowedToolsSummary))
        {
            sb.AppendLine($"  Allowed tools: [{verdict.AllowedToolsSummary}]");
        }
        sb.AppendLine($"  Reason: {verdict.Reason}");
        sb.AppendLine("Emit ONE corrected action: a tool that exists, is permitted for the current step, has all required arguments, and advances the step. Tool results exist only when a tool actually executes.");
        return sb.ToString();
    }

    private List<ToolCallRequest> ParseToolCalls(string response)
    {
        // P1.6: the registered protocol adapter is now the FIRST parser. When an adapter
        // is active, its ParseOutput is the authoritative action normalization for this
        // model — every dialect (qwen native, antml, JSON envelope, tool_call JSON)
        // becomes canonical actions, which we map back to executable ToolCallRequests.
        // The Qwen adapter delegates to the shared ActionDialectParser, so the adapter
        // path and the legacy fallback below are identical by construction (no drift).
        var adapter = CurrentProtocolAdapter;
        if (adapter != null)
        {
            var canonical = adapter.ParseOutput(response);
            if (canonical.Count > 0)
            {
                var results = new List<ToolCallRequest>(canonical.Count);
                foreach (var action in canonical)
                {
                    switch (action.Type)
                    {
                        case Klydis.Core.Protocol.CanonicalActionType.ToolCall:
                            if (!string.IsNullOrWhiteSpace(action.ToolName))
                            {
                                var callArgs = action.Arguments == null
                                    ? new Dictionary<string, object>()
                                    : new Dictionary<string, object>(action.Arguments);
                                results.Add(new ToolCallRequest(action.ToolName, callArgs));
                            }
                            break;
                        case Klydis.Core.Protocol.CanonicalActionType.CompletionClaim:
                            // completion claims are semantically task_complete calls.
                            var completeArgs = action.Arguments == null
                                ? new Dictionary<string, object>()
                                : new Dictionary<string, object>(action.Arguments);
                            results.Add(new ToolCallRequest("task_complete", completeArgs));
                            break;
                        case Klydis.Core.Protocol.CanonicalActionType.Replan:
                            // replan requests execute the plan tool (action=create). Merge the
                            // canonical args (e.g. envelope items) so the adapter path produces
                            // the identical request the legacy envelope parser did.
                            var planArgs = action.Arguments == null
                                ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                                : new Dictionary<string, object>(action.Arguments);
                            planArgs["action"] = "create";
                            results.Add(new ToolCallRequest("plan", planArgs));
                            break;
                        default:
                            break; // Message / Blocked — plain text, never a tool call
                    }
                }
                return results;
            }
        }

        // Legacy fallback (no registered adapter): the shared tolerant multi-dialect
        // parser. This is the exact pipeline the adapters use, moved into the protocol
        // layer so the two paths can never drift. Deleted entirely once every model
        // family has an adapter (migration completion criterion — CurrentProtocolAdapter).
        return Klydis.Core.Protocol.ActionDialectParser.ParseAll(response, logger);
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
