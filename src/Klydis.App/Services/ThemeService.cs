using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Klydis.App.Services;

/// <summary>
/// Light/dark mode. Independent of <see cref="AccentTheme"/> — Windows calls this
/// "mode" (its Settings &gt; Personalization &gt; Colors &gt; "Choose your mode").
/// </summary>
public enum ThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>
/// The accent color identity applied on top of the active mode. Windows calls the
/// equivalent concept a "theme" (its gallery of color/wallpaper combinations).
/// </summary>
public enum AccentTheme
{
    Fluorescent,
    Violet,
    Amber,
    Rose,
    Forest
}

/// <summary>
/// Composes the active palette from two independent, hot-swappable
/// ResourceDictionaries — a mode (neutrals) and an accent (brand color) — and
/// persists both choices. App-layer only: reads/writes a small JSON file under
/// the user's LocalAppData, never touches Klydis.Core.
/// </summary>
public class ThemeService
{
    // App.xaml merges dictionaries in this fixed order: [mode, accent, styles].
    private const int ModeDictionaryIndex = 0;
    private const int AccentDictionaryIndex = 1;

    private readonly string _settingsPath;

    /// <summary>The user's mode selection, which may be <see cref="ThemeMode.System"/>.</summary>
    public ThemeMode CurrentMode { get; private set; } = ThemeMode.Dark;

    /// <summary>The actually-applied mode (Dark or Light) after resolving System.</summary>
    public ThemeMode EffectiveMode { get; private set; } = ThemeMode.Dark;

    public AccentTheme CurrentAccent { get; private set; } = AccentTheme.Fluorescent;

    public event Action? AppearanceChanged;

    public ThemeService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klydis");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "ui-settings.json");
    }

    /// <summary>
    /// Loads the persisted appearance (if any) and applies it. Call before the main
    /// window is shown so there is no flash of the wrong palette.
    /// </summary>
    public void LoadAndApplyPersistedTheme()
    {
        var mode = ThemeMode.Dark;
        var accent = AccentTheme.Fluorescent;
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<UiSettings>(json);
                if (settings != null)
                {
                    if (Enum.TryParse<ThemeMode>(settings.Mode, out var parsedMode)) mode = parsedMode;
                    if (Enum.TryParse<AccentTheme>(settings.Accent, out var parsedAccent)) accent = parsedAccent;
                }
            }
        }
        catch
        {
            // Corrupt or missing settings file: fall back to the defaults.
        }

        Apply(mode, accent, persist: false);
    }

    public void ApplyMode(ThemeMode mode) => Apply(mode, CurrentAccent, persist: true);

    public void ApplyAccent(AccentTheme accent) => Apply(CurrentMode, accent, persist: true);

    /// <summary>
    /// Re-resolves and re-applies the palette if the current mode selection is
    /// System. Call when the OS theme may have changed while the app is open.
    /// </summary>
    public void RefreshIfFollowingSystem()
    {
        if (CurrentMode == ThemeMode.System)
        {
            Apply(ThemeMode.System, CurrentAccent, persist: false);
        }
    }

    private void Apply(ThemeMode mode, AccentTheme accent, bool persist)
    {
        var effectiveMode = mode == ThemeMode.System
            ? (IsSystemInLightMode() ? ThemeMode.Light : ThemeMode.Dark)
            : mode;

        var modeUri = new Uri($"Themes/Modes/{effectiveMode}.xaml", UriKind.Relative);
        var accentUri = new Uri($"Themes/Accents/{accent}{effectiveMode}.xaml", UriKind.Relative);

        var merged = Application.Current.Resources.MergedDictionaries;
        merged[ModeDictionaryIndex] = new ResourceDictionary { Source = modeUri };
        merged[AccentDictionaryIndex] = new ResourceDictionary { Source = accentUri };

        CurrentMode = mode;
        EffectiveMode = effectiveMode;
        CurrentAccent = accent;
        AppearanceChanged?.Invoke();

        if (persist)
        {
            try
            {
                var json = JsonSerializer.Serialize(new UiSettings { Mode = mode.ToString(), Accent = accent.ToString() });
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Non-fatal: the appearance is still applied for this session.
            }
        }
    }

    private static bool IsSystemInLightMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private class UiSettings
    {
        public string Mode { get; set; } = "Dark";
        public string Accent { get; set; } = "Fluorescent";
    }
}
