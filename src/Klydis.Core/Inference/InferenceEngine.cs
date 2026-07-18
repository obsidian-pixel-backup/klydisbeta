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
    private StatelessExecutor? _executor;

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
        return Task.Run(() =>
        {
            _logger.LogInformation("Loading model from {ModelPath} with {GpuLayers} GPU layers.", modelPath, offloadPlan.GpuLayers);

            UnloadModel();

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
                UseMemoryLock = true,
                // Explicitly target GPU 0
                MainGpu = 0
            };
            
            // Use native F16 KV cache to allow GPU Tensor Core acceleration and prevent on-the-fly dequantization overhead
            parameters.TypeK = LLama.Native.GGMLType.GGML_TYPE_F16;
            parameters.TypeV = LLama.Native.GGMLType.GGML_TYPE_F16;

            _weights = LLamaWeights.LoadFromFile(parameters);
            _context = _weights.CreateContext(parameters);
            
            // Using StatelessExecutor to prevent state/KV cache corruption in multi-turn conversation
            _executor = new StatelessExecutor(_weights, parameters);

            CurrentModelPath = modelPath;
            _logger.LogInformation("Model loaded successfully.");
            ModelStateChanged?.Invoke(true, modelPath);
        });
    }

    /// <summary>
    /// Generates tokens asynchronously based on the provided prompt string.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateAsync(
        string prompt, 
        InferenceParams inferenceParams, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsModelLoaded || _executor == null || _context == null)
            throw new InvalidOperationException("Model is not loaded.");

        _logger.LogDebug("Starting token generation.");

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true
        });

        // Run generation on a background thread to prevent UI backpressure from slowing down the GPU
        _ = Task.Run(async () =>
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                int tokenCount = 0;
                bool isFirstToken = true;

                await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cancellationToken: ct))
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
                    
                    float tokensPerSecond = tokenCount > 0 ? (float)(tokenCount / stopwatch.Elapsed.TotalSeconds) : 0;
                    TokenGenerated?.Invoke(token, tokensPerSecond);
                    
                    await channel.Writer.WriteAsync(token, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background generation");
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        // Yield tokens from the channel
        await foreach (var token in channel.Reader.ReadAllAsync(ct))
        {
            yield return token;
        }

        _logger.LogDebug("Finished token generation.");
    }

    public IAsyncEnumerable<string> StreamTokensAsync(string prompt, string[] stopTokens, CancellationToken ct)
    {
        var inferenceParams = new InferenceParams 
        { 
            AntiPrompts = stopTokens.ToList(),
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = 0.7f,
                TopP = 0.9f,
                MinP = 0.05f,
                RepeatPenalty = 1.1f
            }
        };
        return GenerateAsync(prompt, inferenceParams, ct);
    }

    public async Task<string> GenerateTextAsync(string prompt, CancellationToken ct = default)
    {
        var inferenceParams = new InferenceParams();
        var sb = new System.Text.StringBuilder();
        await foreach (var token in GenerateAsync(prompt, inferenceParams, ct))
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
        return GenerateAsync(prompt, inferenceParams, ct);
    }

    /// <summary>
    /// Tokenizes the provided text and returns the token count.
    /// </summary>
    public int GetTokenCount(string text)
    {
        if (_context == null)
            throw new InvalidOperationException("Model is not loaded.");

        return _context.Tokenize(text, special: true).Length;
    }

    /// <summary>
    /// Unloads the model and frees native resources and VRAM.
    /// </summary>
    public void UnloadModel()
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
        ModelStateChanged?.Invoke(false, null);
    }

    /// <summary>
    /// Disposes the inference engine and releases all native resources.
    /// </summary>
    public void Dispose()
    {
        UnloadModel();
    }
}
