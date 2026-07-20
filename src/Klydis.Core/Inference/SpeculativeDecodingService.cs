using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Klydis.Core.Hardware;
using Klydis.Core.Models;

namespace Klydis.Core.Inference;

/// <summary>
/// Result of draft model resolution for speculative decoding.
/// </summary>
public record SpeculativeResolutionResult(
    bool IsEnabled,
    string? DraftModelPath,
    string? DraftModelDisplayName,
    bool IsDualStream,
    string StatusMessage,
    OffloadPlan? DraftOffloadPlan);

/// <summary>
/// Evaluates local model registry and system VRAM to dynamically select
/// optimal draft models or configure dual-streaming for speculative decoding.
/// </summary>
public class SpeculativeDecodingService
{
    private readonly ModelRegistry _registry;
    private readonly GpuProfiler _gpuProfiler;
    private readonly SystemProfiler _systemProfiler;
    private readonly OffloadStrategy _offloadStrategy;
    private readonly ILogger<SpeculativeDecodingService>? _logger;

    public SpeculativeDecodingService(
        ModelRegistry registry,
        GpuProfiler gpuProfiler,
        SystemProfiler systemProfiler,
        OffloadStrategy offloadStrategy,
        ILogger<SpeculativeDecodingService>? logger = null)
    {
        _registry = registry;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the optimal speculative decoding configuration for a target model.
    /// </summary>
    public async Task<SpeculativeResolutionResult> ResolveDraftModelAsync(
        string targetModelPath,
        bool userEnabledSettings)
    {
        if (!userEnabledSettings)
        {
            return new SpeculativeResolutionResult(
                IsEnabled: false,
                DraftModelPath: null,
                DraftModelDisplayName: null,
                IsDualStream: false,
                StatusMessage: "Disabled by user settings.",
                DraftOffloadPlan: null);
        }

        var allModels = _registry.GetAllModels()
            .Where(m => File.Exists(m.FilePath))
            .ToList();

        if (allModels.Count == 0)
        {
            return new SpeculativeResolutionResult(
                IsEnabled: false,
                DraftModelPath: null,
                DraftModelDisplayName: null,
                IsDualStream: false,
                StatusMessage: "Disabled. No local models installed.",
                DraftOffloadPlan: null);
        }

        var targetModel = allModels.FirstOrDefault(m => m.FilePath.Equals(targetModelPath, StringComparison.OrdinalIgnoreCase))
            ?? new ModelInfo(
                Id: Guid.NewGuid().ToString(),
                DisplayName: Path.GetFileName(targetModelPath),
                FilePath: targetModelPath,
                FileName: Path.GetFileName(targetModelPath),
                FileSizeBytes: new FileInfo(targetModelPath).Length,
                Architecture: null,
                ParameterCount: null,
                QuantizationType: null,
                BlockCount: null,
                ContextLength: null,
                EstimatedVramMb: new FileInfo(targetModelPath).Length / (1024 * 1024),
                Source: ModelSource.Local,
                InstalledAt: DateTime.UtcNow,
                LastUsedAt: DateTime.UtcNow,
                ChecksumSha256: null,
                Role: null);

        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        var systemInfo = await _systemProfiler.GetSystemInfoAsync();

        // A valid draft model MUST be strictly smaller than the target model
        // AND must be lightweight (<= 2.0 GB) so it doesn't steal VRAM from the main model.
        const long MaxDraftModelSizeBytes = 2L * 1024 * 1024 * 1024; // 2.0 GB

        var validDraftCandidates = allModels
            .Where(m => !m.FilePath.Equals(targetModelPath, StringComparison.OrdinalIgnoreCase))
            .Where(m => m.FileSizeBytes < targetModel.FileSizeBytes)
            .Where(m => m.FileSizeBytes <= MaxDraftModelSizeBytes)
            .OrderBy(m => m.FileSizeBytes)
            .ToList();

        // Case A: Valid lightweight draft model available
        if (validDraftCandidates.Count > 0)
        {
            var smallestDraftModel = validDraftCandidates.First();

            // Calculate target model VRAM cost at native context
            var targetMetadata = GgufMetadataReader.Parse(targetModel.FilePath);
            int targetTotalLayers = targetMetadata?.BlockCount.HasValue == true && targetMetadata.BlockCount.Value > 0 
                ? (int)targetMetadata.BlockCount.Value : 32;
            long targetLayerSizeBytes = targetModel.FileSizeBytes / targetTotalLayers;

            var targetStandalonePlan = _offloadStrategy.CalculatePlan(
                targetTotalLayers,
                targetLayerSizeBytes,
                kvCachePerLayerBytes: 2048,
                contextLength: 4096,
                gpuInfo,
                systemInfo,
                OffloadStrategyType.FullGpu);

            // If target model fits 100% on GPU standalone (GpuLayers == -1), but attaching draft model pushes target layers to CPU,
            // disable speculative decoding to preserve 100% GPU execution for the primary model at full native context.
            long combinedVramMb = targetModel.EstimatedVramMb.GetValueOrDefault(targetModel.FileSizeBytes / (1024 * 1024)) +
                                 smallestDraftModel.EstimatedVramMb.GetValueOrDefault(smallestDraftModel.FileSizeBytes / (1024 * 1024)) + 1500;

            int availableVramMb = gpuInfo?.TotalVramMb ?? 0;

            if (targetStandalonePlan.GpuLayers == -1 && combinedVramMb > availableVramMb)
            {
                _logger?.LogInformation("Disabling speculative decoding to preserve 100% GPU offload for {TargetModel} at full native context.", targetModel.DisplayName);
                return new SpeculativeResolutionResult(
                    IsEnabled: false,
                    DraftModelPath: null,
                    DraftModelDisplayName: null,
                    IsDualStream: false,
                    StatusMessage: $"Disabled: Preserving 100% GPU offload for {targetModel.DisplayName} at full native context.",
                    DraftOffloadPlan: null);
            }

            var draftMetadata = GgufMetadataReader.Parse(smallestDraftModel.FilePath);
            int totalLayers = draftMetadata?.BlockCount.HasValue == true && draftMetadata.BlockCount.Value > 0 
                ? (int)draftMetadata.BlockCount.Value : 32;
            long layerSizeBytes = smallestDraftModel.FileSizeBytes / totalLayers;

            var draftPlan = _offloadStrategy.CalculatePlan(
                totalLayers,
                layerSizeBytes,
                kvCachePerLayerBytes: 2048,
                contextLength: 4096,
                gpuInfo,
                systemInfo,
                OffloadStrategyType.FullGpu);

            double sizeGb = smallestDraftModel.FileSizeBytes / (1024.0 * 1024.0 * 1024.0);
            string status = $"Enabled. Active Draft Model: {smallestDraftModel.DisplayName} ({sizeGb:F2} GB).";

            _logger?.LogInformation("Resolved speculative draft model {DraftModel} for target {TargetModel}", smallestDraftModel.DisplayName, targetModel.DisplayName);

            return new SpeculativeResolutionResult(
                IsEnabled: true,
                DraftModelPath: smallestDraftModel.FilePath,
                DraftModelDisplayName: smallestDraftModel.DisplayName,
                IsDualStream: false,
                StatusMessage: status,
                DraftOffloadPlan: draftPlan);
        }

        // Case B: Single model available -> Check if dual-streaming fits VRAM
        long singleModelVramMb = targetModel.EstimatedVramMb ?? (targetModel.FileSizeBytes / (1024 * 1024));
        long dualStreamVramMb = singleModelVramMb * 2;

        int availableGpuVramMb = gpuInfo?.TotalVramMb ?? 0;

        if (gpuInfo != null && dualStreamVramMb <= availableGpuVramMb)
        {
            var targetMetadata = GgufMetadataReader.Parse(targetModel.FilePath);
            int totalLayers = targetMetadata?.BlockCount.HasValue == true && targetMetadata.BlockCount.Value > 0 
                ? (int)targetMetadata.BlockCount.Value : 32;
            long layerSizeBytes = targetModel.FileSizeBytes / totalLayers;

            var dualPlan = _offloadStrategy.CalculatePlan(
                totalLayers,
                layerSizeBytes,
                kvCachePerLayerBytes: 2048,
                contextLength: 4096,
                gpuInfo,
                systemInfo,
                OffloadStrategyType.BalancedSplit);

            string status = $"Enabled (Dual-Stream). Running 2 instances of {targetModel.DisplayName} for decoding acceleration.";

            _logger?.LogInformation("Single model present; enabling dual-streaming for {ModelName}", targetModel.DisplayName);

            return new SpeculativeResolutionResult(
                IsEnabled: true,
                DraftModelPath: targetModel.FilePath,
                DraftModelDisplayName: targetModel.DisplayName,
                IsDualStream: true,
                StatusMessage: status,
                DraftOffloadPlan: dualPlan);
        }

        // Dual-stream or candidate models exceed VRAM/size limits
        string warningStatus = allModels.Count > 1
            ? $"Unavailable. Secondary installed models are too large (e.g. 9B) to serve as a draft model without stealing GPU VRAM from {targetModel.DisplayName}. Download a lightweight draft model (≤ 2 GB, e.g. Qwen2.5-0.5B)."
            : $"Unavailable. Only 1 model installed ({targetModel.DisplayName}) and dual-streaming exceeds available VRAM ({availableGpuVramMb} MB). Download a lightweight draft model (≤ 2 GB, e.g. Qwen2.5-0.5B).";

        _logger?.LogWarning("Speculative decoding unavailable for target model {ModelName}: no lightweight draft model (<= 2 GB) available.", targetModel.DisplayName);

        return new SpeculativeResolutionResult(
            IsEnabled: false,
            DraftModelPath: null,
            DraftModelDisplayName: null,
            IsDualStream: false,
            StatusMessage: warningStatus,
            DraftOffloadPlan: null);
    }
}
