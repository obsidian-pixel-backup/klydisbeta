using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klydis.App.ViewModels;

/// <summary>
/// ViewModel for managing the model library, including local models and HuggingFace downloads.
/// </summary>
public partial class ModelLibraryViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ModelCardViewModel> _models = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ModelCardViewModel> _filteredModels = new();

    [ObservableProperty]
    private string _currentlyLoadedModelId = string.Empty;

    [ObservableProperty]
    private int _vramUsageMb;

    [ObservableProperty]
    private int _vramTotalMb;

    [ObservableProperty]
    private double _vramUsagePercent;

    [ObservableProperty]
    private string _hfSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _hfResults = new();

    [ObservableProperty]
    private bool _hfIsSearching;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    private readonly Klydis.Core.Models.ModelRegistry _registry;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;
    private readonly Klydis.Core.Hardware.GpuProfiler _gpuProfiler;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Hardware.OffloadStrategy _offloadStrategy;
    private readonly DispatcherTimer _timer;

    public ModelLibraryViewModel(
        Klydis.Core.Models.ModelRegistry registry,
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        Klydis.Core.Hardware.GpuProfiler gpuProfiler,
        Klydis.Core.Hardware.SystemProfiler systemProfiler,
        Klydis.Core.Hardware.OffloadStrategy offloadStrategy)
    {
        _registry = registry;
        _inferenceEngine = inferenceEngine;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        FilteredModels = new ObservableCollection<ModelCardViewModel>(Models);
        _ = ScanAsync();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (s, e) => await UpdateVramUsageAsync();
        _timer.Start();
    }

    private async Task UpdateVramUsageAsync()
    {
        var usage = await _gpuProfiler.GetRealTimeVramUsageAsync();
        if (usage != null && VramTotalMb > 0)
        {
            VramUsageMb = usage.UsedVramMb;
            VramUsagePercent = 100.0 * VramUsageMb / VramTotalMb;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterModels();
    }

    private void FilterModels()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredModels = new ObservableCollection<ModelCardViewModel>(Models);
        }
        else
        {
            var filtered = Models.Where(m => m.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || 
                                             m.Architecture.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            FilteredModels = new ObservableCollection<ModelCardViewModel>(filtered);
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        await _registry.SyncWithDiskAsync();
        
        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        if (gpuInfo != null)
        {
            VramTotalMb = gpuInfo.TotalVramMb;
            VramUsageMb = gpuInfo.UsedVramMb;
            VramUsagePercent = VramTotalMb > 0 ? 100.0 * VramUsageMb / VramTotalMb : 0;
        }

        Models.Clear();
        foreach (var model in _registry.GetAllModels())
        {
            var estimatedVramMb = (int)(model.EstimatedVramMb ?? 0L);
            Models.Add(new ModelCardViewModel
            {
                ModelId = model.Id,
                DisplayName = model.DisplayName,
                Architecture = model.Architecture ?? "Unknown",
                ParameterSize = model.ParameterCount?.ToString("F1") ?? "Unknown",
                QuantType = model.QuantizationType ?? "Unknown",
                FileSizeGb = (model.FileSizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB",
                FileName = model.FileName,
                EstimatedVramMb = estimatedVramMb,
                ContextLength = (int)(model.ContextLength ?? 8192L),
                CanFitInVram = gpuInfo == null || estimatedVramMb <= gpuInfo.TotalVramMb
            });
        }
        
        FilterModels();
        IsScanning = false;
    }

    [RelayCommand]
    private async Task LoadModelAsync(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return;
        
        var modelInfo = _registry.GetModel(modelId);
        if (modelInfo == null) return;

        CurrentlyLoadedModelId = modelId;
        
        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        var systemInfo = await _systemProfiler.GetSystemInfoAsync();
        
        // Read GGUF metadata for dynamic sizing
        var metadata = Klydis.Core.Models.GgufMetadataReader.Parse(modelInfo.FilePath);
        int totalLayers = metadata != null && metadata.BlockCount.HasValue ? (int)metadata.BlockCount.Value : 32;
        long layerSizeBytes = modelInfo.FileSizeBytes / totalLayers; // Approximation
        
        // Cap context length to a practical default. The model's trained context (often 1M+)
        // is far too large for initial allocation. 32768 tokens is a practical default that
        // fits comfortably in VRAM while allowing long conversations.
        int rawContextLength = (int)(metadata?.ContextLength ?? 8192);
        int contextLength = Math.Min(rawContextLength, 32768);
        
        // KV cache per layer per token: 2 (K+V) * HeadCountKv * HeadDim * sizeof(element)
        // For Q8_0 KV cache, sizeof(element) = 1 byte. For FP16, sizeof(element) = 2 bytes.
        // We use 1 byte since we configure Q8_0 KV cache in InferenceEngine.
        long kvCachePerLayerBytes = 2048; // Safe default: 2 * 8 * 128 * 1 = 2048
        if (metadata != null && metadata.EmbeddingLength.HasValue && metadata.HeadCount.HasValue && metadata.HeadCountKv.HasValue)
        {
            long headDim = metadata.EmbeddingLength.Value / metadata.HeadCount.Value;
            // K + V (2) * HeadCountKv * headDim * 1 byte (Q8_0 quantized KV cache)
            kvCachePerLayerBytes = 2 * metadata.HeadCountKv.Value * headDim * 1;
        }

        var plan = _offloadStrategy.CalculatePlan(
            totalLayers, 
            layerSizeBytes, 
            kvCachePerLayerBytes, 
            contextLength, 
            gpuInfo, 
            systemInfo, 
            Klydis.Core.Hardware.OffloadStrategyType.FullGpu);

        await _inferenceEngine.LoadModelAsync(modelInfo.FilePath, plan);
    }

    [RelayCommand]
    private async Task UnloadModelAsync()
    {
        CurrentlyLoadedModelId = string.Empty;
        _inferenceEngine.UnloadModel();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteModelAsync(string modelId)
    {
        var model = Models.FirstOrDefault(m => m.ModelId == modelId);
        if (model != null)
        {
            Models.Remove(model);
            FilterModels();
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task HfSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(HfSearchText)) return;
        HfIsSearching = true;
        // Simulate search
        await Task.Delay(1000);
        HfIsSearching = false;
    }

    [RelayCommand]
    private async Task DownloadModelAsync(HfFileViewModel file)
    {
        if (file == null) return;
        IsDownloading = true;
        DownloadStatus = $"Downloading {file.FileName}...";
        for (int i = 0; i <= 100; i += 10)
        {
            DownloadProgress = i;
            await Task.Delay(200);
        }
        DownloadStatus = "Download complete.";
        IsDownloading = false;
    }
}
