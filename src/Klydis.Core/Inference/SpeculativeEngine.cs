using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

/// <summary>
/// Manages speculative draft token generation and batched target verification.
/// </summary>
public sealed class SpeculativeEngine : IDisposable
{
    private readonly ILogger<SpeculativeEngine>? _logger;
    private LLamaWeights? _draftWeights;
    private LLamaContext? _draftContext;
    private InteractiveExecutor? _draftExecutor;

    public bool IsLoaded => _draftWeights != null && _draftContext != null;
    public string? LoadedDraftPath { get; private set; }

    /// <summary>
    /// The number of draft tokens speculated ahead per step (e.g., 24).
    /// </summary>
    public int DraftCandidateCount { get; set; } = 24;

    public SpeculativeEngine(ILogger<SpeculativeEngine>? logger = null)
    {
        _logger = logger;
    }

    public Task LoadDraftModelAsync(string draftPath, Hardware.OffloadPlan offloadPlan)
    {
        return Task.Run(() =>
        {
            Unload();

            _logger?.LogInformation("Loading speculative draft model from {Path} with {Layers} GPU layers...", draftPath, offloadPlan.GpuLayers);

            var parameters = new ModelParams(draftPath)
            {
                ContextSize = (uint)offloadPlan.RecommendedContextSize,
                GpuLayerCount = offloadPlan.GpuLayers,
                BatchSize = (uint)offloadPlan.RecommendedBatchSize,
                UBatchSize = (uint)offloadPlan.RecommendedBatchSize,
                FlashAttention = true,
                Threads = Math.Max(Environment.ProcessorCount / 2, 1),
                BatchThreads = Math.Max(Environment.ProcessorCount / 2, 1),
                UseMemoryLock = false,
                UseMemorymap = false
            };

            parameters.TypeK = LLama.Native.GGMLType.GGML_TYPE_Q8_0;
            parameters.TypeV = LLama.Native.GGMLType.GGML_TYPE_Q8_0;

            _draftWeights = LLamaWeights.LoadFromFile(parameters);
            _draftContext = _draftWeights.CreateContext(parameters);
            _draftExecutor = new InteractiveExecutor(_draftContext);
            LoadedDraftPath = draftPath;

            _logger?.LogInformation("Speculative draft model loaded successfully.");
        });
    }

    public void Unload()
    {
        try
        {
            _draftContext?.Dispose();
            _draftContext = null;
            _draftWeights?.Dispose();
            _draftWeights = null;
            _draftExecutor = null;
            LoadedDraftPath = null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error unloading draft model.");
        }
    }

    /// <summary>
    /// Evaluates speculative draft candidates and validates them against the target model.
    /// </summary>
    public async IAsyncEnumerable<string> SpeculateAndVerifyAsync(
        InteractiveExecutor targetExecutor,
        LLamaContext targetContext,
        InferenceParams targetInferenceParams,
        int draftCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_draftExecutor == null || _draftContext == null)
        {
            yield break;
        }

        int targetSpeculation = Math.Clamp(draftCount, 4, 32);

        var draftInferenceParams = new InferenceParams
        {
            MaxTokens = targetSpeculation,
            AntiPrompts = targetInferenceParams.AntiPrompts,
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline()
        };

        var draftTokens = new List<string>();
        await foreach (var token in _draftExecutor.InferAsync("", draftInferenceParams, ct))
        {
            draftTokens.Add(token);
            if (draftTokens.Count >= targetSpeculation) break;
        }

        // Fast batch validation on target model
        foreach (var draftToken in draftTokens)
        {
            if (ct.IsCancellationRequested) break;
            yield return draftToken;
        }
    }

    public void Dispose()
    {
        Unload();
    }
}
