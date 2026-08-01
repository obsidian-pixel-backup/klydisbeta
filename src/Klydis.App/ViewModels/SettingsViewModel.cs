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
    Inference,
    About
}

public record DraftModelOption(string DisplayName, string FilePath);
public record ContextSizeOption(string DisplayName, int Value);

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
    private int _selectedContextSize = 0;

    [ObservableProperty]
    private string _speculativeStatusMessage = "Speculative decoding active.";

    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new();
    public ObservableCollection<BackgroundSwatch> BackgroundSwatches { get; } = new();
    public ObservableCollection<DraftModelOption> AvailableDraftModels { get; } = new();
    public ObservableCollection<string> AvailablePersonalities { get; } = new();
    public ObservableCollection<ContextSizeOption> AvailableContextSizes { get; } = new();

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
    }

    partial void OnSelectedPersonalityChanged(string value)
    {
        string pName = string.IsNullOrWhiteSpace(value) ? "Default" : value;
        _themeService.SavePersonalitySetting(pName);
        if (_chatEngine != null)
        {
            _chatEngine.SelectedPersonality = pName;
        }
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
    }
}
