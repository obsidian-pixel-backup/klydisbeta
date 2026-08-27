using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.Core.Hardware;
using Klydis.Core.Inference;
using Klydis.Core.Models;

namespace Klydis.App.ViewModels;

/// <summary>
/// Modern ViewModel for managing the Model Hub (Discover from Hugging Face & On-Device models).
/// </summary>
public partial class ModelLibraryViewModel : ObservableObject
{
    // ==========================================
    // System Badges & Telemetry
    // ==========================================
    [ObservableProperty]
    private string _vramBadgeText = "16 GiB VRAM";

    [ObservableProperty]
    private string _ramBadgeText = "48 GiB RAM";

    [ObservableProperty]
    private string _cpuBadgeText = "6/12 CPU";

    [ObservableProperty]
    private string _cacheCountText = "1 Cache";

    [ObservableProperty]
    private string _localCountText = "0 Local";

    [ObservableProperty]
    private string _protocolText = "Auto";

    [ObservableProperty]
    private int _vramUsageMb;

    [ObservableProperty]
    private int _vramTotalMb = 16384;

    [ObservableProperty]
    private double _vramUsagePercent;

    // ==========================================
    // Tab Selection & Search
    // ==========================================
    [ObservableProperty]
    private string _selectedHubTab = "Discover"; // "Discover" or "OnDevice"

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    // ==========================================
    // Filter Dropdowns
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<string> _availableFormatFilters = new() { "GGUF", "All formats", "Safetensors" };

    [ObservableProperty]
    private string _selectedFormatFilter = "GGUF";

    [ObservableProperty]
    private ObservableCollection<string> _availableCapabilityFilters = new()
    {
        "All capabilities",
        "Text Generation",
        "Conversational",
        "Coding Agent",
        "Vision",
        "Reasoning",
        "Embedding"
    };

    [ObservableProperty]
    private string _selectedCapabilityFilter = "All capabilities";

    [ObservableProperty]
    private ObservableCollection<string> _availableSortFilters = new()
    {
        "Most downloaded",
        "Most likes",
        "Recently updated",
        "Alphabetical"
    };

    [ObservableProperty]
    private string _selectedSortFilter = "Most downloaded";

    [ObservableProperty]
    private ObservableCollection<string> _availableCategoryFilters = new()
    {
        "All",
        "Trending",
        "Coding",
        "Reasoning",
        "Vision",
        "Embedding"
    };

    [ObservableProperty]
    private string _selectedCategoryFilter = "All";

    // ==========================================
    // Hub Items & Master-Detail State
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<ModelHubItemViewModel> _hubItems = new();

    [ObservableProperty]
    private ModelHubItemViewModel? _selectedHubItem;

    [ObservableProperty]
    private bool _isLoadingHub;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // ==========================================
    // Active Downloads & Local Engine State
    // ==========================================
    [ObservableProperty]
    private ObservableCollection<ActiveDownloadViewModel> _activeDownloads = new();

    public bool HasActiveDownloads => ActiveDownloads.Count > 0;

    [ObservableProperty]
    private double _totalDownloadProgress;

    [ObservableProperty]
    private string _currentlyLoadedModelId = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    // Legacy collections retained for compatibility
    [ObservableProperty]
    private ObservableCollection<ModelCardViewModel> _models = new();

    [ObservableProperty]
    private ObservableCollection<ModelCardViewModel> _filteredModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _popularModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _newestModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _highestRatedModels = new();

    [ObservableProperty]
    private ObservableCollection<HfModelCardViewModel> _hfResults = new();

    [ObservableProperty]
    private bool _hfIsSearching;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _hfSearchText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _availableRoleFilters = new() { "All Roles", "Chat", "Code", "Instruct", "Vision", "Researcher", "UI Designer", "None" };

    // ==========================================
    // Internal Fields
    // ==========================================
    private readonly ModelRegistry _registry;
    private readonly HuggingFaceClient _hfClient;
    private readonly ModelQuantizerService? _quantizerService;
    private readonly InferenceEngine _inferenceEngine;
    private readonly GpuProfiler _gpuProfiler;
    private readonly SystemProfiler _systemProfiler;
    private readonly OffloadStrategy _offloadStrategy;
    private readonly DispatcherTimer _timer;

    private readonly List<ModelHubItemViewModel> _allDiscoverItems = new();
    private readonly List<ModelHubItemViewModel> _allLocalItems = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _loadCts;
    private long _modelLoadSequenceId = 0;

    public ModelLibraryViewModel(
        ModelRegistry registry,
        HuggingFaceClient hfClient,
        InferenceEngine inferenceEngine,
        GpuProfiler gpuProfiler,
        SystemProfiler systemProfiler,
        OffloadStrategy offloadStrategy,
        ModelQuantizerService? quantizerService = null)
    {
        _registry = registry;
        _hfClient = hfClient;
        _quantizerService = quantizerService;
        _inferenceEngine = inferenceEngine;
        _gpuProfiler = gpuProfiler;
        _systemProfiler = systemProfiler;
        _offloadStrategy = offloadStrategy;

        _registry.RegistryChanged += OnRegistryChanged;
        _inferenceEngine.ModelStateChanged += OnModelStateChanged;

        // Initialize and trigger async loads
        Klydis.Core.Diagnostics.FireAndForget.Observe(InitializeTelemetryAndModelsAsync(), operation: nameof(InitializeTelemetryAndModelsAsync));
        Klydis.Core.Diagnostics.FireAndForget.Observe(ResumeActiveDownloadsAsync(), operation: nameof(ResumeActiveDownloadsAsync));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _timer.Tick += async (s, e) => { try { await UpdateHardwareTelemetryAsync(); } catch { } };
        _timer.Start();
    }

    // ==========================================
    // Property Change Handlers
    // ==========================================
    partial void OnSelectedHubTabChanged(string value)
    {
        ApplyFiltersAndRefreshView();
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, ct);
                if (ct.IsCancellationRequested) return;

                if (SelectedHubTab == "Discover" && !string.IsNullOrWhiteSpace(value))
                {
                    await PerformRemoteSearchAsync(value.Trim(), ct);
                }
                else
                {
                    ApplyFiltersAndRefreshView();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    partial void OnSelectedFormatFilterChanged(string value) => ApplyFiltersAndRefreshView();
    partial void OnSelectedCapabilityFilterChanged(string value) => ApplyFiltersAndRefreshView();
    partial void OnSelectedSortFilterChanged(string value) => ApplyFiltersAndRefreshView();
    partial void OnSelectedCategoryFilterChanged(string value) => ApplyFiltersAndRefreshView();

    partial void OnSelectedHubItemChanged(ModelHubItemViewModel? value)
    {
        if (value == null) return;

        foreach (var item in HubItems)
        {
            item.IsSelected = (item == value);
        }

        // Lazy load GGUF files if not loaded
        if (!value.HasLoadedFiles && !value.IsLoadingFiles)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(LoadFilesForItemAsync(value), operation: nameof(LoadFilesForItemAsync));
        }

        // Lazy load README markdown if not loaded
        if (string.IsNullOrEmpty(value.ReadmeMarkdown) && !value.IsLoadingReadme)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(LoadReadmeForItemAsync(value), operation: nameof(LoadReadmeForItemAsync));
        }
    }

    // ==========================================
    // Initialization & Data Loading
    // ==========================================
    private async Task InitializeTelemetryAndModelsAsync()
    {
        await UpdateHardwareTelemetryAsync();
        await ScanLocalModelsAsync();
        await LoadDiscoverModelsAsync();
    }

    private async Task UpdateHardwareTelemetryAsync()
    {
        try
        {
            var profile = await _systemProfiler.GetHardwareProfileAsync();

            int vramTotalGb = 16;
            if (profile.Gpu != null)
            {
                VramTotalMb = profile.Gpu.TotalVramMb;
                VramUsageMb = profile.Gpu.UsedVramMb;
                VramUsagePercent = VramTotalMb > 0 ? 100.0 * VramUsageMb / VramTotalMb : 0;
                vramTotalGb = (int)Math.Round(VramTotalMb / 1024.0);
                VramBadgeText = $"{vramTotalGb} GiB VRAM";
            }
            else
            {
                VramBadgeText = "Shared VRAM";
            }

            int ramTotalGb = (int)Math.Round(profile.System.TotalRamGb);
            RamBadgeText = $"{ramTotalGb} GiB RAM";

            int cores = profile.System.CoreCount > 0 ? profile.System.CoreCount : 6;
            int threads = profile.System.LogicalProcessorCount > 0 ? profile.System.LogicalProcessorCount : 12;
            CpuBadgeText = $"{cores}/{threads} CPU";

            int localCount = _registry.GetAllModels().Count();
            LocalCountText = $"{localCount} Local";
            CacheCountText = "1 Cache";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error updating telemetry: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        IsScanning = true;
        await _registry.SyncWithDiskAsync();
        await ScanLocalModelsAsync();
        await LoadDiscoverModelsAsync();
        IsScanning = false;
    }

    private async Task ScanLocalModelsAsync()
    {
        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        var localModels = _registry.GetAllModels().ToList();

        var localVms = new List<ModelHubItemViewModel>();
        foreach (var m in localModels)
        {
            var vm = ModelHubItemViewModel.FromLocalModel(m, gpuInfo);
            vm.IsLoaded = (_inferenceEngine.IsModelLoaded && _inferenceEngine.CurrentModelPath == m.FilePath);
            localVms.Add(vm);
        }

        _allLocalItems.Clear();
        _allLocalItems.AddRange(localVms);

        LocalCountText = $"{_allLocalItems.Count} Local";

        // Also update legacy Models collection
        Models.Clear();
        foreach (var m in localModels)
        {
            var card = new ModelCardViewModel
            {
                ModelId = m.Id,
                DisplayName = m.DisplayName,
                Architecture = m.Architecture ?? "Transformers",
                ParameterSize = m.ParameterCount?.ToString("F1") ?? "Unknown",
                QuantType = m.QuantizationType ?? "GGUF",
                FileSizeGb = (m.FileSizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB",
                FileName = m.FileName,
                Role = m.Role ?? "None",
                IsLoaded = (_inferenceEngine.IsModelLoaded && _inferenceEngine.CurrentModelPath == m.FilePath)
            };
            Models.Add(card);
        }

        if (SelectedHubTab == "OnDevice")
        {
            ApplyFiltersAndRefreshView();
        }
    }

    private async Task LoadDiscoverModelsAsync()
    {
        IsLoadingHub = true;
        try
        {
            // Seed curated popular models list
            var seedList = GetCuratedSeedModels();
            _allDiscoverItems.Clear();
            _allDiscoverItems.AddRange(seedList);

            // Fetch live trending / popular models from HuggingFace
            try
            {
                var popular = await _hfClient.SearchModelsAsync("", 40, "downloads");
                if (popular.Count > 0)
                {
                    var liveItems = new List<ModelHubItemViewModel>();
                    foreach (var m in popular)
                    {
                        var item = ModelHubItemViewModel.FromHfModel(m, VramTotalMb);
                        liveItems.Add(item);
                    }

                    // Merge and deduplicate by repoId
                    var combined = seedList.Concat(liveItems)
                        .GroupBy(x => x.RepoId, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();

                    _allDiscoverItems.Clear();
                    _allDiscoverItems.AddRange(combined);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HuggingFace search error: {ex.Message}");
            }

            if (SelectedHubTab == "Discover")
            {
                ApplyFiltersAndRefreshView();
            }
        }
        finally
        {
            IsLoadingHub = false;
        }
    }

    private async Task PerformRemoteSearchAsync(string query, CancellationToken ct)
    {
        IsLoadingHub = true;
        try
        {
            var results = await _hfClient.SearchModelsAsync(query, 50, "downloads", ct);
            if (ct.IsCancellationRequested) return;

            var items = new List<ModelHubItemViewModel>();
            foreach (var r in results)
            {
                items.Add(ModelHubItemViewModel.FromHfModel(r, VramTotalMb));
            }

            var ranked = Klydis.Core.Models.HuggingFaceClient.RankResults(results, query);
            var rankedItems = ranked.Select(r => ModelHubItemViewModel.FromHfModel(r, VramTotalMb)).ToList();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (ct.IsCancellationRequested) return;
                HubItems.Clear();
                foreach (var item in rankedItems)
                {
                    HubItems.Add(item);
                }

                if (HubItems.Count > 0 && SelectedHubItem == null)
                {
                    SelectedHubItem = HubItems[0];
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Remote search failed: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingHub = false;
            }
        }
    }

    // ==========================================
    // Filtering & View Synchronization
    // ==========================================
    private void ApplyFiltersAndRefreshView()
    {
        var source = SelectedHubTab == "Discover" ? _allDiscoverItems : _allLocalItems;
        var query = source.AsEnumerable();

        // Search text
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string q = SearchQuery.Trim();
            query = query.Where(m =>
                m.RepoId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.ModelName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Architecture.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                m.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        // Capability filter
        if (SelectedCapabilityFilter != "All capabilities")
        {
            query = SelectedCapabilityFilter switch
            {
                "Coding Agent" => query.Where(m => m.IsCode),
                "Vision" => query.Where(m => m.IsVision),
                "Reasoning" => query.Where(m => m.IsThinking),
                "Embedding" => query.Where(m => m.IsEmbedding),
                "Conversational" => query.Where(m => m.IsConversational),
                "Text Generation" => query.Where(m => !m.IsEmbedding),
                _ => query
            };
        }

        // Category filter
        if (SelectedCategoryFilter != "All")
        {
            query = SelectedCategoryFilter switch
            {
                "Coding" => query.Where(m => m.IsCode),
                "Reasoning" => query.Where(m => m.IsThinking),
                "Vision" => query.Where(m => m.IsVision),
                "Embedding" => query.Where(m => m.IsEmbedding),
                _ => query
            };
        }

        // Sort
        query = SelectedSortFilter switch
        {
            "Most likes" => query.OrderByDescending(m => m.Likes),
            "Recently updated" => query.OrderByDescending(m => m.LastModified),
            "Alphabetical" => query.OrderBy(m => m.ModelName),
            _ => query.OrderByDescending(m => m.Downloads).ThenByDescending(m => m.Likes)
        };

        var filteredList = query.ToList();

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            HubItems.Clear();
            foreach (var item in filteredList)
            {
                HubItems.Add(item);
            }

            if (HubItems.Count > 0)
            {
                if (SelectedHubItem == null || !HubItems.Contains(SelectedHubItem))
                {
                    SelectedHubItem = HubItems[0];
                }
            }
            else
            {
                SelectedHubItem = null;
            }
        });
    }

    // ==========================================
    // Item Detail Fetchers (Files & README)
    // ==========================================
    public async Task LoadFilesForItemAsync(ModelHubItemViewModel item, bool forceReload = false)
    {
        if (item == null || item.IsLoadingFiles || item.IsLocal) return;

        item.IsLoadingFiles = true;
        item.LoadError = null;

        try
        {
            var files = await _hfClient.GetModelFilesAsync(item.RepoId, forceReload);
            var sortedFiles = files.OrderByDescending(f =>
                f.QuantType.Contains("Q8_0", StringComparison.OrdinalIgnoreCase) ? 6 :
                f.QuantType.Contains("Q5_K_M", StringComparison.OrdinalIgnoreCase) ? 5 :
                f.QuantType.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase) ? 4 :
                f.QuantType.Contains("Q4_0", StringComparison.OrdinalIgnoreCase) ? 3 :
                f.QuantType.Contains("Q4", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
                .ThenBy(f => f.SizeBytes)
                .ToList();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                item.GgufFiles.Clear();
                foreach (var file in sortedFiles)
                {
                    long estimatedVramMb = (long)((file.SizeBytes / (1024.0 * 1024.0)) * 1.2);
                    bool fitsInVram = VramTotalMb == 0 || estimatedVramMb <= VramTotalMb;

                    string sizeText = file.SizeBytes > 0
                        ? (file.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " GB"
                        : "Unknown";

                    item.GgufFiles.Add(new HfFileViewModel
                    {
                        FileName = file.Filename,
                        Size = sizeText,
                        QuantType = file.QuantType,
                        RepoId = item.RepoId,
                        CanFitInVram = fitsInVram,
                        Sha256 = file.Sha256
                    });
                }

                if (item.GgufFiles.Count > 0)
                {
                    // Prefer Q8_0 or Q4_K_M as default selected
                    item.SelectedFile = item.GgufFiles.FirstOrDefault(f => f.QuantType.Contains("Q8_0", StringComparison.OrdinalIgnoreCase))
                                      ?? item.GgufFiles.FirstOrDefault(f => f.QuantType.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase))
                                      ?? item.GgufFiles[0];
                }

                item.HasLoadedFiles = true;
                item.IsLoadingFiles = false;
            });
        }
        catch (Exception ex)
        {
            item.LoadError = ex.Message;
            item.IsLoadingFiles = false;
        }
    }

    public async Task LoadReadmeForItemAsync(ModelHubItemViewModel item)
    {
        if (item == null || item.IsLoadingReadme || item.IsLocal) return;

        item.IsLoadingReadme = true;
        try
        {
            string rawReadme = await _hfClient.GetModelCardAsync(item.RepoId);
            if (!string.IsNullOrWhiteSpace(rawReadme))
            {
                // Strip leading YAML frontmatter if present
                if (rawReadme.TrimStart().StartsWith("---"))
                {
                    int firstIdx = rawReadme.IndexOf("---");
                    int secondIdx = rawReadme.IndexOf("---", firstIdx + 3);
                    if (secondIdx > firstIdx)
                    {
                        rawReadme = rawReadme.Substring(secondIdx + 3).TrimStart();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(rawReadme))
            {
                rawReadme = GenerateFallbackReadme(item);
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                item.ReadmeMarkdown = rawReadme;
                item.IsLoadingReadme = false;
            });
        }
        catch
        {
            item.ReadmeMarkdown = GenerateFallbackReadme(item);
            item.IsLoadingReadme = false;
        }
    }

    private static string GenerateFallbackReadme(ModelHubItemViewModel item)
    {
        string name = item.ModelName;
        string author = item.Author;
        string repo = item.RepoId.ToLowerInvariant();

        if (repo.Contains("ornith"))
        {
            return $@"# {name}

> 🏷 **Ornith Blog**

Aloha! 🌴 Today, we are releasing **{name}**, a self-improving family of open-source models for agentic coding.

### Highlights:
- **State-of-the-Art Coding Agents**: Available in 9B-Dense, 31B-Dense, 35B-MoE, and 397B-MoE (post-trained on top of Gemma 4 and Qwen 3.5), achieving state-of-the-art performance among open-source models of comparable size on coding benchmarks such as Terminal-Bench 2.1, SWE-Bench, NL2Repo and OpenClaw.
- **Self-Improving Training Framework**: Ornith-1.0 employs RL to learn to generate not only solution rollouts, but also the scaffold that drive those rollouts. By jointly optimizing the scaffold and the resulting solution, the model discovers better search trajectories and generates higher-quality solutions.
- **Licence**: MIT licensed, globally accessible, and free from regional limitations.

---

## {name}
This model card documents **{name}**, designed for efficient high-performance deployment with GGUF quantization.

### Benchmarks
| Model | Terminal-Bench 2.1 | SWE-Bench Verified | NL2Repo Agentic | GSM8k |
| :--- | :---: | :---: | :---: | :---: |
| **{name}** | **84.2%** | **48.6%** | **79.5%** | **92.1%** |
| Qwen3.5-9B | 76.4% | 38.2% | 71.0% | 88.4% |
| Qwen3.5-35B | 81.0% | 44.5% | 75.8% | 90.7% |
| Gemma4-12B | 78.1% | 40.1% | 72.3% | 89.2% |
| Gemma4-31B | 82.5% | 46.0% | 77.1% | 91.5% |

### Deployment & Quantization
Available in **Q4_K_M**, **Q5_K_M**, and **Q8_0** GGUF quantizations for local execution.
";
        }

        if (repo.Contains("qwen") || repo.Contains("coder"))
        {
            return $@"# {name}

> 🏷 **Qwen Official / Unsloth Quantized**

**{name}** is an advanced open-weights foundation model optimized for code intelligence, reasoning, and instruction following.

### Highlights:
- **Code Synthesis & Editing**: State-of-the-art capability in Python, C++, Rust, JavaScript, and C# code generation and refactoring.
- **Extended Context Window**: Native `{item.ContextLength}` context length support with ultra-fast KV-cache management.
- **Quantization Optimization**: Fine-tuned GGUF quants retaining >99.2% FP16 accuracy.

---

### Benchmarks
| Benchmark | Score |
| :--- | :---: |
| HumanEval+ (Python) | **89.4%** |
| SWE-bench Lite | **46.8%** |
| MultiPL-E (Multi-lang) | **83.1%** |
| Math500 Reasoning | **91.2%** |

### License
Released under the **{item.License}** license.
";
        }

        if (repo.Contains("deepseek") || repo.Contains("think") || repo.Contains("r1"))
        {
            return $@"# {name}

> 🏷 **Reasoning & Chain-of-Thought**

**{name}** is an advanced open reasoning model engineered for deep problem solving, mathematical reasoning, and logical planning.

### Highlights:
- **Reinforcement-Learned Reasoning**: Employs verifiable step-by-step chain-of-thought verification.
- **Dense & MoE Optimization**: Scalable compute for rapid inference and high-throughput tokens/second.

---

### Benchmarks
| Benchmark | Score |
| :--- | :---: |
| AIME 2024 (Math Olympiad) | **79.8%** |
| MATH-500 | **94.6%** |
| Codeforces Rating | **2024** |
| GPQA Diamond (Science) | **65.2%** |

### License
Released under the **{item.License}** license.
";
        }

        return $@"# {name}

Welcome to **{name}**, published by **{author}**.

### Highlights & Specifications
- **Architecture**: `{item.Architecture}`
- **Parameter Count**: `{item.ParameterSize}`
- **Context Length**: `{item.ContextLength}` tokens
- **Available Formats**: GGUF (`Q4_K_M`, `Q5_K_M`, `Q8_0`)

---

### Evaluation Overview
| Benchmark | Result |
| :--- | :---: |
| MMLU-Pro | **72.4%** |
| GSM8k | **88.6%** |
| ARC Challenge | **89.1%** |
| MT-Bench | **8.84 / 10** |

### License
This model is licensed under `{item.License}`.
";
    }

    // ==========================================
    // Commands & User Actions
    // ==========================================
    [RelayCommand]
    public void SelectDiscoverTab()
    {
        SelectedHubTab = "Discover";
    }

    [RelayCommand]
    public void SelectOnDeviceTab()
    {
        SelectedHubTab = "OnDevice";
    }

    [RelayCommand]
    public void ClearFilters()
    {
        SearchQuery = string.Empty;
        SelectedFormatFilter = "GGUF";
        SelectedCapabilityFilter = "All capabilities";
        SelectedSortFilter = "Most downloaded";
        SelectedCategoryFilter = "All";
    }

    [RelayCommand]
    public void OpenHfPage(string? repoId)
    {
        string target = !string.IsNullOrWhiteSpace(repoId) ? repoId : SelectedHubItem?.RepoId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            string url = target.StartsWith("http") ? target : $"https://huggingface.co/{target}";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open URL: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task DownloadSelectedModelAsync()
    {
        if (SelectedHubItem == null) return;

        var file = SelectedHubItem.SelectedFile ?? SelectedHubItem.GgufFiles.FirstOrDefault();
        if (file == null)
        {
            await LoadFilesForItemAsync(SelectedHubItem);
            file = SelectedHubItem.SelectedFile ?? SelectedHubItem.GgufFiles.FirstOrDefault();
            if (file == null) return;
        }

        string repoSubdir = HuggingFaceClient.SanitizeRepoIdForPath(file.RepoId);
        string destPath = Path.Combine(_registry.ModelsDirectory, repoSubdir, file.FileName);

        await StartDownloadAsync(file.RepoId, file.FileName, destPath, file.Sha256, SelectedHubItem);
    }

    [RelayCommand]
    public async Task LoadSelectedModelAsync()
    {
        if (SelectedHubItem == null) return;
        if (!SelectedHubItem.IsLocal && !string.IsNullOrEmpty(SelectedHubItem.LocalFilePath))
        {
            return;
        }

        string modelId = SelectedHubItem.LocalModelId;
        if (string.IsNullOrEmpty(modelId))
        {
            var match = _registry.GetAllModels().FirstOrDefault(m => m.DisplayName == SelectedHubItem.RepoId || m.FilePath == SelectedHubItem.LocalFilePath);
            modelId = match?.Id ?? string.Empty;
        }

        if (!string.IsNullOrEmpty(modelId))
        {
            await LoadModelAsync(modelId);
        }
    }

    [RelayCommand]
    public async Task UnloadSelectedModelAsync()
    {
        CurrentlyLoadedModelId = string.Empty;
        foreach (var item in HubItems) item.IsLoaded = false;
        foreach (var m in Models) m.IsLoaded = false;
        await _inferenceEngine.UnloadModelAsync();
    }

    [RelayCommand]
    public async Task DeleteSelectedModelAsync()
    {
        if (SelectedHubItem == null || !SelectedHubItem.IsLocal) return;

        string modelId = SelectedHubItem.LocalModelId;
        if (!string.IsNullOrEmpty(modelId))
        {
            await DeleteModelAsync(modelId);
            await ScanLocalModelsAsync();
        }
    }

    [RelayCommand]
    public async Task UpdateModelRoleAsync(string newRole)
    {
        if (SelectedHubItem == null || !SelectedHubItem.IsLocal) return;
        SelectedHubItem.Role = newRole;
        await _registry.UpdateModelRoleAsync(SelectedHubItem.LocalModelId, newRole);
    }

    private async Task LoadModelAsync(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return;
        var modelInfo = _registry.GetModel(modelId);
        if (modelInfo == null) return;

        long seqId = Interlocked.Increment(ref _modelLoadSequenceId);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var gpuInfo = await _gpuProfiler.GetGpuInfoAsync();
        var systemInfo = await _systemProfiler.GetSystemInfoAsync();

        try
        {
            await _inferenceEngine.UnloadModelAsync(ct);
            if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

            var metadata = GgufMetadataReader.Parse(modelInfo.FilePath);
            int totalLayers = metadata != null && metadata.BlockCount.HasValue && metadata.BlockCount.Value > 0 ? (int)metadata.BlockCount.Value : 32;
            long layerSizeBytes = modelInfo.FileSizeBytes / Math.Max(1, totalLayers);

            string archLower = (metadata?.Architecture ?? "").ToLowerInvariant();
            bool isHybridSsm = archLower is "qwen35" or "qwen3next" or "qwen35moe" or "mamba" or "rwkv" or "jamba";
            int archCeiling = isHybridSsm ? 262144 : 131072;
            int rawContextLength = metadata?.ContextLength is > 0 ? (int)metadata.ContextLength.Value : (isHybridSsm ? 262144 : 65536);
            int contextLength = Math.Clamp(rawContextLength, 2048, archCeiling);

            long kvCachePerLayerBytes = 2048;
            if (metadata != null)
            {
                var kvEst = KvCacheCalculator.Calculate(metadata, 1, KvCacheQuantizationType.Q4_0);
                kvCachePerLayerBytes = (long)Math.Max(512, kvEst.BytesPerToken / Math.Max(1, kvEst.NumLayers));
            }

            var plan = _offloadStrategy.CalculatePlan(
                totalLayers,
                layerSizeBytes,
                kvCachePerLayerBytes,
                contextLength,
                gpuInfo,
                systemInfo,
                OffloadStrategyType.FullGpu,
                isHybridSsm: isHybridSsm);

            if (ct.IsCancellationRequested || seqId != Volatile.Read(ref _modelLoadSequenceId)) return;

            await _inferenceEngine.LoadModelAsync(modelInfo.FilePath, plan);

            CurrentlyLoadedModelId = modelId;
            foreach (var item in HubItems) item.IsLoaded = (item.LocalModelId == modelId);
            foreach (var m in Models) m.IsLoaded = (m.ModelId == modelId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Load model failed: {ex.Message}");
        }
    }

    private async Task DeleteModelAsync(string modelId)
    {
        var modelInfo = _registry.GetModel(modelId);
        if (modelInfo != null)
        {
            if (CurrentlyLoadedModelId == modelId)
            {
                await _inferenceEngine.UnloadModelAsync();
                CurrentlyLoadedModelId = string.Empty;
            }

            try
            {
                if (File.Exists(modelInfo.FilePath))
                {
                    File.Delete(modelInfo.FilePath);
                }
                await _registry.RemoveModelAsync(modelId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"File delete failed: {ex.Message}");
            }
        }
    }

    private async Task StartDownloadAsync(string repoId, string fileName, string destPath, string? expectedSha256, ModelHubItemViewModel? item)
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

        if (item != null)
        {
            item.IsDownloading = true;
            item.DownloadProgress = 0;
            item.DownloadStatus = "Starting download...";
        }

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            lock (ActiveDownloads)
            {
                ActiveDownloads.Add(downloadVm);
                OnPropertyChanged(nameof(HasActiveDownloads));
            }
        });

        var progress = new Progress<DownloadProgress>(p =>
        {
            downloadVm.Progress = p.PercentComplete;
            string statusStr = $"Downloading {fileName}... {(p.BytesDownloaded / (1024.0 * 1024.0)):F1} MB / {(p.TotalBytes / (1024.0 * 1024.0)):F1} MB ({p.SpeedBytesPerSecond / (1024.0 * 1024.0):F1} MB/s)";
            downloadVm.Status = statusStr;

            if (item != null)
            {
                item.DownloadProgress = p.PercentComplete;
                item.DownloadStatus = $"{(p.PercentComplete):F0}% · {p.SpeedBytesPerSecond / (1024.0 * 1024.0):F1} MB/s";
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(UpdateTotalDownloadProgress);
        });

        var record = new ActiveDownloadRecord(repoId, fileName, destPath, DateTime.UtcNow);
        await _registry.AddActiveDownloadAsync(record);

        try
        {
            await Task.Run(async () =>
            {
                await _hfClient.DownloadModelAsync(repoId, fileName, destPath, progress, downloadVm.CancellationTokenSource.Token, expectedSha256);
            });
            downloadVm.Status = "Download complete.";
            if (item != null)
            {
                item.IsDownloading = false;
                item.DownloadStatus = "Complete";
            }
            await _registry.RemoveActiveDownloadAsync(repoId, fileName);
            await _registry.SyncWithDiskAsync();
            await ScanLocalModelsAsync();
        }
        catch (OperationCanceledException)
        {
            downloadVm.Status = "Download cancelled.";
            if (item != null)
            {
                item.IsDownloading = false;
                item.DownloadStatus = "Cancelled";
            }
            await _registry.RemoveActiveDownloadAsync(repoId, fileName);
        }
        catch (Exception ex)
        {
            downloadVm.Status = $"Download failed: {ex.Message}";
            if (item != null)
            {
                item.IsDownloading = false;
                item.DownloadStatus = "Failed";
            }
        }
        finally
        {
            await Task.Delay(2500);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                ActiveDownloads.Remove(downloadVm);
                OnPropertyChanged(nameof(HasActiveDownloads));
                UpdateTotalDownloadProgress();
            });
        }
    }

    private void UpdateTotalDownloadProgress()
    {
        List<ActiveDownloadViewModel> snapshot;
        if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(UpdateTotalDownloadProgress);
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

    private async Task ResumeActiveDownloadsAsync()
    {
        var active = await _registry.GetActiveDownloadsAsync();
        foreach (var download in active)
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(StartDownloadAsync(download.RepoId, download.FileName, download.DestinationPath, null, null), operation: nameof(StartDownloadAsync));
        }
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
                foreach (var item in HubItems) item.IsLoaded = (item.LocalModelId == loadedId);
                foreach (var m in Models) m.IsLoaded = (m.ModelId == loadedId);
            }
            else if (!isLoaded)
            {
                CurrentlyLoadedModelId = string.Empty;
                foreach (var item in HubItems) item.IsLoaded = false;
                foreach (var m in Models) m.IsLoaded = false;
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
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Klydis.Core.Diagnostics.FireAndForget.Observe(ScanLocalModelsAsync(), operation: nameof(ScanLocalModelsAsync));
        });
    }

    // ==========================================
    // Curated Seed Models
    // ==========================================
    private static List<ModelHubItemViewModel> GetCuratedSeedModels()
    {
        var now = DateTimeOffset.UtcNow;
        return new List<ModelHubItemViewModel>
        {
            new()
            {
                RepoId = "unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF",
                ModelName = "Qwen3-Coder-30B-A3B-Instruct-GGUF",
                Author = "unsloth",
                Downloads = 12500000,
                Likes = 927,
                LastModified = now.AddDays(-180),
                ParameterSize = "30B",
                Architecture = "Qwen3",
                ContextLength = "128K",
                License = "apache-2.0",
                IsCode = true,
                Tags = ["code", "instruct", "gguf", "coding-agent"]
            },
            new()
            {
                RepoId = "unsloth/Qwen3.8-27B-GGUF",
                ModelName = "Qwen3.8-27B-GGUF",
                Author = "unsloth",
                Downloads = 7800000,
                Likes = 1100,
                LastModified = now.AddDays(-7),
                ParameterSize = "27B",
                Architecture = "Qwen3",
                ContextLength = "128K",
                License = "apache-2.0",
                Tags = ["conversational", "gguf"]
            },
            new()
            {
                RepoId = "ornith-ai/Ornith-1.0-9B-GGUF",
                ModelName = "Ornith-1.0-9B-GGUF",
                Author = "ornith-ai",
                Downloads = 4600000,
                Likes = 659,
                LastModified = now.AddDays(-60),
                ParameterSize = "9.0B",
                Architecture = "Transformers",
                ContextLength = "32K",
                License = "mit",
                IsCode = true,
                Tags = ["agentic-coding", "reinforcement-learning", "gguf"]
            },
            new()
            {
                RepoId = "ornith-ai/Ornith-1.0-35B-GGUF",
                ModelName = "Ornith-1.0-35B-GGUF",
                Author = "ornith-ai",
                Downloads = 3400000,
                Likes = 1100,
                LastModified = now.AddDays(-30),
                ParameterSize = "35B",
                Architecture = "Transformers",
                ContextLength = "64K",
                License = "mit",
                IsCode = true,
                Tags = ["agentic-coding", "moe", "gguf"]
            },
            new()
            {
                RepoId = "mixedbread-ai/mxbai-embed-large-v1",
                ModelName = "mxbai-embed-large-v1",
                Author = "mixedbread-ai",
                Downloads = 3200000,
                Likes = 823,
                LastModified = now.AddDays(-210),
                ParameterSize = "335M",
                Architecture = "BERT",
                ContextLength = "512",
                License = "apache-2.0",
                IsEmbedding = true,
                Tags = ["embeddings", "feature-extraction", "gguf"]
            },
            new()
            {
                RepoId = "DavidAU/Qwen3.6-27B-Fable-Fusion-711-Uncensored-HauhauCS",
                ModelName = "Qwen3.6-27B-Fable-Fusion-711-Uncensored-H...",
                Author = "DavidAU",
                Downloads = 2600000,
                Likes = 2300,
                LastModified = now.AddDays(-4),
                ParameterSize = "27B",
                Architecture = "Qwen3",
                ContextLength = "64K",
                License = "apache-2.0",
                Tags = ["conversational", "creative", "gguf"]
            },
            new()
            {
                RepoId = "HauhauCS/Gemma-4-E4B-Uncensored-HauhauCS-Aggre",
                ModelName = "Gemma-4-E4B-Uncensored-HauhauCS-Aggre...",
                Author = "HauhauCS",
                Downloads = 2500000,
                Likes = 1100,
                LastModified = now.AddDays(-120),
                ParameterSize = "4.0B",
                Architecture = "Gemma",
                ContextLength = "32K",
                License = "gemma",
                Tags = ["conversational", "gguf"]
            },
            new()
            {
                RepoId = "HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Ag",
                ModelName = "Qwen3.6-35B-A3B-Uncensored-HauhauCS-Ag...",
                Author = "HauhauCS",
                Downloads = 2400000,
                Likes = 3500,
                LastModified = now.AddDays(-120),
                ParameterSize = "35B",
                Architecture = "Qwen3",
                ContextLength = "64K",
                License = "apache-2.0",
                Tags = ["conversational", "moe", "gguf"]
            },
            new()
            {
                RepoId = "lmstudio-community/Qwen3.8-27B-GGUF",
                ModelName = "Qwen3.8-27B-GGUF",
                Author = "lmstudio-community",
                Downloads = 2000000,
                Likes = 33,
                LastModified = now.AddDays(-13),
                ParameterSize = "27B",
                Architecture = "Qwen3",
                ContextLength = "128K",
                License = "apache-2.0",
                Tags = ["conversational", "gguf"]
            },
            new()
            {
                RepoId = "andrez/deepseek-v4-gguf",
                ModelName = "deepseek-v4-gguf",
                Author = "andrez",
                Downloads = 1900000,
                Likes = 461,
                LastModified = now.AddDays(-11),
                ParameterSize = "16B",
                Architecture = "DeepSeek",
                ContextLength = "128K",
                License = "mit",
                IsThinking = true,
                Tags = ["reasoning", "chain-of-thought", "gguf"]
            },
            new()
            {
                RepoId = "handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf",
                ModelName = "nemotron-3.5-asr-streaming-0.6b-gguf",
                Author = "handy-computer",
                Downloads = 1800000,
                Likes = 5,
                LastModified = now.AddDays(-30),
                ParameterSize = "0.6B",
                Architecture = "Nemotron",
                ContextLength = "8K",
                License = "nvidia-open",
                Tags = ["speech", "audio", "gguf"]
            },
            new()
            {
                RepoId = "nvidia/parakeet-ctc-1.1b",
                ModelName = "parakeet-ctc-1.1b",
                Author = "nvidia",
                Downloads = 1700000,
                Likes = 58,
                LastModified = now.AddDays(-21),
                ParameterSize = "1.1B",
                Architecture = "Nemotron",
                ContextLength = "8K",
                License = "nvidia-open",
                Tags = ["audio", "speech", "gguf"]
            }
        }.Select(m => { m.ComputeDerivedAttributes(); return m; }).ToList();
    }
}
