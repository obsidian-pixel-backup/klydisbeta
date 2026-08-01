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
    private int _activeUseCount = 0;

    public InferenceEngine Engine { get; }
    public string ModelId { get; }
    public DateTime LastActive { get; set; }
    public int ActiveUseCount => Volatile.Read(ref _activeUseCount);

    public LoadedModelInfo(InferenceEngine engine, string modelId)
    {
        Engine = engine;
        ModelId = modelId;
        LastActive = DateTime.UtcNow;
    }

    public void IncrementActiveUse()
    {
        Interlocked.Increment(ref _activeUseCount);
        LastActive = DateTime.UtcNow;
    }

    public void DecrementActiveUse()
    {
        int count = Interlocked.Decrement(ref _activeUseCount);
        if (count < 0) Interlocked.Exchange(ref _activeUseCount, 0);
        LastActive = DateTime.UtcNow;
    }
}

/// <summary>
/// Manages multiple loaded model instances, enforcing VRAM budgets, LRU eviction, and background idle unloading.
/// </summary>
public sealed class ModelPool : IDisposable, IAsyncDisposable
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
    private readonly INativeResourceDisposer? _nativeResourceDisposer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelPool"/> class.
    /// </summary>
    public ModelPool(
        ModelRegistry modelRegistry,
        GpuProfiler gpuProfiler,
        SystemProfiler systemProfiler,
        OffloadStrategy offloadStrategy,
        ILoggerFactory loggerFactory,
        INativeResourceDisposer? nativeResourceDisposer = null)
    {
        _modelRegistry = modelRegistry;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ModelPool>();
        _nativeResourceDisposer = nativeResourceDisposer;

        // Start background idle eviction task
        _ = IdleEvictionLoopAsync(_idleTimeoutCts.Token);
    }

    /// <summary>
    /// Tracks active use for a model to prevent eviction while an inference stream is active.
    /// </summary>
    public void TrackActiveUse(string modelId)
    {
        if (_loadedModels.TryGetValue(modelId, out var modelInfo))
        {
            modelInfo.IncrementActiveUse();
        }
    }

    /// <summary>
    /// Releases active use for a model after an inference stream completes.
    /// </summary>
    public void ReleaseActiveUse(string modelId)
    {
        if (_loadedModels.TryGetValue(modelId, out var modelInfo))
        {
            modelInfo.DecrementActiveUse();
        }
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
                await EvictLruModelAsync().ConfigureAwait(false);
                var newGpuInfo = await _gpuProfiler.GetGpuInfoAsync();
                availableVram = newGpuInfo != null ? newGpuInfo.FreeVramMb * 1024L * 1024L : 0;
            }

            // Read GGUF metadata for dynamic sizing
            var metadata = GgufMetadataReader.Parse(modelFilePath);
            int totalLayers = metadata != null && metadata.BlockCount.HasValue ? (int)metadata.BlockCount.Value : 32;
            long layerSizeBytes = modelInfo.FileSizeBytes / totalLayers; // Approximation
            
            int rawContextLength = (int)(metadata?.ContextLength ?? 32768);
            int contextLength = Math.Clamp(rawContextLength < 32768 ? 32768 : rawContextLength, 32768, 131072);
            
            // KV cache per layer per token: 2 (K+V) * HeadCountKv * HeadDim * sizeof(element)
            // Klydis enforces Q4_0 4-bit quantized KV cache (configured in InferenceEngine), so sizeof = 0.5 bytes.
            long kvCachePerLayerBytes = 1024; // Safe default: 2 * 8 * 128 * 0.5 = 1024
            if (metadata != null && metadata.EmbeddingLength.HasValue && metadata.HeadCount.HasValue && metadata.HeadCountKv.HasValue)
            {
                long headDim = metadata.EmbeddingLength.Value / metadata.HeadCount.Value;
                // K + V (2) * HeadCountKv * headDim * 0.5 bytes (Q4_0 4-bit quantized KV cache)
                kvCachePerLayerBytes = (long)(2 * metadata.HeadCountKv.Value * headDim * 0.5);
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
            var engine = new InferenceEngine(engineLogger, _nativeResourceDisposer);
            try
            {
                await engine.LoadModelAsync(modelFilePath, offloadPlan);
            }
            catch
            {
                await engine.DisposeAsync().ConfigureAwait(false);
                throw;
            }

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
    /// Evicts the least recently used model from the pool asynchronously.
    /// </summary>
    internal async Task EvictLruModelAsync()
    {
        var lruModel = _loadedModels.Values
            .Where(m => m.ActiveUseCount == 0)
            .OrderBy(m => m.LastActive)
            .FirstOrDefault();
        if (lruModel != null)
        {
            _logger.LogInformation("Evicting LRU model {ModelId} to free VRAM.", lruModel.ModelId);
            await lruModel.Engine.UnloadModelAsync().ConfigureAwait(false);
            await lruModel.Engine.DisposeAsync().ConfigureAwait(false);
            _loadedModels.TryRemove(lruModel.ModelId, out _);
        }
    }

    /// <summary>
    /// Evicts the least recently used model from the pool.
    /// </summary>
    private void EvictLruModel()
    {
        var lruModel = _loadedModels.Values
            .Where(m => m.ActiveUseCount == 0)
            .OrderBy(m => m.LastActive)
            .FirstOrDefault();
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
                        .Where(m => m.ActiveUseCount == 0 && now - m.LastActive > _idleTimeout)
                        .ToList();

                    foreach (var model in idleModels)
                    {
                        _logger.LogInformation("Unloading model {ModelId} due to idle timeout.", model.ModelId);
                        await model.Engine.UnloadModelAsync(ct).ConfigureAwait(false);
                        await model.Engine.DisposeAsync().ConfigureAwait(false);
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
    /// Disposes the pool and forcefully unloads all managed models asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _idleTimeoutCts.Cancel();
        _idleTimeoutCts.Dispose();

        await _poolLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var model in _loadedModels.Values)
            {
                await model.Engine.DisposeAsync().ConfigureAwait(false);
            }
            _loadedModels.Clear();
        }
        finally
        {
            _poolLock.Release();
            _poolLock.Dispose();
        }
        GC.SuppressFinalize(this);
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
        GC.SuppressFinalize(this);
    }
}
