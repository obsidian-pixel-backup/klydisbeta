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

        bool isCudaAvailable = System.IO.File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll"));

        if ((gpuInfo == null || totalVramMb <= 4096) && isCudaAvailable && strategyType == OffloadStrategyType.FullGpu)
        {
            // Default to assuming 16GB VRAM on CUDA systems if profiler returned null, 0, or 4GB WMI saturation limit
            totalVramMb = 16384;
            reportedFreeVramMb = 15000;
        }

        // Determine usable VRAM ceiling based on chosen offload strategy
        int usableVramMb;
        if (strategyType == OffloadStrategyType.FullGpu)
        {
            // Full GPU strategy targets up to 98% of total VRAM (reserving ~300 MB for driver/display context)
            int vramCeilingMb = totalVramMb > 0 ? (int)(totalVramMb * 0.98) : reportedFreeVramMb;
            // Account for previous model cleanup lag by allowing full VRAM ceiling if free VRAM reported is low right before load
            usableVramMb = (totalVramMb > 0) ? Math.Max(reportedFreeVramMb, vramCeilingMb) : reportedFreeVramMb;
        }
        else
        {
            // Balanced split leaves ~10% headroom (or min 1000MB) for system/other applications
            int vramCeilingMb = totalVramMb > 0 ? (int)(totalVramMb * 0.90) : reportedFreeVramMb;
            usableVramMb = reportedFreeVramMb > 0 ? Math.Min(vramCeilingMb, reportedFreeVramMb) : vramCeilingMb;
        }

        int driverOverheadMb = 250;
        int netAvailableVramMb = Math.Max(0, usableVramMb - driverOverheadMb);

        double totalModelSizeMb = (totalLayers * layerSizeBytes) / 1048576.0;
        double layerSizeMb = totalLayers > 0 ? totalModelSizeMb / totalLayers : 1.0;

        // Target requested context length (clamped to 2,048 to 131,072)
        int desiredContext = Math.Clamp(contextLength, 2048, 131072);

        // Dynamically calculate max context that fits in VRAM headroom (reserving 15% VRAM headroom for CUDA L2 cache, graph execution & OS display)
        double targetMaxVramMb = totalVramMb > 0 ? (totalVramMb * 0.85) : Math.Max(4000, netAvailableVramMb);
        double availableForKvCacheMb = Math.Max(500, targetMaxVramMb - totalModelSizeMb - CudaContextOverheadMb);
        double kvCacheBytesPerTokenAllLayers = totalLayers * kvCachePerLayerBytes;
        int safeVramContext = kvCacheBytesPerTokenAllLayers > 0
            ? (int)((availableForKvCacheMb * 1048576.0) / kvCacheBytesPerTokenAllLayers)
            : 32768;

        // Clamp recommended context based on GPU VRAM ceiling (min 2,048 to max 131,072)
        int recommendedContext = Math.Clamp(Math.Min(desiredContext, safeVramContext), 2048, 131072);
        if (totalVramMb > 0 && totalVramMb <= 16384 && recommendedContext > 32768)
        {
            // On 16GB GPUs, target 32,768 (32K) tokens to keep VRAM at ~9.5 GB (60% saturation) for peak 60+ tok/s generation throughput
            recommendedContext = 32768;
        }

        // Calculate KV cache per layer at target recommended context length
        double kvCacheMbPerLayer = (kvCachePerLayerBytes * recommendedContext) / 1048576.0;
        double vramCostPerLayerMb = layerSizeMb + kvCacheMbPerLayer;

        // Full model cost = total weights + total KV cache + CUDA driver context overhead
        double totalKvCacheMb = totalLayers * kvCacheMbPerLayer;
        double fullModelVramCostMb = totalModelSizeMb + totalKvCacheMb + CudaContextOverheadMb;

        int targetGpuLayers;

        if (netAvailableVramMb > 0 && (fullModelVramCostMb <= netAvailableVramMb || totalVramMb >= 8000 || strategyType == OffloadStrategyType.FullGpu))
        {
            // 100% of layers fit into GPU VRAM (or hardware VRAM >= 8GB / FullGpu strategy)
            targetGpuLayers = totalLayers;
        }
        else if (netAvailableVramMb > 0)
        {
            // Calculate max layers that fit in available VRAM at exact requested context length
            double availableForLayers = Math.Max(0, netAvailableVramMb - CudaContextOverheadMb);
            targetGpuLayers = Math.Clamp((int)(availableForLayers / Math.Max(1.0, vramCostPerLayerMb)), 0, totalLayers);

            if (targetGpuLayers == 0)
            {
                recommendedContext = CalculateSafeContextSize(systemInfo.AvailableRamGb * 1024, kvCachePerLayerBytes, totalLayers, recommendedContext);
            }
        }
        else
        {
            targetGpuLayers = 0;
            recommendedContext = CalculateSafeContextSize(systemInfo.AvailableRamGb * 1024, kvCachePerLayerBytes, totalLayers, recommendedContext);
        }

        int gpuLayersParam = targetGpuLayers;
        int finalEstimatedVram = EstimateVramUsage(targetGpuLayers, layerSizeBytes, kvCachePerLayerBytes, recommendedContext);

        int recommendedBatchSize = 512;
        if (totalVramMb >= 8000 || strategyType == OffloadStrategyType.FullGpu) recommendedBatchSize = 2048;
        else if (totalVramMb >= 6000) recommendedBatchSize = 1024;

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
        const int MIN_CONTEXT_SIZE = 65536; // 64K tokens baseline context floor for system prompt & chat context

        long memoryForKvCacheBytes = (long)(availableMemoryMb * 1024 * 1024 * 0.5); // Reserve half for OS and weights
        long kvCacheSizePerTokenBytes = totalLayers * kvCachePerLayerBytes;
        if (kvCacheSizePerTokenBytes <= 0) kvCacheSizePerTokenBytes = 1;

        int maxMemoryContext = (int)(memoryForKvCacheBytes / kvCacheSizePerTokenBytes);
        int targetContext = Math.Max(requestedContext, MIN_CONTEXT_SIZE);

        return Math.Max(MIN_CONTEXT_SIZE, Math.Min(maxMemoryContext, Math.Max(targetContext, MIN_CONTEXT_SIZE)));
    }
}
