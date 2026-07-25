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
    private ObservableCollection<ActiveDownloadViewModel> _activeDownloads = new();

    public bool HasActiveDownloads => ActiveDownloads.Count > 0;

    [ObservableProperty]
    private double _totalDownloadProgress;

    private void UpdateTotalDownloadProgress()
    {
        if (ActiveDownloads.Count == 0)
        {
            TotalDownloadProgress = 0;
            return;
        }

        double total = 0;
        foreach (var download in ActiveDownloads)
        {
            total += download.Progress;
        }
        TotalDownloadProgress = total / ActiveDownloads.Count;
    }

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _popularModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _newestModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _highestRatedModels = new();

    [ObservableProperty]
    private bool _filterVisionOnly;

    [ObservableProperty]
    private bool _filterThinkingOnly;

    [ObservableProperty]
    private string _selectedSizeFilter = "All Sizes";

    [ObservableProperty]
    private ObservableCollection<string> _availableSizeFilters = new() { "All Sizes", "< 7B", "7B - 14B", "14B - 35B", "> 35B" };

    [ObservableProperty]
    private string _selectedRoleFilter = "All Roles";


    [ObservableProperty]
    private ObservableCollection<string> _availableRoleFilters = new() { "All Roles", "Chat", "Code", "Instruct", "Vision", "Researcher", "UI Designer", "None" };

    [ObservableProperty]
    private ModelCardViewModel? _selectedLocalModel;

    partial void OnSelectedLocalModelChanged(ModelCardViewModel? value)
    {
        if (value != null && !value.IsLoaded)
        {
            _ = LoadModelAsync(value.ModelId);
        }
    }

    partial void OnFilterVisionOnlyChanged(bool value) { FilterModels(); ApplyHfFilters(); }
    partial void OnFilterThinkingOnlyChanged(bool value) { FilterModels(); ApplyHfFilters(); }
    partial void OnSelectedSizeFilterChanged(string value) { FilterModels(); }
    partial void OnSelectedRoleFilterChanged(string value) { FilterModels(); }

    private void ApplyHfFilters()
    {
        var view1 = System.Windows.Data.CollectionViewSource.GetDefaultView(HfResults);
        var view2 = System.Windows.Data.CollectionViewSource.GetDefaultView(PopularModels);
        var view3 = System.Windows.Data.CollectionViewSource.GetDefaultView(NewestModels);
        var view4 = System.Windows.Data.CollectionViewSource.GetDefaultView(HighestRatedModels);

        Predicate<object> filter = (obj) =>
        {
            if (obj is HfModelCardViewModel card)
            {
                if (FilterVisionOnly && !card.IsVision) return false;
                if (FilterThinkingOnly && !card.IsThinking) return false;
                return true;
            }
            return false;
        };

        if (view1 != null) { view1.Filter = filter; view1.Refresh(); }
        if (view2 != null) { view2.Filter = filter; view2.Refresh(); }
        if (view3 != null) { view3.Filter = filter; view3.Refresh(); }
        if (view4 != null) { view4.Filter = filter; view4.Refresh(); }
    }

    private readonly Klydis.Core.Models.ModelRegistry _registry;
    private readonly Klydis.Core.Models.HuggingFaceClient _hfClient;
    private readonly Klydis.Core.Models.ModelQuantizerService? _quantizerService;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;
    private readonly Klydis.Core.Hardware.GpuProfiler _gpuProfiler;
    private readonly Klydis.Core.Hardware.SystemProfiler _systemProfiler;
    private readonly Klydis.Core.Hardware.OffloadStrategy _offloadStrategy;
    private readonly DispatcherTimer _timer;
    private System.Threading.CancellationTokenSource? _modelLoadCts;
    private long _modelLoadSequenceId = 0;

    public ModelLibraryViewModel(
        Klydis.Core.Models.ModelRegistry registry,
        Klydis.Core.Models.HuggingFaceClient hfClient,
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        Klydis.Core.Hardware.GpuProfiler gpuProfiler,
        Klydis.Core.Hardware.SystemProfiler systemProfiler,
        Klydis.Core.Hardware.OffloadStrategy offloadStrategy,
        Klydis.Core.Models.ModelQuantizerService? quantizerService = null)
    {
        _registry = registry;
        _hfClient = hfClient;
        _quantizerService = quantizerService;
        _inferenceEngine = inferenceEngine;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;
        FilteredModels = new ObservableCollection<ModelCardViewModel>(Models);
        
        _registry.RegistryChanged += OnRegistryChanged;
        _inferenceEngine.ModelStateChanged += OnModelStateChanged;
        
        _ = ScanAsync();
        _ = LoadHfModelsAsync();
        _ = ResumeActiveDownloadsAsync();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (s, e) => await UpdateVramUsageAsync();
        _timer.Start();
    }

    private void OnModelStateChanged(bool isLoaded, string? modelPath)
    {
        Action updateUi = () =>
        {
            if (isLoaded && !string.IsNullOrEmpty(modelPath))
            {
                var modelInfo = _registry.GetAllModels().FirstOrDefault(m => m.FilePath == modelPath);
                string loadedId = modelInfo?.Id ?? string.Empty;
                CurrentlyLoadedModelId = loadedId;
                foreach (var m in Models)
                {
                    m.IsLoaded = (m.ModelId == loadedId);
                }
            }
            else if (!isLoaded)
            {
                CurrentlyLoadedModelId = string.Empty;
                foreach (var m in Models)
                {
                    m.IsLoaded = false;
                }
            }
        };

        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(updateUi);
        }
        else
        {
            updateUi();
        }
    }

    private void OnRegistryChanged()
    {
        if (System.Windows.Application.Current != null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => { _ = PopulateModelsAsync(); });
        }
    }

    private async Task ResumeActiveDownloadsAsync()
    {
        var activeDownloads = await _registry.GetActiveDownloadsAsync();
        foreach (var download in activeDownloads)
        {
            _ = StartDownloadAsync(download.RepoId, download.FileName, download.DestinationPath);
        }
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
        var query = Models.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(m => m.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || 
                                     m.Architecture.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        if (FilterVisionOnly) query = query.Where(m => m.IsVision);
        if (FilterThinkingOnly) query = query.Where(m => m.IsThinking);

        if (SelectedRoleFilter != "All Roles")
        {
            query = query.Where(m => string.Equals(m.Role, SelectedRoleFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedSizeFilter != "All Sizes")
        {
            query = query.Where(m => 
            {
                if (!double.TryParse(m.ParameterSize, out double size)) return false;
                return SelectedSizeFilter switch
                {
                    "< 7B" => size < 7,
                    "7B - 14B" => size >= 7 && size <= 14,
                    "14B - 35B" => size > 14 && size <= 35,
                    "> 35B" => size > 35,
                    _ => true
                };
            });
        }

        FilteredModels = new ObservableCollection<ModelCardViewModel>(query);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        await _registry.SyncWithDiskAsync();
        await PopulateModelsAsync();
        IsScanning = false;
    }

    private async Task PopulateModelsAsync()
    {
        
        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        if (gpuInfo != null)
        {
            VramTotalMb = gpuInfo.TotalVramMb;
            VramUsageMb = gpuInfo.UsedVramMb;
            VramUsagePercent = VramTotalMb > 0 ? 100.0 * VramUsageMb / VramTotalMb : 0;
        }

        foreach (var existingModel in Models)
        {
            existingModel.PropertyChanged -= ModelCard_PropertyChanged;
        }
        Models.Clear();
        foreach (var model in _registry.GetAllModels())
        {
            var estimatedVramMb = (int)(model.EstimatedVramMb ?? 0L);
            bool isVision = model.DisplayName.Contains("vision", StringComparison.OrdinalIgnoreCase) || 
                            model.DisplayName.Contains("llava", StringComparison.OrdinalIgnoreCase) || 
                            model.DisplayName.Contains("pixtral", StringComparison.OrdinalIgnoreCase) ||
                            model.DisplayName.Contains("qwen-vl", StringComparison.OrdinalIgnoreCase) ||
                            (model.Architecture != null && (
                                model.Architecture.Contains("clip", StringComparison.OrdinalIgnoreCase) || 
                                model.Architecture.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
                                model.Architecture.Contains("qwen2vl", StringComparison.OrdinalIgnoreCase) ||
                                model.Architecture.Contains("mllama", StringComparison.OrdinalIgnoreCase)
                            ));

            bool isThinking = model.DisplayName.Contains("think", StringComparison.OrdinalIgnoreCase) || 
                              model.DisplayName.Contains("-r1", StringComparison.OrdinalIgnoreCase) ||
                              (model.Architecture != null && model.Architecture.Contains("deepseek2", StringComparison.OrdinalIgnoreCase));

            var card = new ModelCardViewModel
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
                CanFitInVram = gpuInfo == null || estimatedVramMb <= gpuInfo.TotalVramMb,
                Role = model.Role ?? "None",
                IsVision = isVision,
                IsThinking = isThinking
            };
            card.PropertyChanged += ModelCard_PropertyChanged;
            Models.Add(card);
        }
        
        FilterModels();
    }

    private async void ModelCard_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelCardViewModel.Role) && sender is ModelCardViewModel card)
        {
            await _registry.UpdateModelRoleAsync(card.ModelId, card.Role);
        }
    }

    [RelayCommand]
    private async Task QuantizeModelTo4BitAsync(string modelId)
    {
        if (string.IsNullOrEmpty(modelId) || _quantizerService == null) return;
        var modelInfo = _registry.GetModel(modelId);
        if (modelInfo == null || !System.IO.File.Exists(modelInfo.FilePath)) return;

        bool success = await _quantizerService.QuantizeTo4BitAsync(modelInfo.FilePath, targetQuantType: "Q4_K_M");
        if (success)
        {
            await _registry.SyncWithDiskAsync();
        }
    }

    [RelayCommand]
    private async Task LoadModelAsync(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return;
        
        var modelInfo = _registry.GetModel(modelId);
        if (modelInfo == null) return;

        if (_inferenceEngine.IsModelLoaded && _inferenceEngine.CurrentModelPath == modelInfo.FilePath)
        {
            CurrentlyLoadedModelId = modelId;
            foreach (var m in Models) m.IsLoaded = (m.ModelId == modelId);
            return;
        }

        long seqId = System.Threading.Interlocked.Increment(ref _modelLoadSequenceId);
        _modelLoadCts?.Cancel();
        _modelLoadCts?.Dispose();
        _modelLoadCts = new System.Threading.CancellationTokenSource();
        var ct = _modelLoadCts.Token;

        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        var systemInfo = await _systemProfiler.GetSystemInfoAsync();
        if (ct.IsCancellationRequested || seqId != System.Threading.Volatile.Read(ref _modelLoadSequenceId)) return;
        
        try
        {
            await _inferenceEngine.UnloadModelAsync(ct);
            if (ct.IsCancellationRequested || seqId != System.Threading.Volatile.Read(ref _modelLoadSequenceId)) return;
            
            // Read GGUF metadata for dynamic sizing
            var metadata = Klydis.Core.Models.GgufMetadataReader.Parse(modelInfo.FilePath);
            int totalLayers = metadata != null && metadata.BlockCount.HasValue && metadata.BlockCount.Value > 0 ? (int)metadata.BlockCount.Value : 32;
            long layerSizeBytes = modelInfo.FileSizeBytes / totalLayers; // Approximation
            
            int rawContextLength = (int)(metadata?.ContextLength ?? 32768);
            int contextLength = Math.Clamp(rawContextLength < 32768 ? 32768 : rawContextLength, 32768, 131072);
            
            long kvCachePerLayerBytes = 2048; // Safe default
            if (metadata != null && metadata.EmbeddingLength.HasValue && metadata.HeadCount.HasValue && metadata.HeadCount.Value > 0 && metadata.HeadCountKv.HasValue)
            {
                long headDim = metadata.EmbeddingLength.Value / metadata.HeadCount.Value;
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

            if (ct.IsCancellationRequested || seqId != System.Threading.Volatile.Read(ref _modelLoadSequenceId)) return;

            await _inferenceEngine.LoadModelAsync(modelInfo.FilePath, plan);
        }
        catch (OperationCanceledException)
        {
            // Ignored - canceled by newer selection
        }
        catch (Exception)
        {
            if (seqId == System.Threading.Volatile.Read(ref _modelLoadSequenceId))
            {
                Action resetUi = () =>
                {
                    CurrentlyLoadedModelId = string.Empty;
                    foreach (var m in Models) m.IsLoaded = false;
                };

                if (System.Windows.Application.Current != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(resetUi);
                }
                else
                {
                    resetUi();
                }
            }
        }
    }

    [RelayCommand]
    private async Task UnloadModelAsync()
    {
        CurrentlyLoadedModelId = string.Empty;
        foreach (var m in Models) m.IsLoaded = false;
        await _inferenceEngine.UnloadModelAsync();
    }

    [RelayCommand]
    private async Task DeleteModelAsync(string modelId)
    {
        var model = Models.FirstOrDefault(m => m.ModelId == modelId);
        if (model != null)
        {
            var modelInfo = _registry.GetModel(modelId);
            if (modelInfo != null)
            {
                if (CurrentlyLoadedModelId == modelId)
                {
                    await UnloadModelAsync();
                }

                try
                {
                    if (System.IO.File.Exists(modelInfo.FilePath))
                    {
                        System.IO.File.Delete(modelInfo.FilePath);
                    }
                    await _registry.RemoveModelAsync(modelId);
                }
                catch (Exception)
                {
                    // Ignore or log file deletion errors
                }
            }

            Models.Remove(model);
            FilterModels();
        }
    }

    [ObservableProperty]
    private bool _isHfCategoriesVisible = true;

    partial void OnHfSearchTextChanged(string value)
    {
        IsHfCategoriesVisible = string.IsNullOrWhiteSpace(value);
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = HfSearchAsync();
        }
        else
        {
            HfResults.Clear();
        }
    }

    [RelayCommand]
    private async Task LoadHfModelsAsync()
    {
        try
        {
            // Populate Popular
            var popular = await _hfClient.SearchModelsAsync("", 10, "downloads");
            PopularModels.Clear();
            foreach (var m in popular) PopularModels.Add(CreateHfCard(m));

            // Populate Newest
            var newest = await _hfClient.SearchModelsAsync("", 10, "createdAt");
            NewestModels.Clear();
            foreach (var m in newest) NewestModels.Add(CreateHfCard(m));

            // Populate Highest Rated (Likes)
            var highestRated = await _hfClient.SearchModelsAsync("", 10, "likes");
            HighestRatedModels.Clear();
            foreach (var m in highestRated) HighestRatedModels.Add(CreateHfCard(m));
            
            ApplyHfFilters();
        }
        catch (Exception) { /* Ignored for beta */ }
    }

    private HfModelCardViewModel CreateHfCard(Klydis.Core.Models.HfModelInfo info)
    {
        bool isVision = info.RepoId.Contains("vision", StringComparison.OrdinalIgnoreCase) || 
                        info.RepoId.Contains("llava", StringComparison.OrdinalIgnoreCase) || 
                        info.RepoId.Contains("pixtral", StringComparison.OrdinalIgnoreCase) ||
                        info.RepoId.Contains("qwen-vl", StringComparison.OrdinalIgnoreCase) ||
                        info.Tags.Any(t => t.Contains("vision", StringComparison.OrdinalIgnoreCase) || 
                                           t.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                                           t.Contains("vlm", StringComparison.OrdinalIgnoreCase) ||
                                           t.Contains("multimodal", StringComparison.OrdinalIgnoreCase)) ||
                        info.PipelineTag.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                        info.PipelineTag.Contains("vision", StringComparison.OrdinalIgnoreCase);

        bool isThinking = info.RepoId.Contains("think", StringComparison.OrdinalIgnoreCase) || 
                          info.RepoId.Contains("-r1", StringComparison.OrdinalIgnoreCase) ||
                          info.Tags.Any(t => t.Contains("think", StringComparison.OrdinalIgnoreCase) ||
                                             t.Contains("chain-of-thought", StringComparison.OrdinalIgnoreCase) ||
                                             t.Contains("reasoning", StringComparison.OrdinalIgnoreCase));

        var card = new HfModelCardViewModel
        {
            RepoId = info.RepoId,
            Author = info.Author,
            ModelName = info.ModelName,
            Downloads = info.Downloads.ToString(),
            Likes = info.Likes,
            Tags = info.Tags,
            IsVision = isVision,
            IsThinking = isThinking
        };

        _ = LoadGgufFilesAsync(card, info.RepoId);
        return card;
    }

    private async Task LoadGgufFilesAsync(HfModelCardViewModel card, string repoId)
    {
        try
        {
            var files = await _hfClient.GetModelFilesAsync(repoId);
            var sortedFiles = files.OrderByDescending(f => 
                f.QuantType.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase) ? 3 :
                f.QuantType.Contains("Q4_0", StringComparison.OrdinalIgnoreCase) ? 2 :
                f.QuantType.Contains("Q4", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => f.SizeBytes);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var file in sortedFiles)
                {
                    long estimatedVramMb = (long)((file.SizeBytes / (1024.0 * 1024.0)) * 1.2);
                    bool fitsInVram = VramTotalMb == 0 || estimatedVramMb <= VramTotalMb;

                    card.GgufFiles.Add(new HfFileViewModel
                    {
                        FileName = file.Filename,
                        Size = (file.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB",
                        QuantType = file.QuantType,
                        RepoId = repoId,
                        CanFitInVram = fitsInVram
                    });
                }
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task HfSearchAsync()
    {
        HfIsSearching = true;
        try
        {
            string query = HfSearchText;
            if (FilterVisionOnly) query += " vision";
            if (FilterThinkingOnly) query += " think";
            
            var results = await _hfClient.SearchModelsAsync(query, 20, "downloads");
            HfResults.Clear();
            foreach (var m in results) HfResults.Add(CreateHfCard(m));
            
            ApplyHfFilters();
        }
        catch (Exception) { }
        HfIsSearching = false;
    }

    [RelayCommand]
    private async Task DownloadModelAsync(HfFileViewModel file)
    {
        if (file == null) return;
        
        string destPath = System.IO.Path.Combine(_registry.ModelsDirectory, file.FileName);
        
        await StartDownloadAsync(file.RepoId, file.FileName, destPath);
    }

    private async Task StartDownloadAsync(string repoId, string fileName, string destPath)
    {
        var downloadVm = new ActiveDownloadViewModel
        {
            FileName = fileName,
            Status = $"Downloading {fileName}...",
            Progress = 0
        };

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveDownloads.Add(downloadVm);
            OnPropertyChanged(nameof(HasActiveDownloads));
        });

        var progress = new Progress<Klydis.Core.Models.DownloadProgress>(p =>
        {
            downloadVm.Progress = p.PercentComplete;
            downloadVm.Status = $"Downloading {fileName}... {(p.BytesDownloaded / (1024.0*1024.0)):F1} MB / {(p.TotalBytes / (1024.0*1024.0)):F1} MB ({p.SpeedBytesPerSecond / (1024.0*1024.0):F1} MB/s)";
            System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateTotalDownloadProgress());
        });

        var record = new Klydis.Core.Models.ActiveDownloadRecord(repoId, fileName, destPath, DateTime.UtcNow);
        await _registry.AddActiveDownloadAsync(record);

        try
        {
            await Task.Run(async () =>
            {
                await _hfClient.DownloadModelAsync(repoId, fileName, destPath, progress, downloadVm.CancellationTokenSource.Token);
            });
            downloadVm.Status = "Download complete.";
            await _registry.RemoveActiveDownloadAsync(repoId, fileName);
            await _registry.SyncWithDiskAsync();
            await ScanAsync(); // Refresh local list
        }
        catch (OperationCanceledException)
        {
            downloadVm.Status = "Download cancelled.";
        }
        catch (Exception ex)
        {
            downloadVm.Status = $"Download failed: {ex.Message}";
        }
        finally
        {
            await Task.Delay(3000); // Show complete status for a bit
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ActiveDownloads.Remove(downloadVm);
                OnPropertyChanged(nameof(HasActiveDownloads));
            });
        }
    }
}
