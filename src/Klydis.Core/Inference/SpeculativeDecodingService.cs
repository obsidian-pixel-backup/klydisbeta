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
        bool userEnabledSettings,
        string? selectedDraftModelPath = "auto")
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
                FileSizeBytes: File.Exists(targetModelPath) ? new FileInfo(targetModelPath).Length : 0,
                Architecture: null,
                ParameterCount: null,
                QuantizationType: null,
                BlockCount: null,
                ContextLength: null,
                EstimatedVramMb: File.Exists(targetModelPath) ? new FileInfo(targetModelPath).Length / (1024 * 1024) : 0,
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

        bool isAuto = string.IsNullOrWhiteSpace(selectedDraftModelPath) ||
                      selectedDraftModelPath.Equals("auto", StringComparison.OrdinalIgnoreCase);

        ModelInfo? selectedDraftModel = null;

        if (!isAuto && selectedDraftModelPath != null)
        {
            var manualCandidate = allModels.FirstOrDefault(m => m.FilePath.Equals(selectedDraftModelPath, StringComparison.OrdinalIgnoreCase));
            if (manualCandidate != null &&
                File.Exists(manualCandidate.FilePath) &&
                !manualCandidate.FilePath.Equals(targetModelPath, StringComparison.OrdinalIgnoreCase) &&
                manualCandidate.FileSizeBytes < targetModel.FileSizeBytes)
            {
                selectedDraftModel = manualCandidate;
            }
            else
            {
                _logger?.LogWarning("Manually selected draft model path '{SelectedPath}' is invalid or larger than target model. Falling back to auto selection.", selectedDraftModelPath);
            }
        }

        if (selectedDraftModel == null)
        {
            var validDraftCandidates = allModels
                .Where(m => !m.FilePath.Equals(targetModelPath, StringComparison.OrdinalIgnoreCase))
                .Where(m => m.FileSizeBytes < targetModel.FileSizeBytes)
                .Where(m => m.FileSizeBytes <= MaxDraftModelSizeBytes)
                .OrderByDescending(m => Is4BitQuant(m.QuantizationType, m.FilePath))
                .ThenBy(m => m.FileSizeBytes)
                .ToList();

            if (validDraftCandidates.Count > 0)
            {
                selectedDraftModel = validDraftCandidates.First();
            }
        }

        // Case A: Valid lightweight draft model available
        if (selectedDraftModel != null)
        {
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
                                 selectedDraftModel.EstimatedVramMb.GetValueOrDefault(selectedDraftModel.FileSizeBytes / (1024 * 1024)) + 1500;

            int availableVramMb = gpuInfo?.TotalVramMb ?? 0;

            if (targetStandalonePlan.GpuLayers == -1 && combinedVramMb > availableVramMb)
            {
                _logger?.LogInformation("Switching to zero-VRAM N-gram fallback speculative decoding to preserve 100% GPU offload for {TargetModel}.", targetModel.DisplayName);
                return new SpeculativeResolutionResult(
                    IsEnabled: true,
                    DraftModelPath: null,
                    DraftModelDisplayName: "Zero-VRAM N-Gram Lookup",
                    IsDualStream: false,
                    StatusMessage: $"Enabled (Zero-VRAM N-Gram Fallback). Preserving 100% GPU offload for {targetModel.DisplayName}.",
                    DraftOffloadPlan: null);
            }

            var draftMetadata = GgufMetadataReader.Parse(selectedDraftModel.FilePath);
            int totalLayers = draftMetadata?.BlockCount.HasValue == true && draftMetadata.BlockCount.Value > 0 
                ? (int)draftMetadata.BlockCount.Value : 32;
            long layerSizeBytes = selectedDraftModel.FileSizeBytes / totalLayers;

            var draftPlan = _offloadStrategy.CalculatePlan(
                totalLayers,
                layerSizeBytes,
                kvCachePerLayerBytes: 2048,
                contextLength: 4096,
                gpuInfo,
                systemInfo,
                OffloadStrategyType.FullGpu);

            double sizeGb = selectedDraftModel.FileSizeBytes / (1024.0 * 1024.0 * 1024.0);
            string modeDesc = isAuto ? "Auto" : "Manual";
            string status = $"Enabled ({modeDesc}). Active Draft Model: {selectedDraftModel.DisplayName} ({sizeGb:F2} GB).";

            _logger?.LogInformation("Resolved speculative draft model {DraftModel} ({Mode}) for target {TargetModel}", selectedDraftModel.DisplayName, modeDesc, targetModel.DisplayName);

            return new SpeculativeResolutionResult(
                IsEnabled: true,
                DraftModelPath: selectedDraftModel.FilePath,
                DraftModelDisplayName: selectedDraftModel.DisplayName,
                IsDualStream: false,
                StatusMessage: status,
                DraftOffloadPlan: draftPlan);
        }

        // Case B: No valid lightweight GGUF draft model available. Enable Zero-VRAM N-Gram Prompt Lookup fallback.
        string fallbackStatus = $"Enabled (Zero-VRAM N-Gram Fallback). Accelerating {targetModel.DisplayName} via prompt sequence lookup.";

        _logger?.LogInformation("Speculative decoding using Zero-VRAM N-Gram lookup fallback for target model {ModelName}.", targetModel.DisplayName);

        return new SpeculativeResolutionResult(
            IsEnabled: true,
            DraftModelPath: null,
            DraftModelDisplayName: "Zero-VRAM N-Gram Lookup",
            IsDualStream: false,
            StatusMessage: fallbackStatus,
            DraftOffloadPlan: null);
    }

    private static bool Is4BitQuant(string? quant, string? filePath = null)
    {
        string text = quant ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = !string.IsNullOrEmpty(filePath) ? Path.GetFileName(filePath) : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text)) return false;

        return text.Contains("Q4", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("4_K", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("4_0", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("IQ4", StringComparison.OrdinalIgnoreCase);
    }
}
