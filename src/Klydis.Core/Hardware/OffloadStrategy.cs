using System;

namespace Klydis.Core.Hardware;

/// <summary>
/// Determines the strategy used for offloading inference work.
/// </summary>
public enum OffloadStrategyType
{
    /// <summary>
    /// Maximum layers offloaded to GPU to maximize performance.
    /// </summary>
    FullGpu,
    
    /// <summary>
    /// Leaves some VRAM headroom for the OS or other applications.
    /// </summary>
    BalancedSplit,
    
    /// <summary>
    /// Forces inference entirely on the CPU.
    /// </summary>
    CpuOnly,
    
    /// <summary>
    /// User-defined manual layer count.
    /// </summary>
    Custom
}

/// <summary>
/// Contains the recommended hardware configuration plan for model loading.
/// </summary>
public record OffloadPlan(
    int GpuLayers,
    int CpuLayers,
    int EstimatedVramUsageMb,
    int RecommendedContextSize,
    int RecommendedBatchSize,
    OffloadStrategyType StrategyUsed);

/// <summary>
/// Calculates optimal model offloading configurations given hardware and model metadata.
/// </summary>
public class OffloadStrategy
{
    // Estimated fixed VRAM overhead for CUDA context and system drivers (in MB).
    private const int CudaContextOverheadMb = 500;
    
    // Safety buffer for Balanced Strategy (in MB).
    private const int BalancedBufferMb = 1500;

    /// <summary>
    /// Calculates the optimal offload plan for the current hardware and target model.
    /// </summary>
    /// <param name="totalLayers">Total number of layers in the model.</param>
    /// <param name="layerSizeBytes">Estimated size of a single layer in bytes.</param>
    /// <param name="kvCachePerLayerBytes">Estimated size of KV cache per layer per token in bytes.</param>
    /// <param name="contextLength">Desired context window length.</param>
    /// <param name="gpuInfo">Current GPU information, or null if no GPU is available.</param>
    /// <param name="systemInfo">Current System information.</param>
    /// <param name="strategyType">The strategy to use for calculation.</param>
    /// <param name="customLayers">The number of layers to use if <see cref="OffloadStrategyType.Custom"/> is selected.</param>
    /// <returns>An <see cref="OffloadPlan"/> containing the recommended settings.</returns>
    public OffloadPlan CalculatePlan(
        int totalLayers,
        long layerSizeBytes,
        long kvCachePerLayerBytes,
        int contextLength,
        GpuInfo? gpuInfo,
        SystemInfo systemInfo,
        OffloadStrategyType strategyType = OffloadStrategyType.BalancedSplit,
        int customLayers = 0)
    {
        if (strategyType == OffloadStrategyType.CpuOnly)
        {
            return new OffloadPlan(
                GpuLayers: 0,
                CpuLayers: totalLayers,
                EstimatedVramUsageMb: 0,
                RecommendedContextSize: CalculateSafeContextSize(systemInfo.AvailableRamGb * 1024, kvCachePerLayerBytes, totalLayers, contextLength),
                RecommendedBatchSize: 512, // Default CPU batch size
                StrategyUsed: OffloadStrategyType.CpuOnly
            );
        }

        if (strategyType == OffloadStrategyType.Custom)
        {
            int clampedLayers = Math.Clamp(customLayers, 0, totalLayers);
            int estimatedVram = EstimateVramUsage(clampedLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength);
            return new OffloadPlan(
                GpuLayers: clampedLayers,
                CpuLayers: totalLayers - clampedLayers,
                EstimatedVramUsageMb: estimatedVram,
                RecommendedContextSize: contextLength,
                RecommendedBatchSize: 512,
                StrategyUsed: OffloadStrategyType.Custom
            );
        }

        // Compute graph and CUDA context overhead buffer (MB)
        const int ComputeGraphOverheadMb = 2500;
        int availableVramMb = gpuInfo != null ? Math.Max(0, gpuInfo.FreeVramMb - CudaContextOverheadMb - ComputeGraphOverheadMb) : 0;

        // For FullGpu: check if full model fits in available VRAM, otherwise fallback to max fitting layers
        if (strategyType == OffloadStrategyType.FullGpu)
        {
            int estimatedFullVram = EstimateVramUsage(totalLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength) + ComputeGraphOverheadMb;
            if (gpuInfo == null || estimatedFullVram <= gpuInfo.FreeVramMb - CudaContextOverheadMb)
            {
                int recommendedBatchSize = 512;
                if (gpuInfo != null)
                {
                    if (gpuInfo.TotalVramMb >= 12000) recommendedBatchSize = 2048;
                    else if (gpuInfo.TotalVramMb >= 8000) recommendedBatchSize = 1024;
                }

                return new OffloadPlan(
                    GpuLayers: -1,
                    CpuLayers: 0,
                    EstimatedVramUsageMb: estimatedFullVram,
                    RecommendedContextSize: contextLength,
                    RecommendedBatchSize: recommendedBatchSize,
                    StrategyUsed: OffloadStrategyType.FullGpu
                );
            }
            // If full model exceeds VRAM, fall through to calculate max layers that safely fit
        }

        if (gpuInfo == null || availableVramMb <= 0)
        {
            // Fallback: if no VRAM info or zero headroom, offload 50% layers to GPU as safe fallback
            int safeLayers = Math.Max(1, totalLayers / 2);
            return new OffloadPlan(
                GpuLayers: safeLayers,
                CpuLayers: totalLayers - safeLayers,
                EstimatedVramUsageMb: EstimateVramUsage(safeLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength),
                RecommendedContextSize: contextLength,
                RecommendedBatchSize: 512,
                StrategyUsed: strategyType
            );
        }

        // Estimate maximum layers that fit in VRAM (weights + KV cache)
        double layerSizeMb = layerSizeBytes / 1048576.0;
        double kvCacheMbPerLayer = (kvCachePerLayerBytes * contextLength) / 1048576.0;
        double vramCostPerLayerMb = layerSizeMb + kvCacheMbPerLayer;
        
        if (vramCostPerLayerMb <= 0) vramCostPerLayerMb = 1;

        int maxLayersThatFit = (int)(availableVramMb / vramCostPerLayerMb);
        int targetGpuLayers = Math.Clamp(maxLayersThatFit, 1, totalLayers);

        // If all layers fit with room to spare, pass -1 to offload output head too
        int gpuLayersParam = (targetGpuLayers == totalLayers) ? -1 : targetGpuLayers;
        int finalEstimatedVram = EstimateVramUsage(targetGpuLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength);

        return new OffloadPlan(
            GpuLayers: gpuLayersParam,
            CpuLayers: totalLayers - targetGpuLayers,
            EstimatedVramUsageMb: finalEstimatedVram,
            RecommendedContextSize: contextLength,
            RecommendedBatchSize: 512,
            StrategyUsed: strategyType
        );
    }

    private int EstimateVramUsage(int gpuLayers, long layerSizeBytes, long kvCachePerLayerBytes, int contextLength)
    {
        long layersTotalSizeMb = (gpuLayers * layerSizeBytes) / (1024 * 1024);
        long kvCacheTotalMb = (gpuLayers * kvCachePerLayerBytes * contextLength) / (1024 * 1024);
        
        return (int)(layersTotalSizeMb + kvCacheTotalMb + CudaContextOverheadMb);
    }

    private int CalculateSafeContextSize(double availableMemoryMb, long kvCachePerLayerBytes, int totalLayers, int requestedContext)
    {
        // Safe context calculation for CPU-only fallback to avoid OOM
        long memoryForKvCacheBytes = (long)(availableMemoryMb * 1024 * 1024 * 0.5); // Reserve half for OS and model weights
        long kvCacheSizePerTokenBytes = totalLayers * kvCachePerLayerBytes;
        if (kvCacheSizePerTokenBytes <= 0) kvCacheSizePerTokenBytes = 1;

        int maxSafeContext = (int)(memoryForKvCacheBytes / kvCacheSizePerTokenBytes);
        return Math.Min(maxSafeContext, requestedContext);
    }
}
