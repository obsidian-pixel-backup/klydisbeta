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

        // Dynamically measure free GPU VRAM, accounting for temporary VRAM reclamation lag during model switches
        int totalVramMb = gpuInfo?.TotalVramMb ?? 0;
        int reportedFreeVramMb = gpuInfo?.FreeVramMb ?? 0;

        // Respect actual free VRAM, using a safe ceiling (90% of total VRAM) for system headroom.
        // If reported free VRAM is lower than 90% total VRAM (e.g. during model switching or high GPU usage),
        // use reported free VRAM to avoid forcing full offload and causing CUDA OOM crashes.
        int maxUsableVramMb = totalVramMb > 0 ? (int)(totalVramMb * 0.90) : reportedFreeVramMb;
        int usableVramMb = (reportedFreeVramMb > 0)
            ? Math.Min(maxUsableVramMb, reportedFreeVramMb)
            : maxUsableVramMb;

        // Tightened driver context reservation (200MB)
        int driverOverheadMb = 200;

        // Tightened compute graph workspace buffer for single-sequence inference (150MB)
        int computeGraphMb = 150;

        int netAvailableVramMb = Math.Max(0, usableVramMb - driverOverheadMb - computeGraphMb);

        double layerSizeMb = layerSizeBytes / 1048576.0;
        if (layerSizeMb <= 0) layerSizeMb = 1.0;

        // Non-layer weight overhead (embedding table + output head: ~10% of total layer weights)
        double nonLayerWeightsMb = (totalLayers * layerSizeMb * 0.10);

        // Calculate KV cache footprint at full native requested context length
        double totalKvCacheMb = (totalLayers * kvCachePerLayerBytes * contextLength) / 1048576.0;
        double fullModelVramCostMb = (totalLayers * layerSizeMb) + totalKvCacheMb + nonLayerWeightsMb;

        int recommendedContext = Math.Max(32768, contextLength);
        int targetGpuLayers;

        // Check if full model fits in net available VRAM (or usable VRAM headroom)
        if (netAvailableVramMb > 0 && fullModelVramCostMb <= netAvailableVramMb)
        {
            targetGpuLayers = totalLayers;
        }
        else if (strategyType == OffloadStrategyType.FullGpu && netAvailableVramMb > 0 && fullModelVramCostMb <= (netAvailableVramMb + 100))
        {
            targetGpuLayers = totalLayers;
        }
        else if (netAvailableVramMb > 0)
        {
            // Calculate maximum layers that fit in available VRAM at requested context
            double vramPerLayerWithKvMb = layerSizeMb + ((kvCachePerLayerBytes * recommendedContext) / 1048576.0);
            double availableForLayers = Math.Max(0, netAvailableVramMb - nonLayerWeightsMb);
            targetGpuLayers = Math.Clamp((int)(availableForLayers / Math.Max(1.0, vramPerLayerWithKvMb)), 0, totalLayers);

            if (targetGpuLayers == 0)
            {
                recommendedContext = CalculateSafeContextSize(systemInfo.AvailableRamGb * 1024, kvCachePerLayerBytes, totalLayers, contextLength);
            }
        }
        else
        {
            targetGpuLayers = 0;
            recommendedContext = CalculateSafeContextSize(systemInfo.AvailableRamGb * 1024, kvCachePerLayerBytes, totalLayers, contextLength);
        }

        int gpuLayersParam = targetGpuLayers;
        int finalEstimatedVram = EstimateVramUsage(targetGpuLayers, layerSizeBytes, kvCachePerLayerBytes, recommendedContext);

        int recommendedBatchSize = 512;
        if (totalVramMb >= 12000) recommendedBatchSize = 1024;

        return new OffloadPlan(
            GpuLayers: gpuLayersParam,
            CpuLayers: totalLayers - targetGpuLayers,
            EstimatedVramUsageMb: finalEstimatedVram,
            RecommendedContextSize: recommendedContext,
            RecommendedBatchSize: recommendedBatchSize,
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
        const int MIN_CONTEXT_SIZE = 32768; // Minimum context limit must be 32k, never smaller

        long memoryForKvCacheBytes = (long)(availableMemoryMb * 1024 * 1024 * 0.5); // Reserve half for OS and weights
        long kvCacheSizePerTokenBytes = totalLayers * kvCachePerLayerBytes;
        if (kvCacheSizePerTokenBytes <= 0) kvCacheSizePerTokenBytes = 1;

        int maxMemoryContext = (int)(memoryForKvCacheBytes / kvCacheSizePerTokenBytes);
        int targetContext = Math.Max(requestedContext, MIN_CONTEXT_SIZE);

        return Math.Max(MIN_CONTEXT_SIZE, Math.Min(maxMemoryContext, Math.Max(targetContext, MIN_CONTEXT_SIZE)));
    }
}
