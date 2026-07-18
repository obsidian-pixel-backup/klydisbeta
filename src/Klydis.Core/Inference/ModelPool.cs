using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Klydis.Core.Inference;

using Klydis.Core.Hardware;
using Klydis.Core.Models;

/// <summary>
/// Tracks metadata for a loaded model inside the pool.
/// </summary>
internal class LoadedModelInfo
{
    public InferenceEngine Engine { get; }
    public string ModelId { get; }
    public DateTime LastActive { get; set; }

    public LoadedModelInfo(InferenceEngine engine, string modelId)
    {
        Engine = engine;
        ModelId = modelId;
        LastActive = DateTime.UtcNow;
    }
}

/// <summary>
/// Manages multiple loaded model instances, enforcing VRAM budgets, LRU eviction, and background idle unloading.
/// </summary>
public sealed class ModelPool : IDisposable
{
    private readonly ModelRegistry _modelRegistry;
    private readonly GpuProfiler _gpuProfiler;
    private readonly SystemProfiler _systemProfiler;
    private readonly OffloadStrategy _offloadStrategy;
    private readonly ILogger<ModelPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
    
    private readonly ConcurrentDictionary<string, LoadedModelInfo> _loadedModels = new();
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private readonly CancellationTokenSource _idleTimeoutCts = new();
    
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelPool"/> class.
    /// </summary>
    public ModelPool(
        ModelRegistry modelRegistry,
        GpuProfiler gpuProfiler,
        SystemProfiler systemProfiler,
        OffloadStrategy offloadStrategy,
        ILoggerFactory loggerFactory)
    {
        _modelRegistry = modelRegistry;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ModelPool>();

        // Start background idle eviction task
        _ = IdleEvictionLoopAsync(_idleTimeoutCts.Token);
    }

    /// <summary>
    /// Ensures a model is loaded. If it's not loaded, loads it, potentially evicting LRU models if VRAM is low.
    /// </summary>
    public async Task<InferenceEngine> EnsureLoadedAsync(string modelId)
    {
        await _poolLock.WaitAsync();
        try
        {
            if (_loadedModels.TryGetValue(modelId, out var existingModel))
            {
                existingModel.LastActive = DateTime.UtcNow;
                return existingModel.Engine;
            }

            _logger.LogInformation("Model {ModelId} not loaded. Preparing to load.", modelId);

            var modelInfo = _modelRegistry.GetModel(modelId);
            if (modelInfo == null)
                throw new InvalidOperationException($"Model {modelId} not found in registry.");

            var modelFilePath = modelInfo.FilePath;
            
        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        var systemInfo = await _systemProfiler.GetSystemInfoAsync();
            long availableVram = gpuInfo != null ? gpuInfo.FreeVramMb * 1024L * 1024L : 0;
            
            // Simplified threshold logic: evict LRU models until we have at least 2GB of VRAM or are empty.
            long safeVramThreshold = 2L * 1024 * 1024 * 1024;
            while (_loadedModels.Count > 0 && availableVram < safeVramThreshold)
            {
                EvictLruModel();
                var newGpuInfo = await _gpuProfiler.GetGpuInfoAsync();
                availableVram = newGpuInfo != null ? newGpuInfo.FreeVramMb * 1024L * 1024L : 0;
            }

            // Read GGUF metadata for dynamic sizing
            var metadata = GgufMetadataReader.Parse(modelFilePath);
            int totalLayers = metadata != null && metadata.BlockCount.HasValue ? (int)metadata.BlockCount.Value : 32;
            long layerSizeBytes = modelInfo.FileSizeBytes / totalLayers; // Approximation
            
            // Cap context length to practical default to prevent VRAM overallocation.
            // The model's trained context (often 1M+) would require enormous KV cache.
            int rawContextLength = (int)(metadata?.ContextLength ?? 8192);
            int contextLength = Math.Min(rawContextLength, 32768);
            
            // KV cache per layer per token: 2 (K+V) * HeadCountKv * HeadDim * sizeof(element)
            // We use Q8_0 KV cache (configured in InferenceEngine), so sizeof = 1 byte.
            long kvCachePerLayerBytes = 2048; // Safe default: 2 * 8 * 128 * 1
            if (metadata != null && metadata.EmbeddingLength.HasValue && metadata.HeadCount.HasValue && metadata.HeadCountKv.HasValue)
            {
                long headDim = metadata.EmbeddingLength.Value / metadata.HeadCount.Value;
                // K + V (2) * HeadCountKv * headDim * 1 byte (Q8_0 quantized KV cache)
                kvCachePerLayerBytes = 2 * metadata.HeadCountKv.Value * headDim * 1;
            }

            var offloadPlan = _offloadStrategy.CalculatePlan(
                totalLayers, 
                layerSizeBytes, 
                kvCachePerLayerBytes, 
                contextLength, 
                gpuInfo, 
                systemInfo, 
                OffloadStrategyType.FullGpu);
            
            var engineLogger = _loggerFactory.CreateLogger<InferenceEngine>();
            var engine = new InferenceEngine(engineLogger);
            await engine.LoadModelAsync(modelFilePath, offloadPlan);

            var newModelInfo = new LoadedModelInfo(engine, modelId);
            _loadedModels[modelId] = newModelInfo;

            return engine;
        }
        finally
        {
            _poolLock.Release();
        }
    }

    /// <summary>
    /// Evicts the least recently used model from the pool.
    /// </summary>
    private void EvictLruModel()
    {
        var lruModel = _loadedModels.Values.OrderBy(m => m.LastActive).FirstOrDefault();
        if (lruModel != null)
        {
            _logger.LogInformation("Evicting LRU model {ModelId} to free VRAM.", lruModel.ModelId);
            lruModel.Engine.UnloadModel();
            lruModel.Engine.Dispose();
            _loadedModels.TryRemove(lruModel.ModelId, out _);
        }
    }

    /// <summary>
    /// Background loop to automatically unload idle models after a timeout.
    /// </summary>
    private async Task IdleEvictionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                await _poolLock.WaitAsync(ct);
                try
                {
                    var now = DateTime.UtcNow;
                    var idleModels = _loadedModels.Values
                        .Where(m => now - m.LastActive > _idleTimeout)
                        .ToList();

                    foreach (var model in idleModels)
                    {
                        _logger.LogInformation("Unloading model {ModelId} due to idle timeout.", model.ModelId);
                        model.Engine.UnloadModel();
                        model.Engine.Dispose();
                        _loadedModels.TryRemove(model.ModelId, out _);
                    }
                }
                finally
                {
                    _poolLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in idle eviction loop.");
            }
        }
    }

    /// <summary>
    /// Disposes the pool and forcefully unloads all managed models.
    /// </summary>
    public void Dispose()
    {
        _idleTimeoutCts.Cancel();
        _idleTimeoutCts.Dispose();

        _poolLock.Wait();
        try
        {
            foreach (var model in _loadedModels.Values)
            {
                model.Engine.Dispose();
            }
            _loadedModels.Clear();
        }
        finally
        {
            _poolLock.Release();
            _poolLock.Dispose();
        }
    }
}
