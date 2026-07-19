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

    /// <summary>
    /// Event fired when a token is generated, providing the token text and current tokens/second rate.
    /// </summary>
    public event Action<string, float>? TokenGenerated;

    /// <summary>
    /// Event fired when a model is loaded or unloaded (isLoaded, modelPath).
    /// </summary>
    public event Action<bool, string?>? ModelStateChanged;

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
    /// Loads a GGUF model asynchronously with the specified hardware offloading plan.
    /// </summary>
    public Task LoadModelAsync(string modelPath, Klydis.Core.Hardware.OffloadPlan offloadPlan)
    {
        return Task.Run(async () =>
        {
            await _modelLock.WaitAsync();
            try
            {
                _logger.LogInformation("Loading model from {ModelPath} with {GpuLayers} GPU layers.", modelPath, offloadPlan.GpuLayers);

                UnloadModelInternal();

                // Configure model parameters for maximum GPU throughput
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = (uint)offloadPlan.RecommendedContextSize,
                    GpuLayerCount = offloadPlan.GpuLayers, // -1 = offload ALL layers including output head
                    BatchSize = (uint)offloadPlan.RecommendedBatchSize,
                    UBatchSize = (uint)offloadPlan.RecommendedBatchSize, // Align physical batch size with BatchSize
                    FlashAttention = true,
                    Threads = Math.Max(Environment.ProcessorCount / 2, 1),
                    BatchThreads = Math.Max(Environment.ProcessorCount / 2, 1),
                    // Pin model weights in RAM to prevent OS paging — critical for GPU DMA transfers
                    UseMemoryLock = false,
                    // Disable Memory map to prevent file bounds corruption errors on Windows,
                    // forcing weights directly into RAM.
                    UseMemorymap = false
                };
                
                // Use native Q8_0 KV cache to halve VRAM usage with negligible quality loss
                parameters.TypeK = LLama.Native.GGMLType.GGML_TYPE_Q8_0;
                parameters.TypeV = LLama.Native.GGMLType.GGML_TYPE_Q8_0;

                _modelParams = parameters;
                _weights = LLamaWeights.LoadFromFile(parameters);
                _context = _weights.CreateContext(parameters);
                
                // Using InteractiveExecutor for hybrid fast-path prefix caching
                _executor = new InteractiveExecutor(_context);
                _lastEvaluatedPrompt = string.Empty;

                CurrentModelPath = modelPath;
                _logger.LogInformation("Model loaded successfully.");
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _modelLock.WaitAsync(ct);
        try
        {
            if (!IsModelLoaded || _executor == null || _context == null)
                throw new InvalidOperationException("Model is not loaded.");

            _logger.LogDebug("Starting token generation.");

            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true
            });

            var generationTask = Task.Run(async () =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    int tokenCount = 0;
                    bool isFirstToken = true;
                    string textToEvaluate = prompt;

                    // HYBRID EXECUTOR LOGIC: Fast-path for appends, Slow-path for edits/deletes
                    if (!string.IsNullOrEmpty(_lastEvaluatedPrompt) && prompt.StartsWith(_lastEvaluatedPrompt))
                    {
                        textToEvaluate = prompt.Substring(_lastEvaluatedPrompt.Length);
                        _logger.LogDebug("Fast-path inference triggered. Evaluating delta of {DeltaLength} chars.", textToEvaluate.Length);
                    }
                    else
                    {
                        _logger.LogDebug("Slow-path inference triggered. Re-evaluating entire prompt.");
                        _context?.Dispose();
                        _context = _weights!.CreateContext(_modelParams!);
                        _executor = new InteractiveExecutor(_context); // Reset the executor state
                        _lastEvaluatedPrompt = string.Empty;
                    }

                    var generatedContent = new System.Text.StringBuilder();

                    await foreach (var token in _executor.InferAsync(textToEvaluate, inferenceParams, cancellationToken: ct))
                    {
                        if (isFirstToken)
                        {
                            isFirstToken = false;
                            stopwatch.Restart(); // Reset stopwatch after prompt processing to measure pure generation t/s
                        }
                        else
                        {
                            tokenCount++;
                        }
                        
                        generatedContent.Append(token);
                        
                        float tokensPerSecond = tokenCount > 0 ? (float)(tokenCount / stopwatch.Elapsed.TotalSeconds) : 0;
                        if (triggerEvents)
                        {
                            TokenGenerated?.Invoke(token, tokensPerSecond);
                        }
                        
                        await channel.Writer.WriteAsync(token, ct);
                    }
                    
                    // Update the state hash to include both the input prompt and the generated response
                    _lastEvaluatedPrompt = prompt + generatedContent.ToString();
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
            _modelLock.Release();
        }
    }

    public IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, int tokensKeep, CancellationToken ct)
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
        return GenerateAsync(prompt, inferenceParams, true, ct);
    }

    public async Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default)
    {
        var inferenceParams = new InferenceParams { MaxTokens = -1 };
        var sb = new System.Text.StringBuilder();
        await foreach (var token in GenerateAsync(prompt, inferenceParams, false, ct))
        {
            sb.Append(token);
        }
        return sb.ToString();
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
        return GenerateAsync(prompt, inferenceParams, true, ct);
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
    /// Unloads the model and frees native resources and VRAM.
    /// </summary>
    public void UnloadModel()
    {
        _modelLock.Wait();
        try
        {
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
