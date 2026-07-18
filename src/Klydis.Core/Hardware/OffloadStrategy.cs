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
        if (gpuInfo == null || strategyType == OffloadStrategyType.CpuOnly)
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

        // Available VRAM calculation based on strategy
        int availableVramMb = gpuInfo.FreeVramMb - CudaContextOverheadMb;
        
        if (strategyType == OffloadStrategyType.BalancedSplit)
        {
            availableVramMb -= BalancedBufferMb;
        }

        if (availableVramMb <= 0)
        {
            // Fallback to CPU if no VRAM is usable
            return CalculatePlan(totalLayers, layerSizeBytes, kvCachePerLayerBytes, contextLength, gpuInfo, systemInfo, OffloadStrategyType.CpuOnly);
        }

        int targetGpuLayers;
        if (strategyType == OffloadStrategyType.FullGpu)
        {
            // For FullGpu, use sentinel value -1 to tell llama.cpp to offload ALL layers
            // including the output/embedding head. This is the most reliable approach.
            // Only fall back to partial offload if the model file is massive (>90% of total VRAM).
            long weightsSizeMb = (totalLayers * layerSizeBytes) / (1024 * 1024);
            long totalVramThreshold = (long)(gpuInfo.TotalVramMb * 0.90);
            if (weightsSizeMb < totalVramThreshold)
            {
                // Model fits comfortably — offload everything using sentinel
                targetGpuLayers = -1;
            }
            else
            {
                // Model is very large — do best-effort partial offload
                long layerSizeMb = layerSizeBytes / (1024 * 1024);
                if (layerSizeMb == 0) layerSizeMb = 1;
                targetGpuLayers = Math.Clamp((int)(availableVramMb / layerSizeMb), 0, totalLayers);
            }
        }
        else
        {
            // Estimate maximum layers that fit in VRAM (weights + KV cache)
            double layerSizeMb = layerSizeBytes / 1048576.0;
            double kvCacheMbPerLayer = (kvCachePerLayerBytes * contextLength) / 1048576.0;
            double vramCostPerLayerMb = layerSizeMb + kvCacheMbPerLayer;
            
            if (vramCostPerLayerMb <= 0) vramCostPerLayerMb = 1;

            int maxLayersThatFit = (int)(availableVramMb / vramCostPerLayerMb);
            targetGpuLayers = Math.Min(maxLayersThatFit, totalLayers);
        }

        // For sentinel value -1, estimate VRAM as if all layers are offloaded
        int layersForEstimate = targetGpuLayers == -1 ? totalLayers : targetGpuLayers;
        int finalEstimatedVram = EstimateVramUsage(layersForEstimate, layerSizeBytes, kvCachePerLayerBytes, contextLength);

        // Batch size recommendation: 2048 for full GPU on ≥12GB cards, 1024 for ≥8GB, 512 otherwise
        int recommendedBatchSize;
        bool isFullGpuOffload = targetGpuLayers == -1 || targetGpuLayers >= totalLayers;
        if (isFullGpuOffload && gpuInfo.TotalVramMb >= 12000)
            recommendedBatchSize = 2048;
        else if (isFullGpuOffload && gpuInfo.TotalVramMb >= 8000)
            recommendedBatchSize = 1024;
        else
            recommendedBatchSize = 512;

        // CpuLayers is 0 when using sentinel -1 (everything on GPU)
        int cpuLayers = targetGpuLayers == -1 ? 0 : totalLayers - targetGpuLayers;

        return new OffloadPlan(
            GpuLayers: targetGpuLayers,
            CpuLayers: cpuLayers,
            EstimatedVramUsageMb: finalEstimatedVram,
            RecommendedContextSize: contextLength,
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
        // Safe context calculation for CPU-only fallback to avoid OOM
        long memoryForKvCacheMb = (long)(availableMemoryMb * 0.5); // Reserve half for OS and model weights
        long kvCacheSizePerTokenMb = (totalLayers * kvCachePerLayerBytes) / (1024 * 1024);
        if (kvCacheSizePerTokenMb == 0) kvCacheSizePerTokenMb = 1;

        int maxSafeContext = (int)(memoryForKvCacheMb / kvCacheSizePerTokenMb);
        return Math.Min(maxSafeContext, requestedContext);
    }
}
