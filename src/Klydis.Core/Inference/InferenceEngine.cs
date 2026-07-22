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
public sealed class InferenceEngine : IInferenceEngine, IDisposable
{
    private readonly ILogger<InferenceEngine> _logger;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private ModelParams? _modelParams;
    private InteractiveExecutor? _executor;
    private string _lastEvaluatedPrompt = string.Empty;
    private readonly SemaphoreSlim _modelLock = new SemaphoreSlim(1, 1);
    private CancellationTokenSource? _activeGenerationCts;
    private readonly object _generationCtsLock = new();

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
    public uint ContextSize => _modelParams?.ContextSize ?? 4096;

    /// <summary>
    /// Gets the path of the currently loaded model.
    /// </summary>
    public string? CurrentModelPath { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceEngine"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public InferenceEngine(ILogger<InferenceEngine> logger)
    {
        _logger = logger;
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
            try
            {
                _logger.LogInformation("Loading model from {ModelPath} with {GpuLayers} GPU layers.", modelPath, offloadPlan.GpuLayers);

                SpeculativeEngine.Unload();
                UnloadModelInternal();

                // Lock process execution strictly to Physical P-Cores to prevent E-Core throttling
                Hardware.CpuAffinityHelper.ApplyPCoreAffinityToProcess();

                // Configure model parameters for maximum GPU throughput
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = (uint)offloadPlan.RecommendedContextSize,
                    GpuLayerCount = offloadPlan.GpuLayers, // -1 = offload ALL layers including output head
                    BatchSize = (uint)offloadPlan.RecommendedBatchSize,
                    UBatchSize = (uint)offloadPlan.RecommendedBatchSize, // Align physical batch size with BatchSize
                    FlashAttention = true,
                    Threads = Hardware.CpuAffinityHelper.GetPCoreCount(),
                    BatchThreads = Hardware.CpuAffinityHelper.GetPCoreCount(),
                    // Enable Memory map to eliminate double-buffering in System RAM
                    UseMemorymap = true,
                    UseMemoryLock = false
                };
                
                // Target KV cache precision set to Q4_0 for maximum memory efficiency and throughput
                parameters.TypeK = LLama.Native.GGMLType.GGML_TYPE_Q4_0;
                parameters.TypeV = LLama.Native.GGMLType.GGML_TYPE_Q4_0;

                _modelParams = parameters;
                try
                {
                    _weights = LLamaWeights.LoadFromFile(parameters);
                    _context = _weights.CreateContext(parameters);
                    
                    // Using InteractiveExecutor for hybrid fast-path prefix caching
                    _executor = new InteractiveExecutor(_context);
                    _lastEvaluatedPrompt = string.Empty;

                    CurrentModelPath = modelPath;
                    _logger.LogInformation("Model loaded successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load LLama model or create context from {ModelPath}.", modelPath);
                    UnloadModelInternal();
                    throw;
                }
            }
            finally
            {
                _modelLock.Release();
            }

            ModelStateChanged?.Invoke(true, modelPath);
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

            var generationTask = Task.Run(async () =>
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
                        ResetContextInternal();
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
                        // Update the state hash to include both the input prompt and the generated response
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
                    if (isIsolated || !completedNormally)
                    {
                        ResetContextInternal();
                    }
                    channel.Writer.Complete();
                }
            }, CancellationToken.None);

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
        return GenerateTextAsync(prompt, isIsolated: false, ct);
    }

    public async Task<string> GenerateTextAsync(string prompt, bool isIsolated, CancellationToken ct = default)
    {
        var inferenceParams = new InferenceParams 
        { 
            MaxTokens = -1,
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

    private void ResetContextInternal()
    {
        _lastEvaluatedPrompt = string.Empty;
        if (_weights != null && _modelParams != null)
        {
            _context?.Dispose();
            _context = _weights.CreateContext(_modelParams);
            _executor = new InteractiveExecutor(_context);
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
        lock (_generationCtsLock)
        {
            ctsToCancel = _activeGenerationCts;
            _activeGenerationCts = null;
        }

        if (ctsToCancel != null)
        {
            _logger?.LogInformation("Canceling active token generation task.");
            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException) { }

            ctsToCancel.Dispose();
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Unloads the model asynchronously, canceling active generation tasks and freeing native resources.
    /// </summary>
    public async Task UnloadModelAsync(CancellationToken ct = default)
    {
        await CancelActiveGenerationAsync();

        await _modelLock.WaitAsync(ct);
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

        if (_context != null)
        {
            _context.Dispose();
            _context = null;
        }

        if (_weights != null)
        {
            _weights.Dispose();
            _weights = null;
        }

        CurrentModelPath = null;
        _logger.LogInformation("Model unloaded and native resources freed.");
    }

    /// <summary>
    /// Disposes the inference engine and releases all native resources.
    /// </summary>
    public void Dispose()
    {
        UnloadModel();
        _modelLock.Dispose();
    }
}
