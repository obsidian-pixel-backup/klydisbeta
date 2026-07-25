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

    private readonly object _contextResetLock = new();

    public SpeculativeEngine SpeculativeEngine { get; } = new();
    public SpeculativeDecodingService? SpeculativeDecodingService { get; set; }
    public bool IsSpeculativeDecodingEnabled { get; set; } = true;
    public int SpeculativeDraftCount { get; set; } = 24;
    private string _selectedDraftModelPath = "auto";
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
    /// Gets a value indicating whether a model is currently loaded.
    /// </summary>
    public bool IsModelLoaded => _weights != null && _context != null;

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
        _logger = logger;
        NativeResourceDisposer = nativeResourceDisposer;
    }

    private void SafeOffloadDisposal(params IDisposable?[] resources)
    {
        var disposer = NativeResourceDisposer;
        if (disposer != null)
        {
            disposer.EnqueueForDisposal(resources);
        }
        else
        {
            Task.Run(() =>
            {
                var ordered = resources.Where(r => r != null)
                                       .OrderBy(r => r is LLamaWeights || r!.GetType().Name.Contains("Weights") ? 2 : (r is LLamaContext || r!.GetType().Name.Contains("Context") ? 0 : 1));
                foreach (var r in ordered)
                {
                    try { r?.Dispose(); } catch { }
                }
            });
        }
    }

    /// <summary>
    /// Resolves and attaches a speculative draft model for the current target model.
    /// </summary>
    public async Task AttachSpeculativeDraftAsync(string targetModelPath)
    {
        if (SpeculativeDecodingService == null) return;

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
            }
            else
            {
                SpeculativeEngine.Unload();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to attach speculative draft model.");
            SpeculativeStatus = $"Speculative decoding unavailable: {ex.Message}";
            SpeculativeStatusChanged?.Invoke(SpeculativeStatus);
            SpeculativeEngine.Unload();
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

                SpeculativeEngine.Unload();
                UnloadModelInternal();

                // Only lock process CPU affinity mask for CPU-only execution (0 GPU layers)
                if (offloadPlan.GpuLayers == 0)
                {
                    Hardware.CpuAffinityHelper.ApplyPCoreAffinityToProcess();
                }

                // Configure model parameters for maximum GPU throughput
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = (uint)offloadPlan.RecommendedContextSize,
                    GpuLayerCount = offloadPlan.GpuLayers, // -1 = offload ALL layers including output head
                    BatchSize = (uint)offloadPlan.RecommendedBatchSize,
                    UBatchSize = (uint)offloadPlan.RecommendedBatchSize, // Align physical batch size with BatchSize
                    FlashAttention = true,
                    Threads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8),
                    BatchThreads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8),
                    // Enable Memory map to eliminate double-buffering in System RAM
                    UseMemorymap = true,
                    UseMemoryLock = false
                };
                
                // Target KV cache precision set based on TargetKvQuantization configuration
                var kvType = TargetKvQuantization switch
                {
                    KvCacheQuantizationType.F16 => LLama.Native.GGMLType.GGML_TYPE_F16,
                    KvCacheQuantizationType.Q8_0 => LLama.Native.GGMLType.GGML_TYPE_Q8_0,
                    KvCacheQuantizationType.Q4_1 => LLama.Native.GGMLType.GGML_TYPE_Q4_1,
                    _ => LLama.Native.GGMLType.GGML_TYPE_Q4_0
                };
                parameters.TypeK = kvType;
                parameters.TypeV = kvType;

                var compat = GgufCompatibilityAdapter.Evaluate(modelPath);
                if (compat.WarningMessage != null)
                {
                    _logger.LogWarning("GGUF Pre-flight Notice: {Message}", compat.WarningMessage);
                }

                var metadata = Models.GgufMetadataReader.Parse(modelPath);
                if (metadata != null)
                {
                    CurrentKvCacheEstimate = KvCacheCalculator.Calculate(metadata, offloadPlan.RecommendedContextSize, TargetKvQuantization);
                    _logger.LogInformation("KV Cache VRAM estimate ({Arch}): {Mb} MB ({Gb} GB), {Bpt} bytes/token.",
                        CurrentKvCacheEstimate.AttentionArchitecture,
                        CurrentKvCacheEstimate.TotalVramMegabytes,
                        CurrentKvCacheEstimate.TotalVramGigabytes,
                        CurrentKvCacheEstimate.BytesPerToken);
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
                        throw new InvalidOperationException(
                            $"Failed to load model '{Path.GetFileName(modelPath)}' natively. Architecture '{compat.Architecture}' tensor layout is incompatible with the active native llama.dll (Native error: {loadEx.Message}). To support this model variant, place an updated llama.dll build into '{overrideDir}' and restart.",
                            loadEx);
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
                catch (Exception gpuEx) when (offloadPlan.GpuLayers != 0 && !compat.RequiresUpdatedNativeBackend)
                {
                    _logger.LogWarning(gpuEx, "GPU model/context creation failed for {ModelPath}. Falling back to CPU execution.", modelPath);
                    UnloadModelInternal();

                    // Fallback to CPU-only execution with conservative context
                    parameters.GpuLayerCount = 0;
                    parameters.ContextSize = (uint)Math.Min(4096, offloadPlan.RecommendedContextSize);

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
                
                _lastEvaluatedPrompt = string.Empty;

                CurrentModelPath = modelPath;
                Architecture = !string.IsNullOrWhiteSpace(metadata?.Architecture)
                    ? metadata.Architecture
                    : System.IO.Path.GetFileNameWithoutExtension(modelPath);
                _logger.LogInformation("Model loaded successfully with architecture '{Architecture}'.", Architecture);
                success = true;
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
        await _modelLock.WaitAsync(ct);
        try
        {
            if (!IsModelLoaded || _executor == null || _context == null)
                throw new InvalidOperationException("Model is not loaded.");

            // Safety fallback: Ensure SamplingPipeline is initialized to prevent NullReferenceExceptions during inference
            if (inferenceParams.SamplingPipeline == null)
            {
                inferenceParams.SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline();
            }

            _logger.LogDebug("Starting token generation.");

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
                try
                {
                    var requestStopwatch = Stopwatch.StartNew();
                    var genStopwatch = new Stopwatch();
                    double ttftMs = 0;
                    int tokenCount = 0;
                    bool isFirstToken = true;
                    string textToEvaluate = prompt;

                    if (isIsolated)
                    {
                        _logger.LogDebug("Executing isolated inference task. Context tracking will reset after execution.");
                        if (string.IsNullOrEmpty(savedLastEvaluatedPrompt))
                        {
                            ResetContextInternal();
                        }
                        textToEvaluate = prompt;
                    }
                    else
                    {
                        // HYBRID EXECUTOR LOGIC: Fast-path for appended conversation turns, full reset for new chats
                        if (!string.IsNullOrEmpty(_lastEvaluatedPrompt) && prompt.StartsWith(_lastEvaluatedPrompt, StringComparison.Ordinal))
                        {
                            textToEvaluate = prompt.Substring(_lastEvaluatedPrompt.Length);
                            _logger.LogDebug("Fast-path inference triggered. Initial delta length: {DeltaLength} chars.", textToEvaluate.Length);

                            // Strip leading turn-ending stop tokens (and trailing newlines/spaces) to prevent AntiPrompts
                            // from matching at token 0 of prompt pre-fill.
                            textToEvaluate = StripLeadingStopTokens(textToEvaluate, inferenceParams.AntiPrompts);
                        }
                        else
                        {
                            _logger.LogDebug("Re-evaluating full conversation context.");
                            ResetContextInternal();
                            textToEvaluate = prompt;
                        }
                    }

                    var generatedContent = new System.Text.StringBuilder();

                    var tokenStream = (IsSpeculativeDecodingEnabled && SpeculativeEngine.IsLoaded)
                        ? SpeculativeEngine.SpeculateAndVerifyAsync(textToEvaluate, _executor, _context!, inferenceParams, SpeculativeDraftCount, generationToken)
                        : _executor.InferAsync(textToEvaluate, inferenceParams, cancellationToken: generationToken);

                    await foreach (var token in tokenStream)
                    {
                        if (generationToken.IsCancellationRequested) break;

                        if (isFirstToken)
                        {
                            isFirstToken = false;
                            ttftMs = requestStopwatch.Elapsed.TotalMilliseconds;
                            genStopwatch.Start();
                        }
                        else
                        {
                            tokenCount++;
                        }
                        
                        generatedContent.Append(token);
                        
                        float tokensPerSecond = tokenCount > 0 ? (float)(tokenCount / genStopwatch.Elapsed.TotalSeconds) : 0;
                        if (triggerEvents)
                        {
                            TokenGenerated?.Invoke(token, tokensPerSecond);
                        }
                        
                        await channel.Writer.WriteAsync(token, generationToken);
                    }
                    
                    if (!isIsolated)
                    {
                        // Update the state hash to include both the input prompt and exact generated response matching native KV cache
                        _lastEvaluatedPrompt = prompt + generatedContent.ToString();
                    }
                    completedNormally = true;

                    requestStopwatch.Stop();
                    genStopwatch.Stop();
                    double totalElapsedMs = requestStopwatch.Elapsed.TotalMilliseconds;
                    double genDurationMs = genStopwatch.Elapsed.TotalMilliseconds;
                    int totalGeneratedTokens = isFirstToken ? 0 : tokenCount + 1;
                    double genTokSec = (genDurationMs > 0 && totalGeneratedTokens > 1) ? ((totalGeneratedTokens - 1) / (genDurationMs / 1000.0)) : (totalElapsedMs > 0 ? (totalGeneratedTokens / (totalElapsedMs / 1000.0)) : 0.0);
                    double e2eTokSec = totalElapsedMs > 0 ? (totalGeneratedTokens / (totalElapsedMs / 1000.0)) : 0.0;

                    int promptTokenCount = 0;
                    try { promptTokenCount = GetTokenCount(prompt); } catch { promptTokenCount = Math.Max(1, prompt.Length / 4); }

                    var telemetry = new InferenceTelemetry(
                        RequestId: Guid.NewGuid().ToString("N"),
                        TargetModelPath: CurrentModelPath ?? "Unknown",
                        DraftModelPath: SpeculativeEngine.LoadedDraftPath,
                        IsSpeculativeEnabled: IsSpeculativeDecodingEnabled && SpeculativeEngine.IsLoaded,
                        PromptLengthChars: prompt.Length,
                        PromptTokenCount: promptTokenCount,
                        GeneratedTokenCount: totalGeneratedTokens,
                        TimeToFirstTokenMs: Math.Round(ttftMs, 2),
                        GenerationDurationMs: Math.Round(genDurationMs, 2),
                        TotalElapsedMs: Math.Round(totalElapsedMs, 2),
                        GenerationTokensPerSecond: Math.Round(genTokSec, 2),
                        EndToEndTokensPerSecond: Math.Round(e2eTokSec, 2),
                        SpeculativeMetrics: (IsSpeculativeDecodingEnabled && SpeculativeEngine.IsLoaded) ? SpeculativeEngine.LastTelemetry : null
                    );
                    LastTelemetry = telemetry;
                    InferenceCompleted?.Invoke(telemetry);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Background generation was canceled.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in background generation");
                }
                finally
                {
                    if (isIsolated)
                    {
                        if (!string.IsNullOrEmpty(savedLastEvaluatedPrompt) && _context != null && !_context.NativeHandle.IsClosed && !_context.NativeHandle.IsInvalid && savedTokenCount > 0)
                        {
                            try
                            {
                                _context.NativeHandle.MemorySequenceRemove((LLamaSeqId)0, (LLamaPos)savedTokenCount, (LLamaPos)(-1));
                                _lastEvaluatedPrompt = savedLastEvaluatedPrompt;
                            }
                            catch
                            {
                                ResetContextInternal();
                            }
                        }
                        else
                        {
                            ResetContextInternal();
                        }
                    }
                    else if (!completedNormally)
                    {
                        ResetContextInternal();
                    }
                    channel.Writer.Complete();
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
                if (triggerEvents)
                {
                    TokenGenerated?.Invoke(string.Empty, 0f);
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

    public async IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, int tokensKeep, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var inferenceParams = new InferenceParams 
        { 
            MaxTokens = -1,
            TokensKeep = tokensKeep,
            AntiPrompts = stopTokens.ToList(),
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = 0.7f,
                TopP = 0.9f,
                MinP = 0.05f,
                RepeatPenalty = 1.1f
            }
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
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = 0.7f,
                TopP = 0.9f,
                MinP = 0.05f,
                RepeatPenalty = 1.1f
            }
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
    public void ResetContext()
    {
        _modelLock.Wait();
        try
        {
            ResetContextInternal();
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
            else
            {
                var oldContext = _context;
                _context = null;
                _executor = null;

                if (oldContext != null)
                {
                    SafeOffloadDisposal(oldContext);
                }
            }
        }
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

    internal string StripLeadingStopTokens(string delta, IEnumerable<string>? antiPrompts)
    {
        if (string.IsNullOrEmpty(delta)) return delta;

        var candidateStopTokens = new HashSet<string>(TurnEndingStopTokens, StringComparer.Ordinal);
        if (antiPrompts != null)
        {
            foreach (var ap in antiPrompts)
            {
                if (!string.IsNullOrWhiteSpace(ap) && !ap.Contains("start", StringComparison.OrdinalIgnoreCase))
                {
                    candidateStopTokens.Add(ap);
                }
            }
        }

        var orderedStopTokens = candidateStopTokens.OrderByDescending(s => s.Length).ToList();

        bool strippedAny = true;
        while (strippedAny && !string.IsNullOrEmpty(delta))
        {
            strippedAny = false;
            foreach (var stopToken in orderedStopTokens)
            {
                if (delta.StartsWith(stopToken, StringComparison.Ordinal))
                {
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
        CancelActiveGenerationAsync().GetAwaiter().GetResult();

        _modelLock.Wait();
        try
        {
            SpeculativeEngine.Unload();
            UnloadModelInternal();
        }
        finally
        {
            _modelLock.Release();
        }
        
        ModelStateChanged?.Invoke(false, null);
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
        await UnloadModelAsync().ConfigureAwait(false);
        await SpeculativeEngine.DisposeAsync().ConfigureAwait(false);
        _modelLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the inference engine and releases all native resources.
    /// </summary>
    public void Dispose()
    {
        UnloadModel();
        SpeculativeEngine.Dispose();
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
