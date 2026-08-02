using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.App.Services;
using Klydis.Core.Models;

namespace Klydis.App.ViewModels;

public enum SettingsCategory
{
    Appearance,
    ModelSettings,
    About
}

public record DraftModelOption(string DisplayName, string FilePath);
public record ContextSizeOption(string DisplayName, int Value);
public record ContextSizeBucket(int Value, string Label, string ShortLabel);
public record BatchSizeOption(string DisplayName, int Value);

/// <summary>
/// One tile in the Themes gallery. <see cref="Swatch"/> is a fixed preview color
/// (the theme's bright/dark-mode shade) shown regardless of the active mode, so the
/// gallery previews color identity consistently.
/// </summary>
public partial class AccentSwatch : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public Brush Swatch { get; init; } = Brushes.Gray;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One tile in the Background gallery. <see cref="Swatch"/> is a fixed preview color.
/// </summary>
public partial class BackgroundSwatch : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public Brush Swatch { get; init; } = Brushes.Gray;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly Klydis.Core.Inference.InferenceEngine _inferenceEngine;
    private readonly ModelRegistry? _modelRegistry;
    private readonly Klydis.Core.Chat.ChatEngine? _chatEngine;

    [ObservableProperty]
    private SettingsCategory _selectedCategory = SettingsCategory.Appearance;

    [ObservableProperty]
    private ThemeMode _selectedMode;

    [ObservableProperty]
    private AccentTheme _selectedAccent;

    [ObservableProperty]
    private BackgroundTheme _selectedBackground;

    [ObservableProperty]
    private bool _isSpeculativeDecodingEnabled = true;

    [ObservableProperty]
    private int _speculativeDraftCount = 24;

    [ObservableProperty]
    private string _selectedDraftModelPath = "auto";

    [ObservableProperty]
    private string _selectedPersonality = "Default";

    [ObservableProperty]
    private string _selectedPersonalityDescription = "Standard model output style without personality overrides.";

    public static readonly ContextSizeBucket[] ContextBuckets = new[]
    {
        new ContextSizeBucket(0, "Auto (Smart Hardware Allocation)", "Auto"),
        new ContextSizeBucket(1024, "1,024 tokens (1K)", "1K"),
        new ContextSizeBucket(2048, "2,048 tokens (2K)", "2K"),
        new ContextSizeBucket(4096, "4,096 tokens (4K)", "4K"),
        new ContextSizeBucket(8192, "8,192 tokens (8K)", "8K"),
        new ContextSizeBucket(16384, "16,384 tokens (16K)", "16K"),
        new ContextSizeBucket(32768, "32,768 tokens (32K)", "32K"),
        new ContextSizeBucket(65536, "65,536 tokens (64K)", "64K"),
        new ContextSizeBucket(131072, "131,072 tokens (128K)", "128K"),
        new ContextSizeBucket(262144, "262,144 tokens (256K)", "256K"),
        new ContextSizeBucket(524288, "524,288 tokens (512K)", "512K"),
        new ContextSizeBucket(1000000, "1,000,000 tokens (1 Million)", "1M")
    };

    [ObservableProperty]
    private int _selectedContextSize = 0;

    [ObservableProperty]
    private int _contextSliderIndex = 0;

    [ObservableProperty]
    private string _selectedContextSizeFormatted = "Auto (Smart Hardware Allocation)";

    [ObservableProperty]
    private string _speculativeStatusMessage = "Speculative decoding active.";

    [ObservableProperty]
    private int _selectedBatchSize = 0;

    [ObservableProperty]
    private int _selectedUBatchSize = 0;

    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new();
    public ObservableCollection<BackgroundSwatch> BackgroundSwatches { get; } = new();
    public ObservableCollection<DraftModelOption> AvailableDraftModels { get; } = new();
    public ObservableCollection<string> AvailablePersonalities { get; } = new();
    public ObservableCollection<ContextSizeOption> AvailableContextSizes { get; } = new();
    public ObservableCollection<BatchSizeOption> AvailableBatchSizes { get; } = new();
    public ObservableCollection<BatchSizeOption> AvailableUBatchSizes { get; } = new();

    public string AppVersion { get; }
    public string AppDescription { get; }

    public SettingsViewModel(
        ThemeService themeService, 
        Klydis.Core.Inference.InferenceEngine inferenceEngine,
        ModelRegistry? modelRegistry = null,
        Klydis.Core.Chat.ChatEngine? chatEngine = null)
    {
        _themeService = themeService;
        _inferenceEngine = inferenceEngine;
        _modelRegistry = modelRegistry;
        _chatEngine = chatEngine;

        _selectedMode = themeService.CurrentMode;
        _selectedAccent = themeService.CurrentAccent;
        _selectedBackground = themeService.CurrentBackground;
        _selectedPersonality = themeService.SelectedPersonality;
        _selectedContextSize = themeService.UserContextLimit;
        _inferenceEngine.UserContextLimit = (uint)_selectedContextSize;

        _selectedBatchSize = themeService.UserBatchSize;
        _selectedUBatchSize = themeService.UserUBatchSize;
        _inferenceEngine.UserBatchSize = (uint)_selectedBatchSize;
        _inferenceEngine.UserUBatchSize = (uint)_selectedUBatchSize;

        PopulateBatchSizeOptions();

        int closestIdx = 0;
        int minDiff = int.MaxValue;
        for (int i = 0; i < ContextBuckets.Length; i++)
        {
            int diff = Math.Abs(ContextBuckets[i].Value - _selectedContextSize);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestIdx = i;
            }
        }
        _contextSliderIndex = closestIdx;
        _selectedContextSizeFormatted = ContextBuckets[closestIdx].Label;

        if (_chatEngine != null)
        {
            _chatEngine.SelectedPersonality = _selectedPersonality;
        }

        IsSpeculativeDecodingEnabled = themeService.IsSpeculativeDecodingEnabled;
        SpeculativeDraftCount = themeService.SpeculativeDraftCount;
        SelectedDraftModelPath = themeService.SelectedDraftModelPath;
        _inferenceEngine.SelectedDraftModelPath = SelectedDraftModelPath;
        SpeculativeStatusMessage = inferenceEngine.SpeculativeStatus;

        RefreshAvailableDraftModels();
        RefreshAvailablePersonalities();

        if (_modelRegistry != null)
        {
            _modelRegistry.RegistryChanged += () =>
            {
                if (System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshAvailableDraftModels);
                }
                else
                {
                    RefreshAvailableDraftModels();
                }
            };
        }

        _inferenceEngine.SpeculativeStatusChanged += (status) =>
        {
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    SpeculativeStatusMessage = status;
                });
            }
        };

        foreach (var (name, hex) in new[]
                 {
                     ("Fluorescent", "#50E8F4"),
                     ("Violet", "#B18CFF"),
                     ("Amber", "#FFC24B"),
                     ("Rose", "#FF8FB3"),
                     ("Forest", "#7BE39B")
                 })
        {
            var accent = System.Enum.Parse<AccentTheme>(name);
            AccentSwatches.Add(new AccentSwatch
            {
                Name = name,
                Swatch = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                IsSelected = accent == _selectedAccent
            });
        }

        foreach (var (name, hex) in new[]
                 {
                     ("Ocean", "#001619"),
                     ("Obsidian", "#0D0D0D"),
                     ("Midnight", "#000B18")
                 })
        {
            var bg = System.Enum.Parse<BackgroundTheme>(name);
            BackgroundSwatches.Add(new BackgroundSwatch
            {
                Name = name,
                Swatch = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                IsSelected = bg == _selectedBackground
            });
        }

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        AppVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "dev";
        AppDescription = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description
            ?? "Self-contained local LLM orchestrator.";
    }

    private void RefreshAvailableDraftModels()
    {
        AvailableDraftModels.Clear();
        AvailableDraftModels.Add(new DraftModelOption("Auto (Smallest Model)", "auto"));

        if (_modelRegistry != null)
        {
            var models = _modelRegistry.GetAllModels()
                .Where(m => File.Exists(m.FilePath))
                .OrderBy(m => m.FileSizeBytes);

            foreach (var m in models)
            {
                double sizeGb = m.FileSizeBytes / (1024.0 * 1024.0 * 1024.0);
                AvailableDraftModels.Add(new DraftModelOption($"{m.DisplayName} ({sizeGb:F2} GB)", m.FilePath));
            }
        }
    }

    [RelayCommand]
    public void RefreshPersonalities()
    {
        RefreshAvailablePersonalities();
    }

    private void RefreshAvailablePersonalities()
    {
        AvailablePersonalities.Clear();
        var personalities = Klydis.Core.Chat.SystemPromptManager.GetAvailablePersonalities();
        foreach (var p in personalities)
        {
            AvailablePersonalities.Add(p);
        }

        if (!AvailablePersonalities.Contains(SelectedPersonality) && AvailablePersonalities.Count > 0)
        {
            SelectedPersonality = AvailablePersonalities[0];
        }
        else
        {
            UpdatePersonalityDescription(SelectedPersonality);
        }
    }

    partial void OnSelectedPersonalityChanged(string value)
    {
        string pName = string.IsNullOrWhiteSpace(value) ? "Default" : value;
        _themeService.SavePersonalitySetting(pName);
        if (_chatEngine != null)
        {
            _chatEngine.SelectedPersonality = pName;
        }
        _inferenceEngine.ResetContext();
        UpdatePersonalityDescription(pName);
    }

    private void UpdatePersonalityDescription(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            SelectedPersonalityDescription = "Standard model output style without personality overrides.";
            return;
        }

        var prompt = Klydis.Core.Chat.SystemPromptManager.GetPersonalityPrompt(name);
        SelectedPersonalityDescription = !string.IsNullOrWhiteSpace(prompt)
            ? prompt
            : $"Custom prompt directives for {name}.";
    }

    [RelayCommand]
    private void NavigateSettings(string category)
    {
        if (System.Enum.TryParse<SettingsCategory>(category, out var target))
        {
            SelectedCategory = target;
        }
    }

    [RelayCommand]
    private void SelectMode(string modeName)
    {
        if (System.Enum.TryParse<ThemeMode>(modeName, out var mode) && mode != SelectedMode)
        {
            SelectedMode = mode;
            _themeService.ApplyMode(mode);
        }
    }

    [RelayCommand]
    private void SelectAccent(string accentName)
    {
        if (!System.Enum.TryParse<AccentTheme>(accentName, out var accent) || accent == SelectedAccent)
        {
            return;
        }

        SelectedAccent = accent;
        _themeService.ApplyAccent(accent);

        foreach (var swatch in AccentSwatches)
        {
            swatch.IsSelected = swatch.Name == accentName;
        }
    }

    [RelayCommand]
    private void SelectBackground(string backgroundName)
    {
        if (!System.Enum.TryParse<BackgroundTheme>(backgroundName, out var bg) || bg == SelectedBackground)
        {
            return;
        }

        SelectedBackground = bg;
        _themeService.ApplyBackground(bg);

        foreach (var swatch in BackgroundSwatches)
        {
            swatch.IsSelected = swatch.Name == backgroundName;
        }
    }

    partial void OnSelectedDraftModelPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedDraftModelPath = "auto";
            return;
        }

        string draftPath = value;
        _themeService.SaveSpeculativeSettings(IsSpeculativeDecodingEnabled, SpeculativeDraftCount, draftPath);
        _inferenceEngine.SelectedDraftModelPath = draftPath;

        if (!string.IsNullOrEmpty(_inferenceEngine.CurrentModelPath))
        {
            var modelPath = _inferenceEngine.CurrentModelPath;
            _ = Task.Run(async () => await _inferenceEngine.AttachSpeculativeDraftAsync(modelPath));
        }
    }

    partial void OnIsSpeculativeDecodingEnabledChanged(bool value)
    {
        _themeService.SaveSpeculativeSettings(value, SpeculativeDraftCount, SelectedDraftModelPath);
        _inferenceEngine.IsSpeculativeDecodingEnabled = value;

        if (!string.IsNullOrEmpty(_inferenceEngine.CurrentModelPath))
        {
            var modelPath = _inferenceEngine.CurrentModelPath;
            _ = Task.Run(async () => await _inferenceEngine.AttachSpeculativeDraftAsync(modelPath));
        }
    }

    partial void OnSpeculativeDraftCountChanged(int value)
    {
        int clamped = Math.Clamp(value, 4, 32);
        _themeService.SaveSpeculativeSettings(IsSpeculativeDecodingEnabled, clamped, SelectedDraftModelPath);
        _inferenceEngine.SpeculativeDraftCount = clamped;
        _inferenceEngine.SpeculativeEngine.DraftCandidateCount = clamped;
    }

    private void PopulateContextSizeOptions()
    {
        AvailableContextSizes.Clear();
        AvailableContextSizes.Add(new ContextSizeOption("Auto (Smart Hardware Allocation)", 0));
        AvailableContextSizes.Add(new ContextSizeOption("8,192 tokens (8K)", 8192));
        AvailableContextSizes.Add(new ContextSizeOption("16,384 tokens (16K)", 16384));
        AvailableContextSizes.Add(new ContextSizeOption("32,768 tokens (32K)", 32768));
        AvailableContextSizes.Add(new ContextSizeOption("65,536 tokens (64K)", 65536));
        AvailableContextSizes.Add(new ContextSizeOption("131,072 tokens (128K)", 131072));
        AvailableContextSizes.Add(new ContextSizeOption("262,144 tokens (256K)", 262144));
        AvailableContextSizes.Add(new ContextSizeOption("524,288 tokens (512K)", 524288));
        AvailableContextSizes.Add(new ContextSizeOption("1,048,576 tokens (1 Million)", 1048576));
    }

    partial void OnSelectedContextSizeChanged(int value)
    {
        _themeService.SaveContextSizeSetting(value);
        _inferenceEngine.UserContextLimit = (uint)value;

        int closestIdx = 0;
        int minDiff = int.MaxValue;
        for (int i = 0; i < ContextBuckets.Length; i++)
        {
            int diff = Math.Abs(ContextBuckets[i].Value - value);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestIdx = i;
            }
        }
        if (_contextSliderIndex != closestIdx)
        {
            _contextSliderIndex = closestIdx;
            OnPropertyChanged(nameof(ContextSliderIndex));
        }
        SelectedContextSizeFormatted = ContextBuckets[closestIdx].Label;
        if (_inferenceEngine.IsModelLoaded)
        {
            _ = Task.Run(async () => await _inferenceEngine.ReapplyModelParametersAsync());
        }
    }

    partial void OnContextSliderIndexChanged(int value)
    {
        int clampedIdx = Math.Clamp(value, 0, ContextBuckets.Length - 1);
        var bucket = ContextBuckets[clampedIdx];
        if (SelectedContextSize != bucket.Value)
        {
            SelectedContextSize = bucket.Value;
        }
    }

    private void PopulateBatchSizeOptions()
    {
        AvailableBatchSizes.Clear();
        AvailableBatchSizes.Add(new BatchSizeOption("Auto (Smart Hardware Allocation)", 0));
        AvailableBatchSizes.Add(new BatchSizeOption("512 tokens (Low Memory)", 512));
        AvailableBatchSizes.Add(new BatchSizeOption("1,024 tokens (Standard)", 1024));
        AvailableBatchSizes.Add(new BatchSizeOption("2,048 tokens (High Throughput)", 2048));
        AvailableBatchSizes.Add(new BatchSizeOption("4,096 tokens (Maximum Prefill)", 4096));
        AvailableBatchSizes.Add(new BatchSizeOption("8,192 tokens (Extreme Speed)", 8192));

        AvailableUBatchSizes.Clear();
        AvailableUBatchSizes.Add(new BatchSizeOption("Auto (Smart Tensor Core Allocation)", 0));
        AvailableUBatchSizes.Add(new BatchSizeOption("128 tokens (Low VRAM)", 128));
        AvailableUBatchSizes.Add(new BatchSizeOption("256 tokens (Balanced)", 256));
        AvailableUBatchSizes.Add(new BatchSizeOption("512 tokens (Tensor Core Default)", 512));
        AvailableUBatchSizes.Add(new BatchSizeOption("1,024 tokens (Large Batch)", 1024));
        AvailableUBatchSizes.Add(new BatchSizeOption("2,048 tokens (Max Micro-Batch)", 2048));
    }

    partial void OnSelectedBatchSizeChanged(int value)
    {
        _themeService.SaveBatchProcessingSizeSetting(value, SelectedUBatchSize);
        _inferenceEngine.UserBatchSize = (uint)value;
        if (_inferenceEngine.IsModelLoaded)
        {
            _ = Task.Run(async () => await _inferenceEngine.ReapplyModelParametersAsync());
        }
    }

    partial void OnSelectedUBatchSizeChanged(int value)
    {
        _themeService.SaveBatchProcessingSizeSetting(SelectedBatchSize, value);
        _inferenceEngine.UserUBatchSize = (uint)value;
        if (_inferenceEngine.IsModelLoaded)
        {
            _ = Task.Run(async () => await _inferenceEngine.ReapplyModelParametersAsync());
        }
    }
}
