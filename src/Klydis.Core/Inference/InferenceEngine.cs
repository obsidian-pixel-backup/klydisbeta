using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Klydis.Core.Chat;
using Klydis.Core.Inference.Telemetry;

[assembly: InternalsVisibleTo("Klydis.Core.Tests")]

namespace Klydis.Core.Inference;

/// <summary>
/// Defines a chat template for formatting messages into a prompt string.
/// </summary>
public abstract class ChatTemplate
{
    /// <summary>
    /// Formats a list of messages into a single prompt string suitable for the model architecture.
    /// </summary>
    public abstract string Format(IList<ChatMessage> messages);
}

/// <summary>
/// Core inference engine that uses LLamaSharp to load and run GGUF models in-process.
/// Completely replaces Ollama dependency.
/// </summary>
public sealed class InferenceEngine : IInferenceEngine, IDisposable, IAsyncDisposable
{
    private readonly ILogger<InferenceEngine> _logger;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ModelParams? _modelParams;
    private InteractiveExecutor? _executor;
    private string _lastEvaluatedPrompt = string.Empty;
    private readonly SemaphoreSlim _modelLock = new SemaphoreSlim(1, 1);
    private CancellationTokenSource? _activeGenerationCts;
    private Task? _activeGenerationTask;
    private readonly object _generationCtsLock = new();
    private volatile bool _isDisposed;

    // Session-level latch: set when a generation fails with a decode-level error (e.g. the
    // speculative verification path throwing 'llama_decode failed'). Every such failure burns a
    // full history re-prefill on retry, so once speculation has proven broken for this model it
    // is disabled for the rest of the session (mirrors the existing low-acceptance bypass, but
    // triggers on actual failures instead of statistics).
    private volatile bool _speculationDisabledAfterDecodeFailure;

    private readonly object _contextResetLock = new();

    public SpeculativeEngine SpeculativeEngine { get; } = new();
    public SpeculativeDecodingService? SpeculativeDecodingService { get; set; }
    public bool IsSpeculativeDecodingEnabled { get; set; } = true;

    /// <summary>
    /// Opt-in (default off): constrains sampling to a GBNF grammar from the moment the model
    /// begins a qwen-native tool-call block, so malformed/abandoned calls cannot reach the
    /// regex parser (each failed parse costs a full prompt rebuild + re-prefill + re-inference).
    /// Kept off by default until validated against a real qwen model — see
    /// <see cref="ToolCallConstrainedSamplingPipeline"/> for the safety rails.
    /// </summary>
    public bool EnableToolGrammarConstrainedDecoding { get; set; }
    public int SpeculativeDraftCount { get; set; } = 24;
    private string _selectedDraftModelPath = "auto";
    private Klydis.Core.Hardware.OffloadPlan? _lastOffloadPlan;
    public string SelectedDraftModelPath
    {
        get => _selectedDraftModelPath;
        set => _selectedDraftModelPath = string.IsNullOrWhiteSpace(value) ? "auto" : value;
    }
    public string SpeculativeStatus { get; private set; } = "Speculative decoding initialized.";

    /// <summary>
    /// Gets or sets target KV cache quantization precision. Default is Q4_0.
    /// </summary>
    public KvCacheQuantizationType TargetKvQuantization { get; set; } = KvCacheQuantizationType.Q4_0;

    /// <summary>
    /// Gets current KV cache memory estimate and architecture metrics.
    /// </summary>
    public KvCacheMemoryEstimate? CurrentKvCacheEstimate { get; private set; }

    /// <summary>
    /// Gets the telemetry recorded during the most recent generation.
    /// </summary>
    public InferenceTelemetry? LastTelemetry { get; private set; }

    /// <summary>
    /// Event fired when an inference request completes with telemetry.
    /// </summary>
    public event Action<InferenceTelemetry>? InferenceCompleted;

    /// <summary>
    /// Event fired when a token is generated, providing the token text and current tokens/second rate.
    /// </summary>
    public event Action<string, float>? TokenGenerated;

    /// <summary>
    /// Event fired when a generation begins, carrying the prompt token count so consumers can
    /// account for "tokens in" live (alongside the per-token "tokens out" events) instead of
    /// waiting for completion. Fired only when triggerEvents is true (the chat path), matching
    /// TokenGenerated. PromptTokenCount is populated; GeneratedTokenCount is always 0.
    /// </summary>
    public event Action<InferenceTelemetry>? InferenceStarted;

    /// <summary>
    /// Event fired when a model is loaded or unloaded (isLoaded, modelPath).
    /// </summary>
    public event Action<bool, string?>? ModelStateChanged;

    /// <summary>
    /// Event fired when speculative decoding status changes.
    /// </summary>
    public event Action<string>? SpeculativeStatusChanged;

    /// <summary>
    /// Architecture of the loaded model.
    /// </summary>
    public string Architecture { get; set; } = "llama"; // Default for now

    /// <summary>
    /// True for the Qwen3.x thinking/recurrent model family (qwen35 / qwen35moe / qwen3next).
    /// These are hybrid Gated-DeltaNet + attention architectures with tiny per-layer KV caches,
    /// M-RoPE position requirements, pre-opened &lt;think&gt; templates, and the native
    /// &lt;tool_call&gt;&lt;function=...&gt; tool syntax. EVERY runtime decision that makes them
    /// behave — recurrent overflow strategy, thinking prelude, verbatim assistant storage,
    /// grammar-constrained tool calls, stricter anti-repetition sampling — must key off this
    /// single check. A qwen3next-arch model falling through a qwen35-only gate produced decode
    /// failures, re-prefill loops, and flubbed tool calls (the "all qwen3.6 models are trash"
    /// failure mode: the Qwen3-Next GGUF reports a different architecture string than the
    /// qwen35 family, so it silently missed every qwen35-gated path). Plain dense Qwen3
    /// ("/qwen3") is deliberately NOT matched — it is a standard transformer, not hybrid.
    /// </summary>
    public static bool IsQwenThinkingArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return false;
        string lower = architecture.ToLowerInvariant();
        return lower.StartsWith("qwen35", StringComparison.Ordinal) ||
               lower.StartsWith("qwen3next", StringComparison.Ordinal) ||
               lower.StartsWith("qwen3-next", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the loaded model uses a recurrent/SSM hybrid architecture with M-RoPE
    /// position requirements (qwen35 / qwen35moe / qwen3next, mamba, rwkv, jamba). These
    /// models cannot use partial-prefix KV rewinding (MemorySequenceRemove + fresh executor):
    /// the recurrent memory keeps its old positions while the new input batch starts at 0,
    /// violating the M-RoPE X &lt; Y invariant and making llama_decode fail.
    /// </summary>
    public bool IsRecurrentArchitecture
    {
        get
        {
            string? arch = Architecture?.ToLowerInvariant();
            return IsQwenThinkingArchitecture(arch) || arch is "mamba" or "rwkv" or "jamba";
        }
    }

    /// <summary>
    /// True when the loaded model uses a Mixture-of-Experts (MoE) architecture
    /// (qwen35moe / qwen3.6-Next, mixtral, deepseek-v2/v3, qwen2moe, grok, ...). MoE models are
    /// prone to repetition attractors and tangential drift under stress, so the chat pipeline
    /// applies a stricter anti-repetition sampling profile and runs the degenerate-loop
    /// self-correction system for them (see <see cref="GenerationLoopDetector"/>).
    /// </summary>
    public bool IsMixtureOfExperts { get; private set; }

    /// <summary>
    /// Set when the most recent chat-path generation was stopped because the degenerate-loop
    /// detector fired. ChatEngine reads this after the token stream ends, discards the looped
    /// tail, injects a self-correction instruction and regenerates. Reset at the start of every
    /// generation; null when the last generation was clean.
    /// </summary>
    public GenerationLoopInfo? LastGenerationLoopInfo { get; private set; }

    /// <summary>
    /// True when the most recent chat-path generation stopped because it exhausted its
    /// MaxTokens output budget (the stream was cut at the cap, not by a stop token, user
    /// cancellation, or the degenerate-loop detector). ChatEngine reads this after the token
    /// stream ends to decide whether an auto-continuation is warranted — a response that
    /// ends cleanly at the cap (even with a final period) is still truncated.
    /// </summary>
    public bool LastGenerationHitMaxTokens { get; private set; }

    /// <summary>
    /// True when the most recent chat-path generation was cut short MID-STREAM by a native
    /// decode failure / context overflow AFTER tokens were already emitted, and the stream was
    /// completed cleanly with the partial output (neither a MaxTokens cap hit, a stop token,
    /// user cancellation, nor a detected degenerate loop). This is the "llama_decode failed
    /// (InvalidInputBatch) / ContextOverflowException" path — on recurrent architectures it
    /// fires most often when M-RoPE positioning or the KV cache state breaks mid-generation.
    /// ChatEngine reads this after the token stream ends: the response is truncated and must
    /// be resumed via auto-continuation, exactly like a MaxTokens cap hit. Without it, a cut
    /// that happens to end at a sentence boundary is indistinguishable from a natural stop and
    /// the turn silently terminates at whatever token count was reached (~1k in practice).
    /// </summary>
    public bool LastGenerationWasCutShort { get; private set; }

    /// <summary>
    /// True when the previous generation was aborted BEFORE decoding because the prompt already
    /// filled the context window (recurrent architectures complete empty instead of overflowing
    /// the native cache). ChatEngine must distinguish this from a genuinely degenerate model
    /// output: injecting an empty-response correction here would GROW the prompt and make the
    /// failure worse, when the only working remedies are context compression or a smaller prompt.
    /// </summary>
    public bool LastGenerationPromptFilledWindow { get; private set; }

    /// <summary>
    /// True when the most recent generation ended WITHOUT producing output because it was
    /// cancelled (model switch/unload, user stop, or session teardown) rather than because the
    /// model degenerated. Read after the token stream ends. ChatEngine must NOT treat a
    /// cancelled-before-decode stream as a genuine empty response: injecting self-corrections
    /// against an in-flight model load just rebuilds the context and re-triggers the cancel —
    /// the observed "empty response self-correcting" banner storm during model alternation.
    /// </summary>
    public bool LastGenerationWasCancelled { get; private set; }

    /// <summary>
    /// How the last generation treated the KV cache relative to the previous generation's
    /// prompt: ExactReuse (new prompt is a strict prefix-extension of the evaluated prompt),
    /// PartialReuse (diverged — sequence rewound to the common prefix), or Reset* (no usable
    /// prefix — context rebuilt from scratch). Exposed so the orchestration layer can log the
    /// turn-boundary decision: a "new user turn accidentally running on the previous turn's KV"
    /// failure is visible per generation instead of hiding in debug logs.
    /// </summary>
    public string LastGenerationContextDecision { get; private set; } = "None";

    /// <summary>
    /// Char length of the prompt prefix reused by the last generation (0 when the context was
    /// reset).
    /// </summary>
    public int LastGenerationPrefixLength { get; private set; }

    /// <summary>
    /// Raw GGUF chat template string if present.
    /// </summary>
    public string? RawChatTemplate { get; private set; }

    /// <summary>
    /// Fine-tune name if present.
    /// </summary>
    public string? FineTuneName { get; private set; }

    /// <summary>
    /// Gets a value indicating whether a model is currently loaded.
    /// </summary>
    public bool IsModelLoaded => _weights != null && _context != null;

    /// <summary>
    /// User-defined explicit context limit preference (0 = Auto smart hardware allocation up to model max, or custom limit up to 1M tokens).
    /// </summary>
    public uint UserContextLimit { get; set; } = 0;

    /// <summary>
    /// User-defined explicit logical batch size preference (0 = Auto hardware optimized).
    /// </summary>
    public uint UserBatchSize { get; set; } = 0;

    /// <summary>
    /// User-defined explicit micro-batch size (UBatchSize) preference (0 = Auto hardware optimized).
    /// </summary>
    public uint UserUBatchSize { get; set; } = 0;

    /// <summary>
    /// Gets the loaded context size budget.
    /// </summary>
    public uint ContextSize => _modelParams?.ContextSize ?? 32768;

    /// <summary>
    /// Gets the path of the currently loaded model.
    /// </summary>
    public string? CurrentModelPath { get; private set; }

    private INativeResourceDisposer? _nativeResourceDisposer;

    /// <summary>
    /// True when the current load pinned the process to P-cores (CPU-only execution); the
    /// affinity is restored in <see cref="UnloadModelInternal"/>.
    /// </summary>
    private bool _pCoreAffinityApplied;

    /// <summary>
    /// Gets or sets the native resource disposer for offloading VRAM/handle cleanup off the UI thread.
    /// </summary>
    public INativeResourceDisposer? NativeResourceDisposer
    {
        get => _nativeResourceDisposer;
        set
        {
            _nativeResourceDisposer = value;
            if (SpeculativeEngine != null)
            {
                SpeculativeEngine.NativeResourceDisposer = value;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceEngine"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="nativeResourceDisposer">Optional background native resource disposer.</param>
    public InferenceEngine(ILogger<InferenceEngine> logger, INativeResourceDisposer? nativeResourceDisposer = null)
    {
        NativeEngineManager.EnsureNativeLibraryConfigured();
        // Newer llama.cpp releases (b9181+) require ggml backend plugins to be loaded explicitly.
        NativeEngineManager.LoadNativeBackends();
        _logger = logger;
        NativeResourceDisposer = nativeResourceDisposer;
    }

    private void SafeOffloadDisposal(params IDisposable?[] resources)
    {
        // Native CUDA handle release (LLamaContext / LLamaWeights) can take hundreds of ms.
        // Never run it on the calling thread: route it through the background disposer when
        // registered, otherwise fall back to a fire-and-forget threadpool dispose. Callers
        // that need the resources gone before proceeding (e.g. LoadModelAsync) drain the
        // disposer explicitly via DrainAsync before allocating new native state.
        if (_nativeResourceDisposer != null)
        {
            _nativeResourceDisposer.EnqueueForDisposal(resources);
            return;
        }

        var items = resources.Where(r => r != null).Select(r => r!).ToArray();
        if (items.Length == 0) return;
        Task.Run(() =>
        {
            foreach (var r in items)
            {
                try { r.Dispose(); } catch { }
            }
        });
    }

    /// <summary>
    /// Resolves and attaches a speculative draft model for the current target model.
    /// </summary>
    public async Task AttachSpeculativeDraftAsync(string targetModelPath)
    {
        if (SpeculativeDecodingService == null) return;

        await _modelLock.WaitAsync();
        try
        {
            var res = await SpeculativeDecodingService.ResolveDraftModelAsync(targetModelPath, IsSpeculativeDecodingEnabled, SelectedDraftModelPath);
            SpeculativeStatus = res.StatusMessage;
            SpeculativeStatusChanged?.Invoke(SpeculativeStatus);

            if (res.IsEnabled && !string.IsNullOrEmpty(res.DraftModelPath) && res.DraftOffloadPlan != null)
            {
                _logger.LogInformation("Attaching speculative draft model: {DraftPath}", res.DraftModelPath);
                await SpeculativeEngine.LoadDraftModelAsync(res.DraftModelPath, res.DraftOffloadPlan);
                SpeculativeEngine.DraftCandidateCount = SpeculativeDraftCount;
                SpeculativeEngine.IsNGramFallbackEnabled = false;
            }
            else
            {
                await SpeculativeEngine.UnloadAsync();
                // When the service resolved "enabled" without a draft model path, it means the
                // zero-VRAM N-gram prompt-lookup fallback is active. Surface it as an enabled
                // speculation mode so GenerateAsync actually routes through the speculative path.
                SpeculativeEngine.IsNGramFallbackEnabled = res.IsEnabled;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to attach speculative draft model.");
            SpeculativeStatus = $"Speculative decoding unavailable: {ex.Message}";
            SpeculativeStatusChanged?.Invoke(SpeculativeStatus);
            await SpeculativeEngine.UnloadAsync();
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Loads a GGUF model asynchronously with the specified hardware offloading plan.
    /// </summary>
    public Task LoadModelAsync(string modelPath, Klydis.Core.Hardware.OffloadPlan offloadPlan)
    {
        return Task.Run(async () =>
        {
            await CancelActiveGenerationAsync();
            await _modelLock.WaitAsync();
            bool success = false;
            try
            {
                _logger.LogInformation("Loading model from {ModelPath} with {GpuLayers} GPU layers.", modelPath, offloadPlan.GpuLayers);
                _lastOffloadPlan = offloadPlan;

                await SpeculativeEngine.UnloadAsync();
                UnloadModelInternal();
                if (NativeResourceDisposer != null)
                {
                    await NativeResourceDisposer.DrainAsync();
                }

                // Only lock process CPU affinity mask for CPU-only execution (0 GPU layers)
                if (offloadPlan.GpuLayers == 0)
                {
                    Hardware.CpuAffinityHelper.ApplyPCoreAffinityToProcess();
                    _pCoreAffinityApplied = true;
                }

                var metadata = Models.GgufMetadataReader.ParseCached(modelPath);
                if (metadata != null)
                {
                    CurrentKvCacheEstimate = KvCacheCalculator.Calculate(metadata, offloadPlan.RecommendedContextSize, TargetKvQuantization);
                    _logger.LogInformation("KV Cache VRAM estimate ({Arch}): {Mb} MB ({Gb} GB), {Bpt} bytes/token.",
                        CurrentKvCacheEstimate.AttentionArchitecture,
                        CurrentKvCacheEstimate.TotalVramMegabytes,
                        CurrentKvCacheEstimate.TotalVramGigabytes,
                        CurrentKvCacheEstimate.BytesPerToken);
                }

                // Target KV cache precision set based on TargetKvQuantization configuration
                var kvType = TargetKvQuantization switch
                {
                    KvCacheQuantizationType.F16 => LLama.Native.GGMLType.GGML_TYPE_F16,
                    KvCacheQuantizationType.Q8_0 => LLama.Native.GGMLType.GGML_TYPE_Q8_0,
                    KvCacheQuantizationType.Q4_1 => LLama.Native.GGMLType.GGML_TYPE_Q4_1,
                    _ => LLama.Native.GGMLType.GGML_TYPE_Q4_0
                };

                string archLower = (metadata?.Architecture ?? "").ToLowerInvariant();
                // Pure recurrent/SSM architectures (mamba, rwkv, jamba) have NO attention layers:
                // flash attention is meaningless there and llama.cpp force-disables it.
                bool isPureSsm = archLower is "mamba" or "rwkv" or "jamba";
                // Qwen3.5 / Qwen3-Next (qwen35/qwen3next/qwen35moe) are HYBRID: most layers are
                // Gated DeltaNet (recurrent, constant-size state) but ~1/4 of layers are standard
                // attention layers that DO support flash attention. Modern llama.cpp runs these
                // with flash_attn=enabled (see ggml-org/llama.cpp issues #22817 / #23321); only
                // the pure-SSM family and Grok force FA off. Enabling FA here is the single
                // biggest decode-speedup lever at long context — without it, the attention layers
                // scan the whole KV cache per token and 64K+ context craters to ~20 tps.
                bool isHybridAttentionArch = archLower is "qwen35" or "qwen3next" or "qwen35moe";
                bool isHybridSsm = isPureSsm || isHybridAttentionArch; // KV-shift/context guards still apply to both

                int totalModelLayers = (metadata != null && metadata.BlockCount.HasValue && metadata.BlockCount.Value > 0) ? (int)metadata.BlockCount.Value : 32;
                // Set GpuLayerCount to 999 for full GPU offload when offloadPlan targets full GPU offload (GpuLayers >= totalModelLayers or FullGpu strategy) to offload all transformer blocks + non-layer tensors to CUDA0
                int targetGpuLayers = (offloadPlan.GpuLayers < 0 || offloadPlan.GpuLayers >= totalModelLayers || offloadPlan.StrategyUsed == Hardware.OffloadStrategyType.FullGpu) ? 999 : offloadPlan.GpuLayers;

                // Enable FlashAttention on GPU for all architectures that have real attention layers
                // (dense transformers AND hybrid Qwen). Only pure-SSM archs (mamba/rwkv/jamba) and
                // CPU execution keep it off.
                bool useFlashAttention = !isPureSsm && targetGpuLayers > 0;

                // Context size scales dynamically with user limit or hardware-calculated offload plan
                // context. Hybrid/recurrent archs have tiny KV caches (only attention layers grow),
                // so they are NOT limited by the dense-transformer 128K ceiling — allow up to the
                // model's native context (e.g. Qwen3.5/3.6 trains at 262144 tokens).
                uint autoContextCeiling = isHybridSsm ? 262144u : 131072u;
                uint targetContextSize = UserContextLimit > 0
                    ? UserContextLimit
                    : (uint)Math.Clamp(offloadPlan.RecommendedContextSize, 2048, autoContextCeiling);

                if (isHybridSsm)
                {
                    _logger.LogInformation("Hybrid SSM architecture '{Arch}' detected. Context configured to {MaxCtx} tokens (native ceiling {Ceiling}).", archLower, targetContextSize, autoContextCeiling);
                }

                // Load-time window diagnostics: the effective context must always be visible in
                // the logs. A silent 4K window here is the observed root cause of "generation
                // terminates after ~2k tokens" (recurrent cap = window − prompt − 512), so log
                // every input that produced it — UserContextLimit, the offload plan's
                // recommendation, and the architecture.
                _logger.LogInformation(
                    "Context sizing: UserContextLimit={UserLimit}, plan.RecommendedContextSize={PlanCtx}, architecture={Arch}, hybridSsm={Hybrid} -> effectiveContextSize={Target} (ceiling {Ceiling}).",
                    UserContextLimit, offloadPlan.RecommendedContextSize, archLower, isHybridSsm, targetContextSize, autoContextCeiling);

                uint safeBatchSize = UserBatchSize > 0
                    ? UserBatchSize
                    : (isHybridSsm ? 256u : (uint)Math.Max(2048, offloadPlan.RecommendedBatchSize));
                uint safeUBatchSize = UserUBatchSize > 0
                    ? UserUBatchSize
                    : (isHybridSsm ? 256u : 512u); // Micro-batch size 512u for peak Tensor Core prefill throughput

                // For 100% GPU offload, reduce CPU worker threads to 2 to eliminate llama.cpp spin-wait loops that pin host CPU to 100%.
                int optimalThreads = (targetGpuLayers >= totalModelLayers || targetGpuLayers == 999) ? 2 : Math.Clamp(Environment.ProcessorCount, 4, 16);

                // Configure model parameters for maximum GPU throughput
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = targetContextSize,
                    GpuLayerCount = targetGpuLayers, // Offload 100% of layers to GPU (GpuLayerCount = 999)
                    BatchSize = safeBatchSize,
                    UBatchSize = safeUBatchSize, // Physical micro-batch size optimized for prefill & generation
                    FlashAttention = useFlashAttention,
                    // Use Unspecified pooling to let the model define its own pooling_type (-1/none for generative models)
                    // instead of LLamaSharp's default Mean (0) which triggers "model default pooling_type is [-1], but [0] was specified"
                    PoolingType = LLama.Native.LLamaPoolingType.Unspecified,
                    Threads = optimalThreads,
                    BatchThreads = optimalThreads,
                    // Enable Memory map to eliminate double-buffering in System RAM
                    UseMemorymap = true,
                    UseMemoryLock = false
                };
                
                if (!isHybridSsm)
                {
                    parameters.TypeK = kvType;
                    parameters.TypeV = kvType;
                }

                var compat = GgufCompatibilityAdapter.Evaluate(modelPath);
                if (!compat.IsSupported)
                {
                    // Pre-flight structural validation (truncated/corrupt GGUF, unreadable header,
                    // missing file) failed — surface the actionable message instead of letting the
                    // native loader fail with a confusing "architecture not supported" error.
                    string preflightMessage = compat.WarningMessage ?? "Model file failed pre-flight validation.";
                    _logger.LogError("GGUF pre-flight validation failed for {ModelPath}: {Message}", modelPath, preflightMessage);
                    throw new InvalidOperationException($"Failed to load model '{Path.GetFileName(modelPath)}': {preflightMessage}");
                }
                if (compat.WarningMessage != null)
                {
                    _logger.LogWarning("GGUF Pre-flight Notice: {Message}", compat.WarningMessage);
                }

                _modelParams = parameters;
                try
                {
                    try
                    {
                        _weights = LLamaWeights.LoadFromFile(parameters);
                    }
                    catch (Exception loadEx)
                    {
                        var overrideDir = NativeEngineManager.CustomNativeDirectory;
                        string msg = loadEx.Message ?? string.Empty;

                        // Check if this is DEFINITELY NOT an architecture issue (e.g. file not found, permissions).
                        // LLamaSharp wraps native errors with generic messages like "Failed to load model 'path'"
                        // so we CANNOT rely on checking for "missing tensor" / "blk." in the exception message —
                        // those details only appear in the native log callback, not the exception.
                        bool isDefinitelyNotArchError =
                            msg.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("file not found", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                            msg.Contains("permission", StringComparison.OrdinalIgnoreCase);

                        if (isDefinitelyNotArchError)
                        {
                            throw new InvalidOperationException(
                                $"Failed to load model '{Path.GetFileName(modelPath)}': {msg}",
                                loadEx);
                        }

                        string nativeLogTail = ReadNativeLogTail();
                        _logger.LogWarning(loadEx, "Native load failed for '{ModelFile}' (arch: {Arch}). Native log tail: {Tail}",
                            Path.GetFileName(modelPath), compat.Architecture, nativeLogTail);

                        // Distinguish a truncated/corrupt GGUF (missing tensors mid-file, e.g. an
                        // interrupted download) from a genuinely unsupported architecture. The native
                        // log prints "missing tensor 'blk.N...'" for the former; blaming the arch is
                        // misleading and sends users re-downloading their native engine for nothing.
                        if (nativeLogTail.Contains("missing tensor", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Model file '{Path.GetFileName(modelPath)}' appears to be corrupt or truncated " +
                                $"(missing tensors mid-file). This usually means an interrupted or incomplete download. " +
                                $"Please re-download the model. Native error: {msg}", loadEx);
                        }

                        // The native loader rejects models whose tokenizer features the bundled
                        // backend doesn't know ("unknown pre-tokenizer type: 'minicpm5'", etc.).
                        // This is a vocabulary/backend-version limitation, NOT an architecture
                        // problem — surface it precisely so the user knows the model needs a
                        // newer native engine instead of seeing a misleading "architecture not
                        // supported" message.
                        string? vocabDiagnosis = DiagnoseVocabIncompatibility(nativeLogTail, Path.GetFileName(modelPath));
                        if (vocabDiagnosis != null)
                        {
                            // The installed native engine is older than the model's tokenizer.
                            // Auto-update to the latest llama.cpp release and restart to apply it;
                            // only surface the error if no newer release is available (e.g. offline).
                            try
                            {
                                // Properly awaited (LoadModelAsync runs on a threadpool task): the old
                                // GetAwaiter().GetResult() blocked the calling thread for the duration
                                // of a potentially minutes-long, hundreds-of-MB download.
                                bool updated = await NativeEngineManager.TryAutoUpdateNativeEngineAsync(_logger, forceCheck: true)
                                    .ConfigureAwait(false);
                                if (updated)
                                {
                                    _logger.LogInformation("Auto-updated native engine to support '{ModelFile}'. Restarting to apply.", Path.GetFileName(modelPath));
                                    NativeEngineManager.RestartApplication(_logger);
                                }
                            }
                            catch (Exception updateEx)
                            {
                                _logger.LogWarning(updateEx, "Native engine auto-update failed while diagnosing '{ModelFile}'.", Path.GetFileName(modelPath));
                            }

                            throw new InvalidOperationException(
                                $"{vocabDiagnosis} No newer native engine release could be downloaded right now (check your internet connection and restart Klydis).",
                                loadEx);
                        }

                        throw new InvalidOperationException(
                            $"Failed to load model '{Path.GetFileName(modelPath)}' natively. " +
                            $"Architecture '{compat.Architecture}' is not supported by the current native engine. " +
                            $"Native error: {msg}", loadEx);
                    }

                    if (_weights == null)
                    {
                        throw new InvalidOperationException($"Failed to load model weights from '{modelPath}'.");
                    }

                    _context = _weights.CreateContext(parameters);
                    if (_context == null || _context.NativeHandle == null || _context.NativeHandle.IsInvalid || _context.NativeHandle.IsClosed)
                    {
                        throw new InvalidOperationException($"Failed to create context for model '{modelPath}'. Native context handle is invalid (insufficient GPU VRAM for the KV cache).");
                    }

                    _executor = new InteractiveExecutor(_context);
                }
                catch (Exception gpuEx) when (offloadPlan.GpuLayers != 0 && !compat.RequiresUpdatedNativeBackend && !IsArchitectureIncompatibleError(gpuEx))
                {
                    // Safety net: if flash attention was requested (hybrid Qwen/dense on GPU) and the
                    // native build rejects it, retry ONCE on GPU with FA disabled before falling to
                    // CPU. This keeps the speedup when FA works while never hard-failing the load.
                    if (useFlashAttention)
                    {
                        _logger.LogWarning(gpuEx, "GPU context creation failed with flash attention for {ModelPath}. Retrying without flash attention.", modelPath);
                        UnloadModelInternal();
                        parameters.FlashAttention = false;
                        useFlashAttention = false;

                        try
                        {
                            _weights = LLamaWeights.LoadFromFile(parameters);
                            if (_weights == null)
                            {
                                throw new InvalidOperationException($"Retry (no-FA) failed to load model weights from '{modelPath}'.");
                            }

                            _context = _weights.CreateContext(parameters);
                            if (_context == null || _context.NativeHandle == null || _context.NativeHandle.IsInvalid || _context.NativeHandle.IsClosed)
                            {
                                throw new InvalidOperationException($"Retry (no-FA) failed to create context for '{modelPath}'.");
                            }

                            _executor = new InteractiveExecutor(_context);
                        }
                        catch (Exception noFaEx)
                        {
                            _logger.LogWarning(noFaEx, "GPU retry without flash attention also failed for {ModelPath}. Falling back to CPU execution.", modelPath);
                            UnloadModelInternal();
                            parameters.GpuLayerCount = 0;
                            parameters.ContextSize = (uint)Math.Max(2048, offloadPlan.RecommendedContextSize);

                            _weights = LLamaWeights.LoadFromFile(parameters);
                            if (_weights == null)
                            {
                                throw new InvalidOperationException($"CPU fallback failed to load model weights from '{modelPath}'.");
                            }

                            _context = _weights.CreateContext(parameters);
                            if (_context == null || _context.NativeHandle == null || _context.NativeHandle.IsInvalid || _context.NativeHandle.IsClosed)
                            {
                                throw new InvalidOperationException($"CPU fallback failed to create context for '{modelPath}': {gpuEx.Message}");
                            }

                            _executor = new InteractiveExecutor(_context);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(gpuEx, "GPU model/context creation failed for {ModelPath}. Falling back to CPU execution.", modelPath);
                        UnloadModelInternal();

                        // Fallback to CPU-only execution with conservative context
                        parameters.GpuLayerCount = 0;
                        parameters.ContextSize = (uint)Math.Max(2048, offloadPlan.RecommendedContextSize);

                        _weights = LLamaWeights.LoadFromFile(parameters);
                        if (_weights == null)
                        {
                            throw new InvalidOperationException($"CPU fallback failed to load model weights from '{modelPath}'.");
                        }

                        _context = _weights.CreateContext(parameters);
                        if (_context == null || _context.NativeHandle == null || _context.NativeHandle.IsInvalid || _context.NativeHandle.IsClosed)
                        {
                            throw new InvalidOperationException($"CPU fallback failed to create context for '{modelPath}': {gpuEx.Message}");
                        }

                        _executor = new InteractiveExecutor(_context);
                    }
                }
                
                _lastEvaluatedPrompt = string.Empty;

                CurrentModelPath = modelPath;
                Architecture = !string.IsNullOrWhiteSpace(metadata?.Architecture)
                    ? metadata.Architecture
                    : System.IO.Path.GetFileNameWithoutExtension(modelPath);
                RawChatTemplate = metadata?.RawChatTemplate;
                FineTuneName = metadata?.FineTuneName;
                IsMixtureOfExperts = DetectMixtureOfExperts(Architecture);
                _logger.LogInformation("Model loaded successfully with architecture '{Architecture}'{MoeSuffix}.", Architecture, IsMixtureOfExperts ? " (Mixture-of-Experts: applying MoE stability sampling + loop self-correction)" : "");
                success = true;

                if (IsSpeculativeDecodingEnabled && !string.IsNullOrEmpty(CurrentModelPath))
                {
                    _ = Task.Run(async () => await AttachSpeculativeDraftAsync(CurrentModelPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load LLama model or create context from {ModelPath}.", modelPath);
                UnloadModelInternal();
                ModelStateChanged?.Invoke(false, null);
                throw;
            }
            finally
            {
                _modelLock.Release();
            }

            if (success)
            {
                ModelStateChanged?.Invoke(true, modelPath);
            }
        });
    }

    /// <summary>
    /// True for architecture-related native load errors.
    /// Checks both the exception message AND the native log tail, because LLamaSharp
    /// wraps native errors with generic messages — the real details ("missing tensor",
    /// "unknown model") only appear in the native log callback.
    /// </summary>
    private static bool IsArchitectureIncompatibleError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;

        // Check exception message first
        if (msg.Contains("missing tensor", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("tensor layout is incompatible", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unsupported model", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not supported by the current native", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Also check native log tail for architecture/tokenizer error patterns
        string logTail = ReadNativeLogTail();
        return logTail.Contains("missing tensor", StringComparison.OrdinalIgnoreCase) ||
               logTail.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
               logTail.Contains("unsupported model", StringComparison.OrdinalIgnoreCase) ||
               logTail.Contains("unknown pre-tokenizer", StringComparison.OrdinalIgnoreCase) ||
               logTail.Contains("unknown tokenizer", StringComparison.OrdinalIgnoreCase) ||
               logTail.Contains("error loading model vocabulary", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Inspects the native log tail for tokenizer/vocabulary incompatibility errors and returns
    /// an actionable diagnostic message, or null when the failure is not vocab-related.
    /// Recognized patterns (from llama.cpp's vocab loader):
    ///   - "unknown pre-tokenizer type: '&lt;type&gt;'" — the model's tokenizer.ggml.pre is newer
    ///     than what the bundled native backend knows.
    ///   - "error loading model vocabulary" / "unknown tokenizer" — other vocab-level failures.
    /// </summary>
    private static string? DiagnoseVocabIncompatibility(string nativeLogTail, string fileName)
    {
        if (string.IsNullOrWhiteSpace(nativeLogTail))
        {
            return null;
        }

        // Exact form emitted by llama.cpp: unknown pre-tokenizer type: 'minicpm5'
        var preTokenizerMatch = Regex.Match(nativeLogTail, @"unknown pre-tokenizer type:\s*'([^']+)'", RegexOptions.IgnoreCase);
        if (preTokenizerMatch.Success)
        {
            string preType = preTokenizerMatch.Groups[1].Value.Trim();
            return $"Failed to load model '{fileName}': it declares tokenizer pre-type '{preType}', " +
                   $"which the bundled native engine ({GgufCompatibilityAdapter.BundledNativeBackendLabel}) does not support. " +
                   $"This model needs a newer llama.cpp native backend. " +
                   $"Fix: place an updated llama.dll in %USERPROFILE%\\.klydis\\native\\ and restart Klydis (or use a different model/quantization).";
        }

        if (nativeLogTail.Contains("unknown tokenizer", StringComparison.OrdinalIgnoreCase) ||
            nativeLogTail.Contains("error loading model vocabulary", StringComparison.OrdinalIgnoreCase))
        {
            return $"Failed to load model '{fileName}': its tokenizer/vocabulary is not supported by the bundled native engine " +
                   $"({GgufCompatibilityAdapter.BundledNativeBackendLabel}). " +
                   $"Fix: place an updated llama.dll in %USERPROFILE%\\.klydis\\native\\ and restart Klydis (or use a different model/quantization).";
        }

        return null;
    }

    /// <summary>
    /// Detects whether an architecture string denotes a Mixture-of-Experts model. These models
    /// (qwen35moe / Qwen3.6-Next, mixtral, deepseek-v2/v3, qwen2moe, grok, ...) are prone to
    /// repetition attractors and tangential drift, so they get the stricter MoE sampling profile
    /// and the degenerate-loop self-correction system.
    /// </summary>
    internal static bool DetectMixtureOfExperts(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture)) return false;
        string lower = architecture.ToLowerInvariant();
        return lower.Contains("moe", StringComparison.Ordinal) ||
               lower.Contains("mixtral", StringComparison.Ordinal) ||
               lower.Contains("deepseekv2", StringComparison.Ordinal) ||
               lower.Contains("deepseekv3", StringComparison.Ordinal) ||
               lower.Contains("grok", StringComparison.Ordinal) ||
               // qwen3next (Qwen3-Next, e.g. qwen3.6-14B-A3B): hybrid sparse model, fragile
               // under stress — it gets the same MoE stabilizers (compact prompt, stricter
               // anti-repetition sampling, loop self-correction) as the qwen35moe family.
               lower.StartsWith("qwen3next", StringComparison.Ordinal) ||
               lower.StartsWith("qwen3-next", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the last ~4KB of the native log file to check for error patterns
    /// that appear in the log callback but not in exception messages.
    /// </summary>
    private static string ReadNativeLogTail()
    {
        // Rotating log in %LOCALAPPDATA%\Klydis\logs (see KlydisLog).
        return Klydis.Core.Diagnostics.KlydisLog.ReadNativeLogTail();
    }

    /// <summary>
    /// Generates tokens asynchronously based on the provided prompt string.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateAsync(
        string prompt, 
        InferenceParams inferenceParams, 
        bool triggerEvents = true,
        bool isIsolated = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CancellationTokenSource linkedCts;
        lock (_generationCtsLock)
        {
            try
            {
                _activeGenerationCts?.Cancel();
                _activeGenerationCts?.Dispose();
            }
            catch (ObjectDisposedException) { }

            _activeGenerationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts = _activeGenerationCts;
        }
        CancellationToken generationToken = linkedCts.Token;

        Task? generationTask = null;
        try
        {
            await _modelLock.WaitAsync(ct);
        }
        catch
        {
            lock (_generationCtsLock)
            {
                if (_activeGenerationCts == linkedCts)
                {
                    _activeGenerationCts = null;
                }
            }
            try { linkedCts.Dispose(); } catch (ObjectDisposedException) { }
            throw;
        }
        try
        {
            if (!IsModelLoaded || _executor == null || _context == null)
                throw new InvalidOperationException("Model is not loaded.");

            // Safety fallback: Ensure SamplingPipeline is initialized to prevent NullReferenceExceptions during inference
            if (inferenceParams.SamplingPipeline == null)
            {
                inferenceParams.SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline();
            }

            // Recurrent/M-RoPE architectures (qwen35, mamba, rwkv, jamba) cannot use native KV
            // cache shifting: TruncateAndReprefill performs llama_kv_cache_seq_rm, which the
            // recurrent memory module ignores, so the cache stays at position X while a fresh
            // batch starts at Y=0 -> M-RoPE requires X < Y -> llama_decode fails (ret = -1) and
            // every retry re-prefills the whole history. That reset/re-prefill loop is what
            // craters throughput from 60+ tps to ~20 tps once a long session fills the window.
            // For these models, cap generation so the window can never fill: prompt + output
            // must fit inside ContextSize minus a safety margin. The app-level context
            // compression (ChatEngine) keeps the prompt bounded; this cap protects the
            // generation side and turns "fill the window, corrupt the cache, retry" into a
            // clean, bounded response.
            int promptTokenCount = 0;
            try { promptTokenCount = GetTokenCount(prompt); }
            catch { promptTokenCount = Math.Max(1, prompt.Length / 4); }

            // Announce the generation start so consumers can count "tokens in" live. Without this,
            // "in" only moved when a generation completed, so on long-horizon sessions with
            // frequent mid-stream failures the status bar showed "out" climbing to millions while
            // "in" stayed near zero (the observed in/out swap). Firing at start means every
            // generation contributes its prompt tokens even if it later fails.
            //
            // Compute the speculation flag up front so the start event reports the mode the
            // generation will actually use (previously it always read false, so the UI showed
            // speculative runs as plain inference until completion).
            bool speculationWillBeActive = IsSpeculativeDecodingEnabled &&
                                           !_speculationDisabledAfterDecodeFailure &&
                                           (SpeculativeEngine.IsLoaded || SpeculativeEngine.IsNGramFallbackEnabled);
            if (triggerEvents && InferenceStarted != null)
            {
                InferenceStarted?.Invoke(new InferenceTelemetry(
                    RequestId: Guid.NewGuid().ToString("N"),
                    TargetModelPath: CurrentModelPath ?? "Unknown",
                    DraftModelPath: SpeculativeEngine.LoadedDraftPath,
                    IsSpeculativeEnabled: speculationWillBeActive,
                    PromptLengthChars: prompt.Length,
                    PromptTokenCount: promptTokenCount,
                    GeneratedTokenCount: 0,
                    TimeToFirstTokenMs: 0,
                    GenerationDurationMs: 0,
                    TotalElapsedMs: 0,
                    GenerationTokensPerSecond: 0,
                    EndToEndTokensPerSecond: 0,
                    SpeculativeMetrics: null,
                    IsIsolated: isIsolated));
            }

            if (IsRecurrentArchitecture)
            {
                int window = (int)ContextSize;
                const int RecurrentSafetyMargin = 512;
                int maxGenerationTokens = window - promptTokenCount - RecurrentSafetyMargin;

                if (maxGenerationTokens < 1)
                {
                    _logger.LogWarning("Prompt ({PromptTokens} tokens) already fills the recurrent context window ({Window}). Completing this generation empty; context compression must reduce the prompt.",
                        promptTokenCount, window);
                    // Expose the cause to the caller (ChatEngine): this is NOT a degenerate model
                    // output — the prompt itself cannot fit. Without this flag ChatEngine's
                    // empty-response self-correction injects a correction message (growing the
                    // prompt) and retries, which keeps failing identically — the observed
                    // "Model produced an empty response — self-correcting…" banner loop.
                    LastGenerationPromptFilledWindow = true;

                    var emptyTelemetry = new InferenceTelemetry(
                        RequestId: Guid.NewGuid().ToString("N"),
                        TargetModelPath: CurrentModelPath ?? "Unknown",
                        DraftModelPath: SpeculativeEngine.LoadedDraftPath,
                        IsSpeculativeEnabled: false,
                        PromptLengthChars: prompt.Length,
                        PromptTokenCount: promptTokenCount,
                        GeneratedTokenCount: 0,
                        TimeToFirstTokenMs: 0,
                        GenerationDurationMs: 0,
                        TotalElapsedMs: 0,
                        GenerationTokensPerSecond: 0,
                        EndToEndTokensPerSecond: 0,
                        SpeculativeMetrics: null,
                        IsIsolated: isIsolated);
                    LastTelemetry = emptyTelemetry;
                    InferenceCompleted?.Invoke(emptyTelemetry);
                    yield break;
                }

                if (inferenceParams.MaxTokens < 0 || inferenceParams.MaxTokens > maxGenerationTokens)
                {
                    _logger.LogDebug("Capping generation for recurrent architecture to {MaxGen} tokens (window {Window}, prompt {PromptTokens}).",
                        maxGenerationTokens, window, promptTokenCount);
                    inferenceParams.MaxTokens = maxGenerationTokens;
                }
            }

            _logger.LogDebug("Starting token generation.");

            // Reset the per-generation flags so stale state from a previous generation can
            // never leak into this one. LastGenerationLoopInfo is set (chat path only) when
            // GenerationLoopDetector fires; LastGenerationHitMaxTokens is set when the output
            // budget is exhausted; LastGenerationPromptFilledWindow is set when the recurrent
            // prompt fills the window and generation completes empty; LastGenerationWasCancelled
            // is set when the stream ended early because the generation was cancelled.
            LastGenerationLoopInfo = null;
            LastGenerationHitMaxTokens = false;
            LastGenerationPromptFilledWindow = false;
            LastGenerationWasCancelled = false;
            LastGenerationWasCutShort = false;
            LastGenerationContextDecision = "Pending";
            LastGenerationPrefixLength = 0;

            Channel<Action>? eventChannel = null;
            Task? eventDispatcherTask = null;

            // The dispatcher only exists to serve TokenGenerated subscribers; without any, the
            // channel + Task.Run per generation is pure overhead. Subscribing mid-generation is
            // not a supported pattern, so the snapshot taken here is authoritative.
            bool hasTokenSubscribers = triggerEvents && TokenGenerated != null;

            if (hasTokenSubscribers)
            {
                eventChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

                var reader = eventChannel.Reader;
                eventDispatcherTask = Task.Run(async () =>
                {
                    await foreach (var action in reader.ReadAllAsync())
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Error executing TokenGenerated event callback.");
                        }
                    }
                });
            }

            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true
            });

            string savedLastEvaluatedPrompt = _lastEvaluatedPrompt;
            int savedTokenCount = 0;
            if (isIsolated && !string.IsNullOrEmpty(savedLastEvaluatedPrompt) && _context != null)
            {
                try { savedTokenCount = GetTokenCount(savedLastEvaluatedPrompt); } catch { }
            }

            generationTask = Task.Run(async () =>
            {
                bool completedNormally = false;
                bool isFirstToken = true;
                Exception? generationException = null;
                // Exactly-once completion telemetry. Declared here (outside the try) so the
                // finally block can always emit it, even when generation fails mid-stream.
                bool telemetryEmitted = false;
                var requestStopwatch = Stopwatch.StartNew();
                var genStopwatch = new Stopwatch();
                double ttftMs = 0;
                int tokenCount = 0;
                bool isSpeculationActive = false;
                // Live tokens/sec tracker (EMA over per-token intervals) — see TokenSpeedTracker.
                var tokenSpeed = new TokenSpeedTracker();
                try
                {
                    string textToEvaluate = prompt;

                    if (isIsolated)
                    {
                        _logger.LogDebug("Executing isolated inference task. Context tracking will reset cleanly after execution.");
                        LastGenerationContextDecision = "IsolatedReset";
                        ResetContextInternal();
                        textToEvaluate = prompt;
                    }
                    else
                    {
                        int commonPrefixLength = GetSafePrefixBoundary(_lastEvaluatedPrompt, prompt);
                        if (commonPrefixLength > 0)
                        {
                            string commonPrefix = _lastEvaluatedPrompt.Substring(0, commonPrefixLength);
                            if (commonPrefixLength == _lastEvaluatedPrompt.Length)
                            {
                                textToEvaluate = prompt.Substring(_lastEvaluatedPrompt.Length);
                                LastGenerationContextDecision = "ExactReuse";
                                LastGenerationPrefixLength = commonPrefixLength;
                                _logger.LogDebug("KV Cache Prefix hit (Exact). Reusing full evaluated KV context ({PrefixLength} chars).", commonPrefixLength);
                                textToEvaluate = StripLeadingStopTokens(textToEvaluate, inferenceParams.AntiPrompts);
                            }
                            else
                            {
                                // Recurrent/M-RoPE architectures (qwen35, mamba, rwkv, jamba) cannot use the
                                // partial-prefix KV rewind: MemorySequenceRemove is not honored by the recurrent
                                // memory module (its positions stay at X), while the fresh InteractiveExecutor
                                // starts input batches at position Y=0. M-RoPE requires X < Y, so llama_decode
                                // fails (ret = -1) and generation dies or falls into repeated full re-prefills.
                                // For these models, take the slow-but-correct path: clean context + full prompt.
                                if (IsRecurrentArchitecture)
                                {
                                    _logger.LogDebug("Skipping partial KV prefix reuse for recurrent architecture '{Arch}'. Resetting context cleanly.", Architecture);
                                    LastGenerationContextDecision = "ResetRecurrentPartial";
                                    ResetContextInternal();
                                    textToEvaluate = prompt;
                                }
                                else
                                {
                                    int prefixTokenCount = 0;
                                    try { prefixTokenCount = GetTokenCount(commonPrefix); } catch { }

                                    if (prefixTokenCount > 0 && _context != null && _context.NativeHandle != null && !_context.NativeHandle.IsClosed && !_context.NativeHandle.IsInvalid)
                                    {
                                        try
                                        {
                                            _context.NativeHandle.MemorySequenceRemove((LLamaSeqId)0, (LLamaPos)prefixTokenCount, (LLamaPos)(-1));
                                            _executor = new InteractiveExecutor(_context);
                                            _lastEvaluatedPrompt = commonPrefix;
                                            textToEvaluate = prompt.Substring(commonPrefixLength);
                                            textToEvaluate = StripLeadingStopTokens(textToEvaluate, inferenceParams.AntiPrompts);
                                            LastGenerationContextDecision = "PartialReuse";
                                            LastGenerationPrefixLength = commonPrefixLength;
                                            _logger.LogDebug("KV Cache Prefix hit (Partial). Rewound sequence to {Tokens} tokens via MemorySequenceRemove. Evaluating delta ({DeltaLength} chars).", prefixTokenCount, textToEvaluate.Length);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning(ex, "Failed partial KV sequence removal; resetting context cleanly.");
                                            LastGenerationContextDecision = "ResetPartialFailed";
                                            ResetContextInternal();
                                            textToEvaluate = prompt;
                                        }
                                    }
                                    else
                                    {
                                        ResetContextInternal();
                                        textToEvaluate = prompt;
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogDebug("No common KV prefix found. Resetting context.");
                            LastGenerationContextDecision = "ResetNoPrefix";
                            ResetContextInternal();
                            textToEvaluate = prompt;
                        }
                    }

                    // Turn-boundary instrumentation (reviewer request): one line per generation
                    // with the KV reuse decision and prompt fingerprints, so a stale-context
                    // failure ("new user turn running on the previous turn's KV") is visible in
                    // the logs without digging through debug lines.
                    int prevPromptTokens = 0;
                    if (!string.IsNullOrEmpty(savedLastEvaluatedPrompt))
                    {
                        try { prevPromptTokens = GetTokenCount(savedLastEvaluatedPrompt); } catch { }
                    }
                    _logger.LogInformation(
                        "GenerationContext: decision={Decision} prefixChars={Prefix} prevPromptTokens={PrevTokens} newPromptTokens={NewTokens} prevHash={PrevHash} newHash={NewHash}",
                        LastGenerationContextDecision, LastGenerationPrefixLength, prevPromptTokens, promptTokenCount,
                        HashPrompt(savedLastEvaluatedPrompt), HashPrompt(prompt));

                    var generatedContent = new System.Text.StringBuilder();

                    // Degenerate-loop detector for the chat path. MoE / thinking models can fall
                    // into repetition attractors (think-tag spam, token stutter, n-gram cycles);
                    // when the detector fires we stop the stream here WITHOUT delivering the
                    // triggering token. ChatEngine reads LastGenerationLoopInfo, discards the
                    // looped tail, injects a self-correction instruction and regenerates.
                    GenerationLoopDetector? loopDetector = null;

                    // Seed think-block state from the prompt: qwen thinking templates end with an
                    // OPEN <think> that the model continues, so reasoning content arrives before any
                    // open tag is seen in the stream. Phrase-level loop detection is lenient inside
                    // thinking blocks (planning restates ideas by design) and strict on visible text.
                    bool startsInsideThink = GenerationLoopDetector.EndsInsideThinkBlock(prompt);

                    // Thinking-token budget: a thinking model can stream reasoning indefinitely
                    // without ever closing its think block (observed live: minutes of "stopped
                    // responding" while the model re-drafted its plan inside <think> with no
                    // visible output). The phrase-level loop detectors are deliberately lenient
                    // inside think blocks, so a slow, non-repeating reasoning drift evades them
                    // entirely. Cap uninterrupted in-think tokens per generation; beyond the cap
                    // the generation is degenerate — stop it and route through the same
                    // self-correction path as a detected loop (LastGenerationLoopInfo). Tool-call
                    // content (which qwen models may emit inside the pre-opened think block) is
                    // exempt: it is short, and cutting it mid-way would corrupt the call.
                    // The cap scales with the context window (25%, floor 4096) instead of being a
                    // fixed 4096: long-horizon planning legitimately drafts large plans inside the
                    // think block, and a fixed small cap cut those mid-reasoning. A degenerate
                    // think-loop still trips the cap — just proportionally to the window.
                    int maxThinkTokensPerGeneration = Math.Max(4096, (int)(ContextSize * 0.25));
                    bool thinkBlockWasOpen = startsInsideThink;
                    int thinkTokenCount = 0;
                    bool thinkCapFired = false;
                    bool toolTagSeen = false;

                    // Route through the speculative path when a draft model is loaded OR the
                    // zero-VRAM N-gram fallback is active (previously the fallback was advertised
                    // in the UI but never engaged because IsLoaded stayed false).
                    isSpeculationActive = IsSpeculativeDecodingEnabled &&
                                          !_speculationDisabledAfterDecodeFailure &&
                                          (SpeculativeEngine.IsLoaded || SpeculativeEngine.IsNGramFallbackEnabled);
                    var tokenStream = isSpeculationActive
                        ? SpeculativeEngine.SpeculateAndVerifyAsync(textToEvaluate, _executor, _context!, inferenceParams, SpeculativeDraftCount, generationToken)
                        : _executor.InferAsync(textToEvaluate, inferenceParams, cancellationToken: generationToken);

                    await foreach (var token in tokenStream)
                    {
                        if (generationToken.IsCancellationRequested) break;

                        tokenCount++;
                        if (isFirstToken)
                        {
                            isFirstToken = false;
                            ttftMs = requestStopwatch.Elapsed.TotalMilliseconds;
                            genStopwatch.Start();
                        }
                        
                        generatedContent.Append(token);

                        // Self-correction: detect degenerate loops in the chat path only (isolated
                        // generations like titles have no correction loop to recover through).
                        if (!isIsolated && triggerEvents)
                        {
                            loopDetector ??= new GenerationLoopDetector(startsInsideThink);
                            loopDetector.Append(token);
                            var loop = loopDetector.Detect();
                            if (loop != null)
                            {
                                _logger.LogWarning("Degenerate generation loop detected ({Reason}) after {Tokens} tokens (loop starts at char {LoopStart}). Stopping stream for self-correction.",
                                    loop.Reason, tokenCount, loop.LoopStartChar);
                                LastGenerationLoopInfo = loop;
                                break;
                            }

                            // Thinking-cap (see the budget declaration above). A generation stuck
                            // in an unclosed think block with no tool call and no visible output
                            // is degenerate no matter how varied its reasoning looks.
                            if (!thinkCapFired)
                            {
                                bool thinkOpen = loopDetector.IsInThinkBlock;
                                if (thinkOpen && !thinkBlockWasOpen)
                                {
                                    thinkTokenCount = 0; // new think block: restart the budget
                                }
                                thinkBlockWasOpen = thinkOpen;
                                if (thinkOpen && !toolTagSeen)
                                {
                                    if (token.Contains("<tool_call", StringComparison.OrdinalIgnoreCase) ||
                                        token.Contains("<|tool_call", StringComparison.OrdinalIgnoreCase) ||
                                        token.Contains("function=", StringComparison.OrdinalIgnoreCase))
                                    {
                                        toolTagSeen = true;
                                    }
                                    else
                                    {
                                        thinkTokenCount++;
                                        if (thinkTokenCount > maxThinkTokensPerGeneration)
                                        {
                                            thinkCapFired = true;
                                            _logger.LogWarning("Thinking block exceeded {Cap} tokens with no visible output and no tool call; stopping generation for self-correction (ThinkOverflow).",
                                                maxThinkTokensPerGeneration);
                                            LastGenerationLoopInfo = new GenerationLoopInfo("ThinkOverflow", 0, tokenCount);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        
                        double elapsedSec = genStopwatch.Elapsed.TotalSeconds;
                        double emaTokensPerSecond = tokenSpeed.Update(elapsedSec, tokenCount);
                        float tokensPerSecond = emaTokensPerSecond > 0
                            ? (float)emaTokensPerSecond
                            : (tokenCount > 1 && elapsedSec > 0.001 ? (float)((tokenCount - 1) / elapsedSec) : 0f);

                        if (hasTokenSubscribers && eventChannel != null && TokenGenerated != null)
                        {
                            var handlers = TokenGenerated;
                            string currentToken = token;
                            float currentTps = tokensPerSecond;
                            eventChannel.Writer.TryWrite(() => handlers.Invoke(currentToken, currentTps));
                        }
                        
                        await channel.Writer.WriteAsync(token, generationToken);
                    }
                    
                    if (!isIsolated)
                    {
                        // Update the state hash to include both the input prompt and exact generated response matching native KV cache
                        _lastEvaluatedPrompt = prompt + generatedContent.ToString();
                    }

                    // Exhausted the output budget (n_predict reached, no stop token seen): the
                    // stream was cut at the cap. Distinguish cap-hits from natural stops so
                    // ChatEngine can auto-continue long-form generations (stories, reports)
                    // across chunks instead of silently delivering a cut-off response.
                    LastGenerationHitMaxTokens = inferenceParams.MaxTokens > 0 && tokenCount >= inferenceParams.MaxTokens;
                    completedNormally = true;

                    requestStopwatch.Stop();
                    genStopwatch.Stop();

                    if (hasTokenSubscribers && eventChannel != null && TokenGenerated != null)
                    {
                        var handlers = TokenGenerated;
                        double totalElapsedMs = requestStopwatch.Elapsed.TotalMilliseconds;
                        double genDurationMs = genStopwatch.Elapsed.TotalMilliseconds;
                        int totalGeneratedTokens = isFirstToken ? 0 : tokenCount;
                        // Prefer the live EMA reading so the counter does not snap back to the
                        // flat lifetime average when the final per-token event fires.
                        double genTokSec = tokenSpeed.Current > 0
                            ? tokenSpeed.Current
                            : (genDurationMs > 0 && totalGeneratedTokens > 0) ? (totalGeneratedTokens / (genDurationMs / 1000.0)) : (totalElapsedMs > 0 ? (totalGeneratedTokens / (totalElapsedMs / 1000.0)) : 0.0);
                        eventChannel.Writer.TryWrite(() => handlers.Invoke(string.Empty, (float)genTokSec));
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Background generation was canceled.");
                }
                catch (Exception ex)
                {
                    bool isContextOverflowError = 
                        ex.Message.Contains("native memory shifting", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("context overflowed", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("context window is full", StringComparison.OrdinalIgnoreCase) ||
                        (ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) && (ex.Message.Contains("full", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("shift", StringComparison.OrdinalIgnoreCase))) ||
                        ex.GetType().Name.Contains("Context", StringComparison.OrdinalIgnoreCase) ||
                        ex.GetType().Name.Contains("LLama", StringComparison.OrdinalIgnoreCase);

                    if (isContextOverflowError)
                    {
                        if (IsRecurrentArchitecture)
                        {
                            // Recurrent/M-RoPE models cannot use native KV shifting, so a full
                            // reset + retry would just re-prefill the entire history and fail again
                            // (the classic re-prefill loop that drops tps from 60+ to ~20). The
                            // MaxTokens cap above prevents the window from filling in the first
                            // place; if it still happens (tokenizer edge case), complete the
                            // channel cleanly with whatever was already generated.
                            _logger.LogWarning(ex, "Context window filled on recurrent architecture; completing generation cleanly instead of retrying.");
                            ResetContextInternal();
                            // The stream was cut mid-generation after emitting {tokenCount}
                            // tokens. Signal the cut to ChatEngine so the response is resumed
                            // via auto-continuation instead of the turn silently ending at a
                            // sentence boundary with a partial answer.
                            LastGenerationWasCutShort = tokenCount > 0;
                            completedNormally = true;
                            generationException = null;
                        }
                        else
                        {
                            _logger.LogWarning(ex, "Model context overflowed or does not support native memory shifting. Resetting context and retrying generation cleanly.");

                            // The debug log shows these decode failures (llama_decode failed /
                            // InvalidInputBatch) originate inside the speculative verification
                            // path. Every one triggers this reset + full-history re-prefill, which
                            // is the dominant slow-down on long sessions. Disable speculation for
                            // the rest of the session so the next generations run plain direct
                            // inference instead of failing speculatively first.
                            bool isDecodeFailure =
                                ex.Message.Contains("llama_decode failed", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("InvalidInputBatch", StringComparison.OrdinalIgnoreCase);
                            if (isDecodeFailure && !_speculationDisabledAfterDecodeFailure)
                            {
                                _speculationDisabledAfterDecodeFailure = true;
                                _logger.LogWarning("Disabling speculative decoding for this session after a decode-level failure; subsequent generations use direct inference.");
                            }

                            try
                            {
                                ResetContextInternal();
                                string safePrompt = prompt;

                                if (isFirstToken && _executor != null)
                                {
                                    // Bounded retry: cap MaxTokens so a post-reset re-prefill of a
                                    // huge prompt cannot stream unbounded output. The cap scales
                                    // with the window (50%, floor 4096 — the same budget as
                                    // StreamTokensAsync) so a recovered generation can still produce
                                    // a full chunk; ChatEngine's continuation resumes it. The retry
                                    // must also fire TokenGenerated and count its tokens, otherwise
                                    // "tokens out" and the completion telemetry silently miss
                                    // entire recovered generations.
                                    var retryParams = new InferenceParams
                                    {
                                        MaxTokens = Math.Min(inferenceParams.MaxTokens < 0 ? int.MaxValue : inferenceParams.MaxTokens, Math.Max(4096, (int)(ContextSize * 0.50))),
                                        TokensKeep = inferenceParams.TokensKeep,
                                        AntiPrompts = inferenceParams.AntiPrompts,
                                        OverflowStrategy = inferenceParams.OverflowStrategy,
                                        SamplingPipeline = inferenceParams.SamplingPipeline
                                    };
                                    var retryStream = _executor.InferAsync(safePrompt, retryParams, cancellationToken: generationToken);
                                    await foreach (var token in retryStream)
                                    {
                                        if (generationToken.IsCancellationRequested) break;
                                        if (isFirstToken)
                                        {
                                            isFirstToken = false;
                                            ttftMs = requestStopwatch.Elapsed.TotalMilliseconds;
                                            genStopwatch.Start();
                                        }
                                        tokenCount++;
                                        if (hasTokenSubscribers && eventChannel != null && TokenGenerated != null)
                                        {
                                            var handlers = TokenGenerated;
                                            string currentToken = token;
                                            float currentTps = 0f;
                                            eventChannel.Writer.TryWrite(() => handlers.Invoke(currentToken, currentTps));
                                        }
                                        await channel.Writer.WriteAsync(token, generationToken);
                                    }
                                    completedNormally = true;
                                    generationException = null;
                                }
                                else
                                {
                                    _logger.LogWarning("Context limit reached after tokens were emitted; completing channel cleanly.");
                                    // Same mid-stream cut signal as the recurrent branch: tokens
                                    // were already streamed and the stream ends early, so the
                                    // response must be resumed rather than left truncated.
                                    LastGenerationWasCutShort = tokenCount > 0;
                                    completedNormally = true;
                                    generationException = null;
                                }
                            }
                            catch (Exception retryEx)
                            {
                                _logger.LogError(retryEx, "Error during context reset retry");
                                generationException = retryEx;
                            }
                        }
                    }
                    else
                    {
                        _logger.LogError(ex, "Error in background generation (model={ModelPath}, architecture={Architecture}, promptChars={PromptChars}, tokensEmitted={TokenCount}).", CurrentModelPath, Architecture, prompt?.Length ?? 0, tokenCount);
                        generationException = ex;
                    }
                    // Rotating log in %LOCALAPPDATA%\Klydis\logs (see KlydisLog). Full ex.ToString()
                    // so inner exceptions (AggregateException from native interop, etc.) and all
                    // stack frames survive in the durable chat_debug.log.
                    Klydis.Core.Diagnostics.KlydisLog.AppendChatDebug($"[{DateTime.Now:HH:mm:ss.fff}] INFERENCE EXCEPTION (model={CurrentModelPath}, arch={Architecture}, promptChars={prompt?.Length ?? 0}, tokensEmitted={tokenCount}): {ex}{Environment.NewLine}");
                }
                finally
                {
                    // Cancellation (model switch, user stop, teardown) aborts the stream before
                    // it completes. Expose that to the caller so an empty cancelled stream is not
                    // mistaken for a degenerate model output — ChatEngine must not fire the
                    // empty-response correction cascade against an in-flight model load (each
                    // correction rebuilds the context, re-triggering the cancel).
                    LastGenerationWasCancelled = !completedNormally && generationToken.IsCancellationRequested;

                    // A generation that failed/canceled BEFORE decoding a single token did not
                    // dirty the context: it is still in the same clean state it was in after the
                    // start-of-generation reset (or still holds the untouched prefix cache).
                    // Resetting again is a full context dispose + recreate (on recurrent
                    // architectures like qwen35 that cannot use the fast KV clear) with zero
                    // benefit — the observed "alternating models / not loading" death spiral was
                    // exactly this: a failed empty generation reset the context, the queue
                    // auto-processed the next message, which reset again, forever. Skip the
                    // rebuild when nothing was decoded; the next generation's prefix check
                    // handles genuinely dirty caches.
                    if (isIsolated)
                    {
                        ResetContextInternal();
                    }
                    else if (!completedNormally && tokenCount > 0)
                    {
                        ResetContextInternal();
                    }

                    // ALWAYS emit completion telemetry, including failed/partial generations.
                    // Previously this lived only in the success path, so a turn that failed
                    // mid-stream streamed tokens (counted as "tokens out" via TokenGenerated)
                    // but never reported its PromptTokenCount — the status-bar "in" counter
                    // undercounted massively on long-horizon sessions (the observed "2M out,
                    // ~0 in" swap). Exactly-once via telemetryEmitted.
                    if (!telemetryEmitted)
                    {
                        telemetryEmitted = true;
                        requestStopwatch.Stop();
                        genStopwatch.Stop();
                        double totalElapsedMs = requestStopwatch.Elapsed.TotalMilliseconds;
                        double genDurationMs = genStopwatch.Elapsed.TotalMilliseconds;
                        int totalGeneratedTokens = isFirstToken ? 0 : tokenCount;
                        double genTokSec = (genDurationMs > 0 && totalGeneratedTokens > 0) ? (totalGeneratedTokens / (genDurationMs / 1000.0)) : (totalElapsedMs > 0 ? (totalGeneratedTokens / (totalElapsedMs / 1000.0)) : 0.0);
                        double e2eTokSec = totalElapsedMs > 0 ? (totalGeneratedTokens / (totalElapsedMs / 1000.0)) : 0.0;

                        var telemetry = new InferenceTelemetry(
                            RequestId: Guid.NewGuid().ToString("N"),
                            TargetModelPath: CurrentModelPath ?? "Unknown",
                            DraftModelPath: SpeculativeEngine.LoadedDraftPath,
                            IsSpeculativeEnabled: isSpeculationActive,
                            PromptLengthChars: prompt.Length,
                            PromptTokenCount: promptTokenCount,
                            GeneratedTokenCount: totalGeneratedTokens,
                            TimeToFirstTokenMs: Math.Round(ttftMs, 2),
                            GenerationDurationMs: Math.Round(genDurationMs, 2),
                            TotalElapsedMs: Math.Round(totalElapsedMs, 2),
                            GenerationTokensPerSecond: Math.Round(genTokSec, 2),
                            EndToEndTokensPerSecond: Math.Round(e2eTokSec, 2),
                            SpeculativeMetrics: isSpeculationActive ? SpeculativeEngine.LastTelemetry : null,
                            IsIsolated: isIsolated,
                            PromptPrefillTokensPerSecond: ttftMs > 0 && promptTokenCount > 0
                                ? Math.Round(promptTokenCount / (ttftMs / 1000.0), 2)
                                : 0
                        );
                        LastTelemetry = telemetry;
                        InferenceCompleted?.Invoke(telemetry);
                    }

                    channel.Writer.Complete(completedNormally ? null : generationException);
                }
            }, CancellationToken.None);

            lock (_generationCtsLock)
            {
                _activeGenerationTask = generationTask;
            }

            try
            {
                // Yield tokens from the channel
                await foreach (var token in channel.Reader.ReadAllAsync(ct))
                {
                    yield return token;
                }
            }
            finally
            {
                // Ensure the background task has fully exited before we release the model lock.
                // Otherwise, a canceled request might leave the background task running,
                // and a subsequent request could dispose the context while it is still in use!
                try { await generationTask.ConfigureAwait(false); } catch { }
                if (eventChannel != null && eventDispatcherTask != null)
                {
                    eventChannel.Writer.Complete();
                    try
                    {
                        await eventDispatcherTask.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error awaiting token event dispatcher task completion.");
                    }
                }
            }

            _logger.LogDebug("Finished token generation.");
        }
        finally
        {
            lock (_generationCtsLock)
            {
                if (_activeGenerationCts == linkedCts)
                {
                    _activeGenerationCts = null;
                }
                if (_activeGenerationTask == generationTask)
                {
                    _activeGenerationTask = null;
                }
            }
            try { linkedCts.Dispose(); } catch (ObjectDisposedException) { }
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Builds the sampling pipeline for a generation. MoE models (qwen35moe / Qwen3.6-Next,
    /// mixtral, deepseek-v2/v3, ...) are prone to repetition attractors and tangential drift,
    /// so they get a stricter anti-repetition profile: lower temperature, higher repeat penalty,
    /// plus small frequency/presence penalties that actively suppress self-repeat. Dense models
    /// keep the proven default profile.
    /// </summary>
    private LLama.Sampling.ISamplingPipeline BuildSamplingPipeline()
    {
        // Control/special tokens (e.g. qwen's <channel|> control token) must never stream to
        // the user; llama.cpp's sampler chain does not exclude them. See
        // SpecialTokenFilterPipeline.
        LLama.Sampling.DefaultSamplingPipeline profile = IsMixtureOfExperts
            ? new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = 0.6f,
                TopP = 0.9f,
                MinP = 0.05f,
                RepeatPenalty = 1.25f,
                FrequencyPenalty = 0.15f,
                PresencePenalty = 0.15f
            }
            : new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = 0.7f,
                TopP = 0.9f,
                MinP = 0.05f,
                // 1.15 (was 1.1): thinking models in production stuttered "The The The..." on a
                // cold first generation; a slightly stronger repeat penalty breaks that attractor
                // without harming long-form quality.
                RepeatPenalty = 1.15f
            };

        if (!EnableToolGrammarConstrainedDecoding || !IsQwenNativeToolCallModel)
        {
            return new SpecialTokenFilterPipeline(profile);
        }

        // Grammar-constrained twin with the identical sampling profile: from the moment the
        // model opens a <tool_call> block, sampling is constrained to the native call grammar
        // so malformed/abandoned calls cannot reach the regex parser.
        var constrained = new LLama.Sampling.DefaultSamplingPipeline
        {
            Temperature = profile.Temperature,
            TopP = profile.TopP,
            TopK = profile.TopK,
            MinP = profile.MinP,
            TypicalP = profile.TypicalP,
            RepeatPenalty = profile.RepeatPenalty,
            FrequencyPenalty = profile.FrequencyPenalty,
            PresencePenalty = profile.PresencePenalty,
            Seed = profile.Seed,
            Grammar = new LLama.Sampling.Grammar(ToolCallGrammar.BuildQwenNativeGbnf(), "root")
        };

        return new SpecialTokenFilterPipeline(
            new ToolCallConstrainedSamplingPipeline(profile, constrained, ToolCallGrammarFormat.QwenNative, _logger));
    }

    /// <summary>
    /// True when the loaded model uses the qwen native tool-call template
    /// (<c>&lt;tool_call&gt;&lt;function=...&gt;</c>) — the only format the grammar gate
    /// understands. Mirrors ChatEngine's isQwenThinkingModel detection.
    /// </summary>
    private bool IsQwenNativeToolCallModel =>
        IsQwenThinkingArchitecture(Architecture) &&
        !string.IsNullOrWhiteSpace(RawChatTemplate) &&
        RawChatTemplate.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, int tokensKeep, [EnumeratorCancellation] CancellationToken ct = default)
    {
        int maxKeep = (int)(ContextSize / 2);
        int safeTokensKeep = Math.Clamp(tokensKeep, 0, maxKeep);

        // Bound chat generations so a degenerate or over-ambitious model cannot stream
        // unboundedly (production logs show a 3-minute 10-chapter repetition and an 80-second
        // garbage run). The degenerate-loop detector already stops repetition attractors in
        // the chat path, so this cap only guards the truly unbounded case: 50% of the window,
        // with NO fixed upper ceiling — the budget scales with the context size (a 128K window
        // yields a 64K output chunk; 256K yields 128K). A fixed 65536 ceiling silently capped
        // long-horizon output regardless of window size. ChatEngine's auto-continuation loop
        // (bounded per turn, keyed on LastGenerationHitMaxTokens) resumes long responses
        // across chunks, and the recurrent-architecture cap (window - prompt - 512) still
        // prevents the window from ever filling.
        int maxChatTokens = Math.Max(4096, (int)(ContextSize * 0.50));

        var inferenceParams = new InferenceParams 
        { 
            MaxTokens = maxChatTokens,
            TokensKeep = safeTokensKeep,
            AntiPrompts = stopTokens.ToList(),
            // Native KV shifting (TruncateAndReprefill) is unsupported on recurrent/M-RoPE
            // models — it corrupts the cache and every retry re-prefills the whole history.
            // GenerateAsync caps MaxTokens for those architectures so the window never fills;
            // ThrowException is the no-shift belt-and-suspenders fallback.
            OverflowStrategy = IsRecurrentArchitecture
                ? LLama.Common.ContextOverflowStrategy.ThrowException
                : LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = BuildSamplingPipeline()
        };

        await foreach (var token in GenerateAsync(prompt, inferenceParams, triggerEvents: true, isIsolated: false, ct: ct))
        {
            yield return token;
        }
    }

    public Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default)
    {
        return GenerateTextAsync(prompt, isIsolated: false, maxTokens: 256, ct: ct);
    }

    public Task<string> GenerateTextAsync(string prompt, bool isIsolated, CancellationToken ct = default)
    {
        return GenerateTextAsync(prompt, isIsolated: isIsolated, maxTokens: 256, ct: ct);
    }

    public async Task<string> GenerateTextAsync(string prompt, bool isIsolated, int maxTokens, CancellationToken ct = default)
    {
        var inferenceParams = new InferenceParams 
        { 
            MaxTokens = maxTokens > 0 ? maxTokens : 256,
            // Recurrent/M-RoPE models cannot use native KV shifting; ThrowException avoids
            // corrupting the cache when the window fills (GenerateAsync caps MaxTokens for
            // recurrent architectures, so this is a rare edge case).
            OverflowStrategy = IsRecurrentArchitecture
                ? LLama.Common.ContextOverflowStrategy.ThrowException
                : LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = BuildSamplingPipeline()
        };
        var sb = new System.Text.StringBuilder();
        await foreach (var token in GenerateAsync(prompt, inferenceParams, triggerEvents: false, isIsolated: isIsolated, ct: ct))
        {
            sb.Append(token);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resets the engine context and executor state, clearing cached prefix state.
    /// </summary>
    public async Task ResetContextAsync(CancellationToken ct = default)
    {
        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ResetContextInternal();
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public void ResetContext()
    {
        _ = ResetContextAsync();
    }

    /// <summary>
    /// Max auto-allocated context ceiling for the currently loaded architecture. Hybrid/recurrent
    /// SSM architectures (qwen35/qwen3next/qwen35moe, mamba, rwkv, jamba) have tiny per-layer KV
    /// caches (only their sparse attention layers grow) and run up to their native 256K ceiling;
    /// dense transformers cap at 128K. Must mirror the ceiling computed at load time.
    /// </summary>
    internal uint GetAutoContextCeiling()
    {
        string archLower = (Architecture ?? "").ToLowerInvariant();
        bool isPureSsm = archLower is "mamba" or "rwkv" or "jamba";
        bool isHybridAttentionArch = archLower is "qwen35" or "qwen3next" or "qwen35moe";
        return (isPureSsm || isHybridAttentionArch) ? 262144u : 131072u;
    }

    /// <summary>
    /// Re-applies updated user hardware parameters (ContextSize, BatchSize, UBatchSize) dynamically to the active loaded context.
    /// </summary>
    public async Task ReapplyModelParametersAsync()
    {
        if (!IsModelLoaded || _weights == null || _modelParams == null) return;

        await _modelLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_weights == null || _modelParams == null) return;

            uint autoContextCeiling = GetAutoContextCeiling();
            uint targetContextSize = UserContextLimit > 0
                ? UserContextLimit
                : (uint)Math.Clamp(_lastOffloadPlan?.RecommendedContextSize ?? 32768, 2048, autoContextCeiling);

            uint safeBatchSize = UserBatchSize > 0
                ? UserBatchSize
                : (uint)Math.Max(2048, _lastOffloadPlan?.RecommendedBatchSize ?? 2048);

            uint safeUBatchSize = UserUBatchSize > 0
                ? UserUBatchSize
                : 512u;

            if (_modelParams.ContextSize == targetContextSize &&
                _modelParams.BatchSize == safeBatchSize &&
                _modelParams.UBatchSize == safeUBatchSize)
            {
                return;
            }

            _logger?.LogInformation("Re-applying model parameters: ContextSize={Ctx}, BatchSize={Batch}, UBatchSize={UBatch}",
                targetContextSize, safeBatchSize, safeUBatchSize);

            _modelParams.ContextSize = targetContextSize;
            _modelParams.BatchSize = safeBatchSize;
            _modelParams.UBatchSize = safeUBatchSize;

            var oldContext = _context;
            _context = null;
            _executor = null;

            if (oldContext != null)
            {
                try { oldContext.Dispose(); } catch { }
            }

            _context = _weights.CreateContext(_modelParams);
            _executor = new InteractiveExecutor(_context);
            _lastEvaluatedPrompt = string.Empty;
            _logger?.LogInformation("Successfully re-created inference context with updated hardware settings.");

            if (IsSpeculativeDecodingEnabled && !string.IsNullOrEmpty(CurrentModelPath))
            {
                _ = Task.Run(async () => await AttachSpeculativeDraftAsync(CurrentModelPath));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to re-apply updated model parameters to active context.");
        }
        finally
        {
            _modelLock.Release();
        }
    }

    internal void ResetContextInternal()
    {
        lock (_contextResetLock)
        {
            _lastEvaluatedPrompt = string.Empty;
            if (_weights != null && _modelParams != null)
            {
                var context = _context;

                // Fast path: clear the native KV cache in place with MemorySequenceRemove and
                // re-instantiate the executor. This avoids the full context dispose + recreate
                // (VRAM realloc + graph re-init, hundreds of ms) that used to run on EVERY
                // isolated background generation and every compression event — which also wiped
                // the freshly-built chat KV cache right after the first exchange.
                //
                // Recurrent/M-RoPE architectures (qwen35, mamba, rwkv, jamba) are excluded:
                // their recurrent memory module ignores llama_kv_cache_seq_rm, so the cache
                // would keep stale positions while a fresh executor starts at 0, and
                // llama_decode fails (M-RoPE requires position monotonicity).
                if (!IsRecurrentArchitecture &&
                    context != null &&
                    context.NativeHandle != null &&
                    !context.NativeHandle.IsClosed &&
                    !context.NativeHandle.IsInvalid)
                {
                    try
                    {
                        context.NativeHandle.MemorySequenceRemove((LLamaSeqId)0, (LLamaPos)0, (LLamaPos)(-1));
                        _executor = new InteractiveExecutor(context);
                        _logger?.LogDebug("Fast KV cache clear completed in ResetContextInternal.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Fast KV cache clear failed; falling back to full context recreation.");
                    }
                }

                var oldContext = _context;
                _context = null;
                _executor = null;

                if (oldContext != null)
                {
                    try
                    {
                        oldContext.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error disposing old context during ResetContextInternal.");
                    }
                }

                try
                {
                    _context = _weights.CreateContext(_modelParams);
                    _executor = new InteractiveExecutor(_context);
                    _logger?.LogDebug("Clean context recreation completed in ResetContextInternal.");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to recreate context during ResetContextInternal.");
                    _context = null;
                    _executor = null;
                }
            }
        }
    }

    internal static int GetSafePrefixBoundary(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0;
        int minLen = Math.Min(s1.Length, s2.Length);
        int matchLen = 0;
        while (matchLen < minLen && s1[matchLen] == s2[matchLen])
        {
            matchLen++;
        }

        if (matchLen == s1.Length) return matchLen; // s1 is exact prefix of s2

        // Look back for nearest newline or section tag boundary (e.g., '\n' or '>')
        int safeBoundary = matchLen;
        while (safeBoundary > 0 && s1[safeBoundary - 1] != '\n' && s1[safeBoundary - 1] != '>')
        {
            safeBoundary--;
        }
        return safeBoundary > 0 ? safeBoundary : matchLen;
    }

    /// <summary>
    /// Short stable fingerprint of a prompt (first 6 hex chars of SHA-256) for the
    /// GenerationContext instrumentation line — enough to spot "same prompt, different turn"
    /// and "prompt changed but KV was reused" at a glance without logging full prompts.
    /// </summary>
    private static string HashPrompt(string text)
    {
        if (string.IsNullOrEmpty(text)) return "—";
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    private static readonly string[] TurnEndingStopTokens = new[]
    {
        "<|eot_id|>",
        "<|im_end|>",
        "<|end_of_text|>",
        "<end_of_turn>",
        "</s>",
        "<|end|>"
    };

    /// <summary>
    /// The static turn-ending stop tokens, pre-sorted longest-first. The base set is the common
    /// case, so we only pay for a full re-sort when anti-prompts contribute anything new.
    /// </summary>
    private static readonly string[] TurnEndingStopTokensSorted = TurnEndingStopTokens
        .OrderByDescending(s => s.Length)
        .ToArray();

    internal string StripLeadingStopTokens(string delta, IEnumerable<string>? antiPrompts)
    {
        if (string.IsNullOrEmpty(delta)) return delta;

        var candidateStopTokens = new HashSet<string>(TurnEndingStopTokens, StringComparer.Ordinal);
        bool addedAny = false;
        if (antiPrompts != null)
        {
            foreach (var ap in antiPrompts)
            {
                if (!string.IsNullOrWhiteSpace(ap) && !ap.Contains("start", StringComparison.OrdinalIgnoreCase))
                {
                    addedAny |= candidateStopTokens.Add(ap);
                }
            }
        }

        var orderedStopTokens = addedAny
            ? candidateStopTokens.OrderByDescending(s => s.Length).ToArray()
            : TurnEndingStopTokensSorted;

        bool strippedAny = true;
        while (strippedAny && !string.IsNullOrEmpty(delta))
        {
            strippedAny = false;
            foreach (var stopToken in orderedStopTokens)
            {
                if (delta.StartsWith(stopToken, StringComparison.Ordinal))
                {
                    // Only strip a leading stop token when the ALREADY-EVALUATED context ends
                    // with it — i.e. it is a duplicate the previous generation emitted. When the
                    // delta legitimately starts with a NEW message terminator (e.g. the
                    // <|im_end|> closing the previous assistant turn in an exact KV-prefix
                    // chain), the evaluated context does NOT contain it yet and stripping would
                    // corrupt the template (the model would never see the turn close).
                    if (!_lastEvaluatedPrompt.EndsWith(stopToken, StringComparison.Ordinal))
                    {
                        _logger?.LogDebug("Keeping leading stop token '{StopToken}' in prefix delta: evaluated context does not end with it (new message terminator, not a duplicate).", stopToken);
                        continue;
                    }

                    _logger?.LogDebug("Stripped leading turn stop token '{StopToken}' from prefix delta.", stopToken);
                    _lastEvaluatedPrompt += stopToken;
                    delta = delta.Substring(stopToken.Length);

                    if (delta.StartsWith("\n", StringComparison.Ordinal))
                    {
                        _lastEvaluatedPrompt += "\n";
                        delta = delta.Substring(1);
                    }
                    else if (delta.StartsWith("\r\n", StringComparison.Ordinal))
                    {
                        _lastEvaluatedPrompt += "\r\n";
                        delta = delta.Substring(2);
                    }

                    strippedAny = true;
                    break;
                }
            }
        }

        return delta;
    }

    /// <summary>
    /// Formats messages using a ChatTemplate and generates tokens asynchronously.
    /// </summary>
    public IAsyncEnumerable<string> GenerateChatAsync(
        IList<ChatMessage> messages, 
        ChatTemplate template, 
        InferenceParams inferenceParams, 
        CancellationToken ct = default)
    {
        var prompt = template.Format(messages);
        return GenerateAsync(prompt, inferenceParams, triggerEvents: true, isIsolated: false, ct: ct);
    }

    /// <summary>
    /// Tokenizes the provided text and returns the token count.
    /// </summary>
    public int GetTokenCount(string text)
    {
        var context = _context;
        if (context == null)
            throw new InvalidOperationException("Model is not loaded.");

        return context.Tokenize(text, special: true).Length;
    }

    /// <summary>
    /// Truncates prompt from start so that the remaining text fits within targetTokenLimit tokens.
    /// </summary>
    private string TruncatePromptToTokenLimit(string prompt, int targetTokenLimit)
    {
        var context = _context;
        if (context == null || string.IsNullOrEmpty(prompt)) return prompt;
        try
        {
            var tokens = context.Tokenize(prompt, special: true);
            if (tokens.Length <= targetTokenLimit) return prompt;

            var slicedTokens = tokens.AsSpan(tokens.Length - targetTokenLimit).ToArray();
            var decoder = new LLama.StreamingTokenDecoder(context);
            foreach (var token in slicedTokens)
            {
                decoder.Add(token);
            }
            return decoder.Read();
        }
        catch
        {
            int estCharLimit = targetTokenLimit * 3;
            if (prompt.Length > estCharLimit)
            {
                return prompt.Substring(prompt.Length - estCharLimit);
            }
            return prompt;
        }
    }

    /// <summary>
    /// Cancels any active token generation task and awaits background teardown.
    /// </summary>
    public async Task CancelActiveGenerationAsync()
    {
        CancellationTokenSource? ctsToCancel;
        Task? taskToAwait;
        lock (_generationCtsLock)
        {
            ctsToCancel = _activeGenerationCts;
            _activeGenerationCts = null;
            taskToAwait = _activeGenerationTask;
            _activeGenerationTask = null;
        }

        if (ctsToCancel != null)
        {
            _logger?.LogInformation("Canceling active token generation task.");
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException) { }

            try
            {
                ctsToCancel.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        if (taskToAwait != null)
        {
            try
            {
                await taskToAwait.ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Unloads the model asynchronously, canceling active generation tasks and freeing native resources.
    /// </summary>
    public async Task UnloadModelAsync(CancellationToken ct = default)
    {
        await CancelActiveGenerationAsync();

        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SpeculativeEngine.UnloadAsync().ConfigureAwait(false);
            UnloadModelInternal();
        }
        finally
        {
            _modelLock.Release();
        }

        ModelStateChanged?.Invoke(false, null);
    }

    /// <summary>
    /// Unloads the model and frees native resources and VRAM.
    /// </summary>
    public void UnloadModel()
    {
        _ = UnloadModelAsync();
    }

    private void UnloadModelInternal()
    {
        if (_executor != null)
        {
            _executor = null;
        }

        var ctx = _context;
        _context = null;

        var weights = _weights;
        _weights = null;

        CurrentModelPath = null;
        RawChatTemplate = null;
        FineTuneName = null;
        // Clear the model params so ContextSize stops reporting the previous model's window
        // after an unload (it would otherwise go back to the 32768 fallback).
        _modelParams = null;

        // A CPU-only load pinned the process to P-cores; release that so the rest of the app
        // (and later GPU loads) can use the full processor set again.
        if (_pCoreAffinityApplied)
        {
            Hardware.CpuAffinityHelper.RestoreProcessAffinity();
            _pCoreAffinityApplied = false;
        }

        if (ctx != null || weights != null)
        {
            SafeOffloadDisposal(ctx, weights);
        }

        _logger.LogInformation("Model unloaded and native resource disposal offloaded.");
    }

    /// <summary>
    /// Saves native KV context state snapshot to disk file.
    /// </summary>
    public async Task SaveStateAsync(string filePath)
    {
        await _modelLock.WaitAsync();
        try
        {
            if (_context != null && !_context.NativeHandle.IsClosed && !_context.NativeHandle.IsInvalid)
            {
                _context.SaveState(filePath);
                _logger?.LogInformation("Successfully saved native KV state snapshot to {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save native KV state snapshot to {FilePath}", filePath);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Loads native KV context state snapshot from disk file.
    /// </summary>
    public async Task LoadStateAsync(string filePath)
    {
        await _modelLock.WaitAsync();
        try
        {
            if (_context != null && !_context.NativeHandle.IsClosed && !_context.NativeHandle.IsInvalid && System.IO.File.Exists(filePath))
            {
                _context.LoadState(filePath);
                _logger?.LogInformation("Successfully restored native KV state snapshot from {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to restore native KV state snapshot from {FilePath}", filePath);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>
    /// Disposes the inference engine asynchronously and releases all native resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            await UnloadModelAsync().ConfigureAwait(false);
            await SpeculativeEngine.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
        _modelLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the inference engine and releases all native resources.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            UnloadModel();
            SpeculativeEngine.Dispose();
        }
        catch { }
        _modelLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string SanitizeThinkingTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?(?:</think>|$)", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"</?think>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(clean) ? text : clean;
    }
}
