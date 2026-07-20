using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klydis.App.Services;

namespace Klydis.App.ViewModels;

public enum SettingsCategory
{
    Appearance,
    Inference,
    About
}

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
    private string _speculativeStatusMessage = "Speculative decoding active.";

    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new();
    public ObservableCollection<BackgroundSwatch> BackgroundSwatches { get; } = new();

    public string AppVersion { get; }
    public string AppDescription { get; }

    public SettingsViewModel(ThemeService themeService, Klydis.Core.Inference.InferenceEngine inferenceEngine)
    {
        _themeService = themeService;
        _inferenceEngine = inferenceEngine;

        _selectedMode = themeService.CurrentMode;
        _selectedAccent = themeService.CurrentAccent;
        _selectedBackground = themeService.CurrentBackground;

        IsSpeculativeDecodingEnabled = themeService.IsSpeculativeDecodingEnabled;
        SpeculativeDraftCount = themeService.SpeculativeDraftCount;
        SpeculativeStatusMessage = inferenceEngine.SpeculativeStatus;

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

    partial void OnIsSpeculativeDecodingEnabledChanged(bool value)
    {
        _themeService.SaveSpeculativeSettings(value, SpeculativeDraftCount);
        _inferenceEngine.IsSpeculativeDecodingEnabled = value;

        if (!string.IsNullOrEmpty(_inferenceEngine.CurrentModelPath))
        {
            _ = _inferenceEngine.AttachSpeculativeDraftAsync(_inferenceEngine.CurrentModelPath);
        }
    }

    partial void OnSpeculativeDraftCountChanged(int value)
    {
        int clamped = Math.Clamp(value, 4, 32);
        _themeService.SaveSpeculativeSettings(IsSpeculativeDecodingEnabled, clamped);
        _inferenceEngine.SpeculativeDraftCount = clamped;
        _inferenceEngine.SpeculativeEngine.DraftCandidateCount = clamped;
    }
}
