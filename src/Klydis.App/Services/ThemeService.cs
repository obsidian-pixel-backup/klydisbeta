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
/// The background color identity applied underneath the active mode.
/// </summary>
public enum BackgroundTheme
{
    Ocean,
    Obsidian,
    Midnight
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

    public BackgroundTheme CurrentBackground { get; private set; } = BackgroundTheme.Ocean;

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
        var background = BackgroundTheme.Ocean;
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
                    if (Enum.TryParse<BackgroundTheme>(settings.Background, out var parsedBackground)) background = parsedBackground;
                    if (Enum.TryParse<AccentTheme>(settings.Accent, out var parsedAccent)) accent = parsedAccent;
                    IsSpeculativeDecodingEnabled = settings.IsSpeculativeDecodingEnabled;
                    SpeculativeDraftCount = settings.SpeculativeDraftCount > 0 ? Math.Clamp(settings.SpeculativeDraftCount, 4, 32) : 24;
                    SelectedDraftModelPath = string.IsNullOrWhiteSpace(settings.SelectedDraftModelPath) ? "auto" : settings.SelectedDraftModelPath;
                    SelectedPersonality = string.IsNullOrWhiteSpace(settings.SelectedPersonality) ? "Default" : settings.SelectedPersonality;
                    // 0 is a legitimate persisted value (user selected "Auto (Smart Hardware
                    // Allocation)"); only an unset key (-1 sentinel from pre-Auto versions)
                    // falls back to the 64K default.
                    UserContextLimit = settings.UserContextLimit >= 0 ? settings.UserContextLimit : 65536;
                    UserBatchSize = settings.UserBatchSize;
                    UserUBatchSize = settings.UserUBatchSize;
                }
            }
        }
        catch
        {
            // Corrupt or missing settings file: fall back to the defaults.
        }

        Apply(mode, background, accent, persist: false);
    }

    public void ApplyMode(ThemeMode mode) => Apply(mode, CurrentBackground, CurrentAccent, persist: true);

    public void ApplyBackground(BackgroundTheme background) => Apply(CurrentMode, background, CurrentAccent, persist: true);

    public void ApplyAccent(AccentTheme accent) => Apply(CurrentMode, CurrentBackground, accent, persist: true);

    /// <summary>
    /// Re-resolves and re-applies the palette if the current mode selection is
    /// System. Call when the OS theme may have changed while the app is open.
    /// </summary>
    public void RefreshIfFollowingSystem()
    {
        if (CurrentMode == ThemeMode.System)
        {
            Apply(ThemeMode.System, CurrentBackground, CurrentAccent, persist: false);
        }
    }

    private void Apply(ThemeMode mode, BackgroundTheme background, AccentTheme accent, bool persist)
    {
        var effectiveMode = mode == ThemeMode.System
            ? (IsSystemInLightMode() ? ThemeMode.Light : ThemeMode.Dark)
            : mode;

        var backgroundUri = new Uri($"pack://application:,,,/Themes/Backgrounds/{background}{effectiveMode}.xaml", UriKind.Absolute);
        var accentUri = new Uri($"pack://application:,,,/Themes/Accents/{accent}{effectiveMode}.xaml", UriKind.Absolute);

        if (Application.Current != null)
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > AccentDictionaryIndex)
            {
                merged[ModeDictionaryIndex] = new ResourceDictionary { Source = backgroundUri };
                merged[AccentDictionaryIndex] = new ResourceDictionary { Source = accentUri };
            }
        }

        CurrentMode = mode;
        EffectiveMode = effectiveMode;
        CurrentBackground = background;
        CurrentAccent = accent;
        AppearanceChanged?.Invoke();

        if (persist)
        {
            PersistAllSettings();
        }
    }

    public bool IsSpeculativeDecodingEnabled { get; private set; } = true;
    public int SpeculativeDraftCount { get; private set; } = 24;
    public string SelectedDraftModelPath { get; private set; } = "auto";
    public string SelectedPersonality { get; private set; } = "Default";
    public int UserContextLimit { get; private set; } = 65536;
    public int UserBatchSize { get; private set; } = 0;
    public int UserUBatchSize { get; private set; } = 0;

    public void SaveSpeculativeSettings(bool enabled, int draftCount, string selectedDraftModelPath = "auto")
    {
        IsSpeculativeDecodingEnabled = enabled;
        SpeculativeDraftCount = Math.Clamp(draftCount, 4, 32);
        SelectedDraftModelPath = string.IsNullOrWhiteSpace(selectedDraftModelPath) ? "auto" : selectedDraftModelPath;
        PersistAllSettings();
    }

    public void SavePersonalitySetting(string personality)
    {
        SelectedPersonality = string.IsNullOrWhiteSpace(personality) ? "Default" : personality;
        PersistAllSettings();
    }

    public void SaveContextSizeSetting(int contextSize)
    {
        UserContextLimit = contextSize;
        PersistAllSettings();
    }

    public void SaveBatchProcessingSizeSetting(int batchSize, int uBatchSize)
    {
        UserBatchSize = batchSize;
        UserUBatchSize = uBatchSize;
        PersistAllSettings();
    }

    private void PersistAllSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(new UiSettings 
            { 
                Mode = CurrentMode.ToString(), 
                Background = CurrentBackground.ToString(), 
                Accent = CurrentAccent.ToString(),
                IsSpeculativeDecodingEnabled = IsSpeculativeDecodingEnabled,
                SpeculativeDraftCount = SpeculativeDraftCount,
                SelectedDraftModelPath = SelectedDraftModelPath,
                SelectedPersonality = SelectedPersonality,
                UserContextLimit = UserContextLimit,
                UserBatchSize = UserBatchSize,
                UserUBatchSize = UserUBatchSize
            });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
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
        public string Background { get; set; } = "Ocean";
        public string Accent { get; set; } = "Fluorescent";
        public bool IsSpeculativeDecodingEnabled { get; set; } = true;
        public int SpeculativeDraftCount { get; set; } = 24;
        public string SelectedDraftModelPath { get; set; } = "auto";
        public string SelectedPersonality { get; set; } = "Default";
        public int UserContextLimit { get; set; } = -1;
        public int UserBatchSize { get; set; } = 0;
        public int UserUBatchSize { get; set; } = 0;
    }
}
