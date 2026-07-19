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

public partial class SettingsViewModel : ObservableObject
{
    private readonly ThemeService _themeService;

    [ObservableProperty]
    private SettingsCategory _selectedCategory = SettingsCategory.Appearance;

    [ObservableProperty]
    private ThemeMode _selectedMode;

    [ObservableProperty]
    private AccentTheme _selectedAccent;

    public ObservableCollection<AccentSwatch> AccentSwatches { get; } = new();

    public string AppVersion { get; }
    public string AppDescription { get; }

    public SettingsViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        _selectedMode = themeService.CurrentMode;
        _selectedAccent = themeService.CurrentAccent;

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
}
