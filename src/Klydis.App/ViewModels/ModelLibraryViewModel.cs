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
        List<ActiveDownloadViewModel> snapshot;
        if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => UpdateTotalDownloadProgress());
            return;
        }

        lock (ActiveDownloads)
        {
            snapshot = ActiveDownloads.ToList();
        }

        if (snapshot.Count == 0)
        {
            TotalDownloadProgress = 0;
            return;
        }

        double total = 0;
        foreach (var download in snapshot)
        {
            total += download.Progress;
        }
        TotalDownloadProgress = total / snapshot.Count;
    }

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _popularModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _newestModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _highestRatedModels = new();

    // Local model filters
    [ObservableProperty]
    private bool _filterVisionOnly;

    [ObservableProperty]
    private bool _filterThinkingOnly;

    [ObservableProperty]
    private string _selectedSizeFilter = "All Sizes";

    [ObservableProperty]
    private string _selectedRoleFilter = "All Roles";

    // Hugging Face isolated model filters
    [ObservableProperty]
    private bool _hfFilterVisionOnly;

    [ObservableProperty]
    private bool _hfFilterThinkingOnly;

    [ObservableProperty]
    private string _hfSelectedSizeFilter = "All Sizes";

    [ObservableProperty]
    private string _hfSelectedRoleFilter = "All Roles";

    [ObservableProperty]
    private ObservableCollection<string> _availableSizeFilters = new() { "All Sizes", "< 7B", "7B - 14B", "14B - 35B", "> 35B" };

    [ObservableProperty]
    private ObservableCollection<string> _availableRoleFilters = new() { "All Roles", "Chat", "Code", "Instruct", "Vision", "Researcher", "UI Designer", "None" };

    [ObservableProperty]
    private ModelCardViewModel? _selectedLocalModel;

    partial void OnSelectedLocalModelChanged(ModelCardViewModel? value)
    {
        if (value != null && !value.IsLoaded)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(LoadModelAsync(value.ModelId), operation: nameof(LoadModelAsync));
        }
    }

    // Local model filter change handlers (strictly isolated from HF)
    partial void OnFilterVisionOnlyChanged(bool value) { FilterModels(); }
    partial void OnFilterThinkingOnlyChanged(bool value) { FilterModels(); }
    partial void OnSelectedSizeFilterChanged(string value) { FilterModels(); }
    partial void OnSelectedRoleFilterChanged(string value) { FilterModels(); }

    // Hugging Face filter change handlers (strictly isolated from Local models)
    partial void OnHfFilterVisionOnlyChanged(bool value) { OnHfSearchOrFilterChanged(); }
    partial void OnHfFilterThinkingOnlyChanged(bool value) { OnHfSearchOrFilterChanged(); }
    partial void OnHfSelectedSizeFilterChanged(string value) { OnHfSearchOrFilterChanged(); }
    partial void OnHfSelectedRoleFilterChanged(string value) { OnHfSearchOrFilterChanged(); }

    public bool IsHfSearchOrFilterActive =>
        !string.IsNullOrWhiteSpace(HfSearchText) ||
        HfFilterVisionOnly ||
        HfFilterThinkingOnly ||
        (HfSelectedSizeFilter != null && HfSelectedSizeFilter != "All Sizes") ||
        (HfSelectedRoleFilter != null && HfSelectedRoleFilter != "All Roles");

    private void ApplyHfFilters()
    {
        var view2 = System.Windows.Data.CollectionViewSource.GetDefaultView(PopularModels);
        var view3 = System.Windows.Data.CollectionViewSource.GetDefaultView(NewestModels);
        var view4 = System.Windows.Data.CollectionViewSource.GetDefaultView(HighestRatedModels);

        Predicate<object> filter = (obj) =>
        {
            if (obj is HfModelCardViewModel card)
            {
                if (HfFilterVisionOnly && !card.IsVision) return false;
                if (HfFilterThinkingOnly && !card.IsThinking) return false;
                return true;
            }
            return false;
        };

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
        
        Klydis.Core.Diagnostics.FireAndForget.Observe(ScanAsync(), operation: nameof(ScanAsync));
        Klydis.Core.Diagnostics.FireAndForget.Observe(LoadHfModelsAsync(), operation: nameof(LoadHfModelsAsync));
        Klydis.Core.Diagnostics.FireAndForget.Observe(ResumeActiveDownloadsAsync(), operation: nameof(ResumeActiveDownloadsAsync));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (s, e) => { try { await UpdateVramUsageAsync(); } catch { } };
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
            System.Windows.Application.Current.Dispatcher.Invoke(() => { Klydis.Core.Diagnostics.FireAndForget.Observe(PopulateModelsAsync(), operation: nameof(PopulateModelsAsync)); });
        }
    }

    private async Task ResumeActiveDownloadsAsync()
    {
        var activeDownloads = await _registry.GetActiveDownloadsAsync();
        foreach (var download in activeDownloads)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(StartDownloadAsync(download.RepoId, download.FileName, download.DestinationPath), operation: nameof(StartDownloadAsync));
        }
    }

    private async Task UpdateVramUsageAsync()
    {
        try
        {
            var usage = await _gpuProfiler.GetRealTimeVramUsageAsync();
            if (usage != null && VramTotalMb > 0)
            {
                VramUsageMb = usage.UsedVramMb;
                VramUsagePercent = 100.0 * VramUsageMb / VramTotalMb;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VRAM usage update failed: {ex.Message}");
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

            // Pre-flight compatibility evaluation: flags models the bundled native engine
            // cannot load (e.g. a newer tokenizer pre-type) so users see it on the card
            // instead of discovering it via a confusing load error.
            var compat = Klydis.Core.Inference.GgufCompatibilityAdapter.Evaluate(model.FilePath);
            bool isCompatible = compat.IsSupported;
            string? compatWarning = isCompatible ? null : (compat.WarningMessage ?? "Model is not compatible with the bundled native engine.");

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
                IsThinking = isThinking,
                IsCompatible = isCompatible,
                CompatibilityWarning = compatWarning
            };
            card.PropertyChanged += ModelCard_PropertyChanged;
            Models.Add(card);
        }
        
        FilterModels();
    }

    private async void ModelCard_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        try
        {
            if (e.PropertyName == nameof(ModelCardViewModel.Role) && sender is ModelCardViewModel card)
            {
                await _registry.UpdateModelRoleAsync(card.ModelId, card.Role);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Model role update failed: {ex.Message}");
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
            long layerSizeBytes = modelInfo.FileSizeBytes / Math.Max(1, totalLayers); // Approximation
            
            // Model's native context bounded by the architecture ceiling — never an arbitrary
            // 4K/16K floor: the offload plan's VRAM math protects the GPU, and hybrid/recurrent
            // models (tiny KV caches) run at 64K+ on a 16GB card. A tiny desired context here
            // combined with a zero UserContextLimit loaded the model at a 4K window, capping
            // every generation at window − prompt − 512 ≈ 2K tokens.
            string archLower = (metadata?.Architecture ?? "").ToLowerInvariant();
            bool isHybridSsm = archLower is "qwen35" or "qwen3next" or "qwen35moe" or "mamba" or "rwkv" or "jamba";
            int archCeiling = isHybridSsm ? 262144 : 131072;
            int rawContextLength = metadata?.ContextLength is > 0
                ? (int)metadata.ContextLength.Value
                : (isHybridSsm ? 262144 : 65536);
            int contextLength = Math.Clamp(rawContextLength, 2048, archCeiling);
            
            long kvCachePerLayerBytes = 2048;
            if (metadata != null)
            {
                var kvEst = Klydis.Core.Inference.KvCacheCalculator.Calculate(metadata, 1, Klydis.Core.Inference.KvCacheQuantizationType.Q4_0);
                kvCachePerLayerBytes = (long)Math.Max(512, kvEst.BytesPerToken / Math.Max(1, kvEst.NumLayers));
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

    private System.Threading.CancellationTokenSource? _hfSearchCts;

    partial void OnHfSearchTextChanged(string value)
    {
        OnHfSearchOrFilterChanged();
    }

    private void OnHfSearchOrFilterChanged()
    {
        bool active = IsHfSearchOrFilterActive;
        IsHfCategoriesVisible = !active;

        if (active)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(TriggerHfSearchAsync(), operation: nameof(TriggerHfSearchAsync));
        }
        else
        {
            _hfSearchCts?.Cancel();
            HfResults.Clear();
            ApplyHfFilters();
        }
    }

    [RelayCommand]
    private void ClearHfFilters()
    {
        HfSearchText = string.Empty;
        HfFilterVisionOnly = false;
        HfFilterThinkingOnly = false;
        HfSelectedSizeFilter = "All Sizes";
        HfSelectedRoleFilter = "All Roles";
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

        card.LoadFilesAction = async (targetCard, forceReload) =>
        {
            await LoadGgufFilesAsync(targetCard, info.RepoId, forceReload);
        };

        return card;
    }

    private async Task LoadGgufFilesAsync(HfModelCardViewModel card, string repoId, bool forceReload = false)
    {
        if (card.IsLoadingFiles) return;

        void SetLoadingState(bool loading, string? error = null)
        {
            Action act = () =>
            {
                card.IsLoadingFiles = loading;
                card.LoadError = error;
            };
            if (System.Windows.Application.Current != null)
                System.Windows.Application.Current.Dispatcher.Invoke(act);
            else
                act();
        }

        SetLoadingState(true, null);

        try
        {
            var files = await _hfClient.GetModelFilesAsync(repoId, forceReload);
            var sortedFiles = files.OrderByDescending(f => 
                f.QuantType.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase) ? 4 :
                f.QuantType.Contains("Q5_K_M", StringComparison.OrdinalIgnoreCase) ? 3 :
                f.QuantType.Contains("Q4_0", StringComparison.OrdinalIgnoreCase) ? 2 :
                f.QuantType.Contains("Q4", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => f.SizeBytes)
                .ToList();

            Action updateUi = () =>
            {
                card.GgufFiles.Clear();
                foreach (var file in sortedFiles)
                {
                    long estimatedVramMb = (long)((file.SizeBytes / (1024.0 * 1024.0)) * 1.2);
                    bool fitsInVram = VramTotalMb == 0 || estimatedVramMb <= VramTotalMb;

                    string sizeText = file.SizeBytes > 0
                        ? (file.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB"
                        : "Unknown size";

                    card.GgufFiles.Add(new HfFileViewModel
                    {
                        FileName = file.Filename,
                        Size = sizeText,
                        QuantType = file.QuantType,
                        RepoId = repoId,
                        CanFitInVram = fitsInVram,
                        Sha256 = file.Sha256
                    });
                }

                card.HasLoadedFiles = true;
                card.IsLoadingFiles = false;
                card.LoadError = null;
            };

            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(updateUi);
            }
            else
            {
                updateUi();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load GGUF files for {repoId}: {ex.Message}");
            SetLoadingState(false, "Failed to load files from Hugging Face.");
        }
    }

    [RelayCommand]
    private async Task HfSearchAsync()
    {
        await TriggerHfSearchAsync();
    }

    private async Task TriggerHfSearchAsync()
    {
        _hfSearchCts?.Cancel();
        _hfSearchCts?.Dispose();
        _hfSearchCts = new System.Threading.CancellationTokenSource();
        var ct = _hfSearchCts.Token;

        try
        {
            await Task.Delay(300, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HfIsSearching = true;
        try
        {
            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(HfSearchText))
            {
                queryParts.Add(HfSearchText.Trim());
            }

            if (HfFilterVisionOnly) queryParts.Add("vision");
            if (HfFilterThinkingOnly) queryParts.Add("think");

            if (HfSelectedRoleFilter != "All Roles" && HfSelectedRoleFilter != "None")
            {
                switch (HfSelectedRoleFilter)
                {
                    case "Code": queryParts.Add("code"); break;
                    case "Instruct": queryParts.Add("instruct"); break;
                    case "Chat": queryParts.Add("chat"); break;
                    case "Vision": queryParts.Add("vision"); break;
                    case "Researcher": queryParts.Add("research"); break;
                    case "UI Designer": queryParts.Add("ui"); break;
                }
            }

            if (HfSelectedSizeFilter != "All Sizes")
            {
                switch (HfSelectedSizeFilter)
                {
                    case "< 7B": queryParts.Add("7b"); break;
                    case "7B - 14B": queryParts.Add("7b"); break;
                    case "14B - 35B": queryParts.Add("14b"); break;
                    case "> 35B": queryParts.Add("70b"); break;
                }
            }

            string hfQuery = string.Join(" ", queryParts.Distinct(StringComparer.OrdinalIgnoreCase));

            var rawResults = await _hfClient.SearchModelsAsync(hfQuery, limit: 60, sort: "downloads", ct: ct);
            if (ct.IsCancellationRequested) return;

            if (rawResults.Count == 0 && queryParts.Count > 1 && !string.IsNullOrWhiteSpace(HfSearchText))
            {
                rawResults = await _hfClient.SearchModelsAsync(HfSearchText.Trim(), limit: 60, sort: "downloads", ct: ct);
            }

            if (ct.IsCancellationRequested) return;

            var filteredList = rawResults.Where(info =>
            {
                if (HfFilterVisionOnly)
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
                    if (!isVision) return false;
                }

                if (HfFilterThinkingOnly)
                {
                    bool isThinking = info.RepoId.Contains("think", StringComparison.OrdinalIgnoreCase) || 
                                      info.RepoId.Contains("-r1", StringComparison.OrdinalIgnoreCase) ||
                                      info.Tags.Any(t => t.Contains("think", StringComparison.OrdinalIgnoreCase) ||
                                                         t.Contains("chain-of-thought", StringComparison.OrdinalIgnoreCase) ||
                                                         t.Contains("reasoning", StringComparison.OrdinalIgnoreCase));
                    if (!isThinking) return false;
                }

                if (HfSelectedSizeFilter != "All Sizes")
                {
                    double? size = Klydis.Core.Models.HuggingFaceClient.ExtractParameterSize(info.RepoId, info.Tags);
                    if (size.HasValue)
                    {
                        bool sizeMatches = HfSelectedSizeFilter switch
                        {
                            "< 7B" => size.Value < 7,
                            "7B - 14B" => size.Value >= 7 && size.Value <= 14,
                            "14B - 35B" => size.Value > 14 && size.Value <= 35,
                            "> 35B" => size.Value > 35,
                            _ => true
                        };
                        if (!sizeMatches) return false;
                    }
                }

                if (HfSelectedRoleFilter != "All Roles" && HfSelectedRoleFilter != "None")
                {
                    bool roleMatches = HfSelectedRoleFilter switch
                    {
                        "Chat" => info.RepoId.Contains("chat", StringComparison.OrdinalIgnoreCase) || info.RepoId.Contains("instruct", StringComparison.OrdinalIgnoreCase),
                        "Code" => info.RepoId.Contains("code", StringComparison.OrdinalIgnoreCase) || info.RepoId.Contains("coder", StringComparison.OrdinalIgnoreCase) || info.Tags.Any(t => t.Contains("code", StringComparison.OrdinalIgnoreCase)),
                        "Instruct" => info.RepoId.Contains("instruct", StringComparison.OrdinalIgnoreCase),
                        "Vision" => info.RepoId.Contains("vision", StringComparison.OrdinalIgnoreCase) || info.RepoId.Contains("vl", StringComparison.OrdinalIgnoreCase) || info.Tags.Any(t => t.Contains("vision", StringComparison.OrdinalIgnoreCase)),
                        "Researcher" => info.RepoId.Contains("research", StringComparison.OrdinalIgnoreCase) || info.RepoId.Contains("r1", StringComparison.OrdinalIgnoreCase) || info.Tags.Any(t => t.Contains("math", StringComparison.OrdinalIgnoreCase) || t.Contains("reasoning", StringComparison.OrdinalIgnoreCase)),
                        "UI Designer" => info.RepoId.Contains("ui", StringComparison.OrdinalIgnoreCase) || info.RepoId.Contains("design", StringComparison.OrdinalIgnoreCase),
                        _ => true
                    };
                    if (!roleMatches) return false;
                }

                return true;
            }).ToList();

            var rankedList = Klydis.Core.Models.HuggingFaceClient.RankResults(filteredList, HfSearchText);

            Action updateUi = () =>
            {
                if (ct.IsCancellationRequested) return;
                HfResults.Clear();
                int idx = 0;
                foreach (var item in rankedList)
                {
                    var card = CreateHfCard(item);
                    HfResults.Add(card);
                    // Pre-fetch top 3 results for instant responsiveness without flooding
                    if (idx < 3)
                    {
                        Klydis.Core.Diagnostics.FireAndForget.Observe(card.LoadFilesCommand.ExecuteAsync(false), operation: nameof(card.LoadFilesCommand));
                    }
                    idx++;
                }
            };

            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(updateUi);
            }
            else
            {
                updateUi();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                HfIsSearching = false;
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadModelAsync(HfFileViewModel file)
    {
        if (file == null) return;

        // Repo-scoped destination: two repos can publish identically named GGUF files, and a
        // flat models directory silently overwrites (or blocks) the second one. Scope by the
        // sanitized repository ID, mirroring the Hub's own cache layout.
        string repoSubdir = Klydis.Core.Models.HuggingFaceClient.SanitizeRepoIdForPath(file.RepoId);
        string destPath = System.IO.Path.Combine(_registry.ModelsDirectory, repoSubdir, file.FileName);
        
        await StartDownloadAsync(file.RepoId, file.FileName, destPath, file.Sha256);
    }

    private async Task StartDownloadAsync(string repoId, string fileName, string destPath, string? expectedSha256 = null)
    {
        lock (ActiveDownloads)
        {
            if (ActiveDownloads.Any(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        var downloadVm = new ActiveDownloadViewModel
        {
            FileName = fileName,
            Status = $"Downloading {fileName}...",
            Progress = 0
        };

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            lock (ActiveDownloads)
            {
                if (!ActiveDownloads.Any(d => string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    ActiveDownloads.Add(downloadVm);
                    OnPropertyChanged(nameof(HasActiveDownloads));
                }
            }
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
                await _hfClient.DownloadModelAsync(repoId, fileName, destPath, progress, downloadVm.CancellationTokenSource.Token, expectedSha256);
            });
            downloadVm.Status = "Download complete.";
            await _registry.RemoveActiveDownloadAsync(repoId, fileName);
            await _registry.SyncWithDiskAsync();
            await ScanAsync(); // Refresh local list
        }
        catch (OperationCanceledException)
        {
            downloadVm.Status = "Download cancelled.";
            // An explicit cancel is a deliberate stop, unlike a crash: drop the resume record
            // so the download does not silently restart on the next app launch. The partial
            // .download file is kept, so a later manual download resumes from it.
            await _registry.RemoveActiveDownloadAsync(repoId, fileName);
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
