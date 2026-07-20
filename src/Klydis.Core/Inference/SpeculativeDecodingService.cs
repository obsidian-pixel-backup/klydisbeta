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

        var otherModels = allModels
            .Where(m => !m.FilePath.Equals(targetModelPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.FileSizeBytes)
            .ToList();

        // Case A: Multiple local models available -> Pick smallest candidate
        if (otherModels.Count > 0)
        {
            var smallestDraftModel = otherModels.First();
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

        // Dual-stream exceeds VRAM limit
        string warningStatus = $"Unavailable. Only 1 model installed ({targetModel.DisplayName}) and dual-streaming exceeds available VRAM ({availableGpuVramMb} MB). Download a smaller draft model (e.g. Qwen2.5-0.5B).";

        _logger?.LogWarning("Speculative decoding unavailable for single model {ModelName}: dual-stream requires {DualVram} MB VRAM, available {TotalVram} MB",
            targetModel.DisplayName, dualStreamVramMb, availableGpuVramMb);

        return new SpeculativeResolutionResult(
            IsEnabled: false,
            DraftModelPath: null,
            DraftModelDisplayName: null,
            IsDualStream: false,
            StatusMessage: warningStatus,
            DraftOffloadPlan: null);
    }
}
