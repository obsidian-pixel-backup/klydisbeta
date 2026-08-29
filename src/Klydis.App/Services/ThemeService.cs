using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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
/// The accent color identity applied on top of the active mode. The first five have
/// hand-tuned XAML dictionaries (Themes/Accents/*.xaml); every other accent is derived
/// programmatically from <see cref="ThemeService.AccentColors"/> at apply time.
/// </summary>
public enum AccentTheme
{
    Fluorescent,
    Violet,
    Amber,
    Rose,
    Forest,
    Cherry,
    Cobalt,
    Emerald,
    Gold,
    Indigo,
    Lavender,
    Magenta,
    Mint,
    Orange,
    Peach,
    Ruby,
    Sapphire,
    Sky,
    Teal,
    Turquoise
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
/// A named font style: display label + the WPF weight/style pair it maps to.
/// </summary>
public record FontStyleChoice(string Label, string Weight, string Style);

/// <summary>
/// Composes the active palette from two independent, hot-swappable
/// ResourceDictionaries — a mode (neutrals) and an accent (brand color) — layers
/// user-chosen custom colors and typography on top, and persists everything.
/// App-layer only: reads/writes a small JSON file under the user's LocalAppData.
/// </summary>
public class ThemeService
{
    // App.xaml merges dictionaries in this fixed order: [mode, accent, styles, typography].
    private const int ModeDictionaryIndex = 0;
    private const int AccentDictionaryIndex = 1;
    private const int TypographyDictionaryIndex = 3;

    /// <summary>Curated font families offered in the UI (all standard Windows fonts).</summary>
    public static readonly string[] FontFamilyOptions =
    {
        "Segoe UI Variable", "Segoe UI", "Calibri", "Cambria", "Georgia", "Arial",
        "Verdana", "Tahoma", "Trebuchet MS", "Franklin Gothic Medium", "Bahnschrift",
        "Consolas", "Courier New", "Lucida Console", "Segoe Print", "Segoe Script",
        "Comic Sans MS", "Brush Script MT"
    };

    /// <summary>Named font styles offered in the UI.</summary>
    public static readonly FontStyleChoice[] FontStyleOptions =
    {
        new("Regular", "Normal", "Normal"),
        new("Medium", "Medium", "Normal"),
        new("Semi Bold", "SemiBold", "Normal"),
        new("Bold", "Bold", "Normal"),
        new("Light", "Light", "Normal"),
        new("Italic", "Normal", "Italic"),
        new("Bold Italic", "Bold", "Italic")
    };

    /// <summary>Every accent theme mapped to its bright brand shade (dark-mode primary).</summary>
    public static readonly IReadOnlyDictionary<AccentTheme, string> AccentColors = new Dictionary<AccentTheme, string>
    {
        [AccentTheme.Fluorescent] = "#50E8F4",
        [AccentTheme.Violet] = "#B18CFF",
        [AccentTheme.Amber] = "#FFC24B",
        [AccentTheme.Rose] = "#FF8FB3",
        [AccentTheme.Forest] = "#7BE39B",
        [AccentTheme.Cherry] = "#FF6B81",
        [AccentTheme.Cobalt] = "#4D9FFF",
        [AccentTheme.Emerald] = "#34D399",
        [AccentTheme.Gold] = "#F5C542",
        [AccentTheme.Indigo] = "#8B8CFF",
        [AccentTheme.Lavender] = "#C9A9FF",
        [AccentTheme.Magenta] = "#FF5CC8",
        [AccentTheme.Mint] = "#6EE7B7",
        [AccentTheme.Orange] = "#FFA04D",
        [AccentTheme.Peach] = "#FFB49A",
        [AccentTheme.Ruby] = "#E5484D",
        [AccentTheme.Sapphire] = "#5EB1FF",
        [AccentTheme.Sky] = "#7DD3FC",
        [AccentTheme.Teal] = "#2DD4BF",
        [AccentTheme.Turquoise] = "#48C6EF"
    };

    /// <summary>Every background identity mapped to its base color.</summary>
    public static readonly IReadOnlyDictionary<BackgroundTheme, string> BackgroundColors = new Dictionary<BackgroundTheme, string>
    {
        [BackgroundTheme.Ocean] = "#001619",
        [BackgroundTheme.Obsidian] = "#0D0D0D",
        [BackgroundTheme.Midnight] = "#000B18"
    };

    private readonly string _settingsPath;

    /// <summary>The user's mode selection, which may be <see cref="ThemeMode.System"/>.</summary>
    public ThemeMode CurrentMode { get; private set; } = ThemeMode.Dark;

    /// <summary>The actually-applied mode (Dark or Light) after resolving System.</summary>
    public ThemeMode EffectiveMode { get; private set; } = ThemeMode.Dark;

    public BackgroundTheme CurrentBackground { get; private set; } = BackgroundTheme.Ocean;

    public AccentTheme CurrentAccent { get; private set; } = AccentTheme.Fluorescent;

    public event Action? AppearanceChanged;

    // ---- Custom color overrides (empty string = use the theme's built-in color) ----
    public string CustomAccentColorHex { get; private set; } = string.Empty;
    public string CustomBackgroundColorHex { get; private set; } = string.Empty;
    public string CustomFontColorHex { get; private set; } = string.Empty;

    public bool HasCustomAccent => !string.IsNullOrEmpty(CustomAccentColorHex);
    public bool HasCustomBackground => !string.IsNullOrEmpty(CustomBackgroundColorHex);
    public bool HasCustomFont => !string.IsNullOrEmpty(CustomFontColorHex);

    // ---- Typography ----
    public string FontFamilyName { get; private set; } = "Segoe UI Variable";
    public string FontWeightName { get; private set; } = "Normal";
    public string FontStyleName { get; private set; } = "Normal";

    public static string GetDefaultAccentColorHex(AccentTheme accent)
    {
        return AccentColors.TryGetValue(accent, out var hex) ? hex : "#50E8F4";
    }

    public static string GetDefaultBackgroundColorHex(BackgroundTheme bg, ThemeMode effectiveMode)
    {
        if (effectiveMode == ThemeMode.Light)
        {
            return bg switch
            {
                BackgroundTheme.Obsidian => "#F3F4F6",
                BackgroundTheme.Midnight => "#F0F4FA",
                _ => "#EAF6F7" // Ocean
            };
        }
        return BackgroundColors.TryGetValue(bg, out var hex) ? hex : "#001619";
    }

    public static string GetDefaultFontColorHex(BackgroundTheme bg, ThemeMode effectiveMode)
    {
        if (effectiveMode == ThemeMode.Light)
        {
            return bg switch
            {
                BackgroundTheme.Obsidian => "#111827",
                BackgroundTheme.Midnight => "#001533",
                _ => "#04262B" // Ocean
            };
        }
        return bg switch
        {
            BackgroundTheme.Obsidian => "#E5E5E5",
            BackgroundTheme.Midnight => "#E0F0FF",
            _ => "#C7F8FE" // Ocean
        };
    }

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

                    CustomAccentColorHex = NormalizeHex(settings.CustomAccentColor);
                    CustomBackgroundColorHex = NormalizeHex(settings.CustomBackgroundColor);
                    CustomFontColorHex = NormalizeHex(settings.CustomFontColor);
                    if (!string.IsNullOrWhiteSpace(settings.FontFamilyName)) FontFamilyName = settings.FontFamilyName;
                    if (!string.IsNullOrWhiteSpace(settings.FontWeightName)) FontWeightName = settings.FontWeightName;
                    if (!string.IsNullOrWhiteSpace(settings.FontStyleName)) FontStyleName = settings.FontStyleName;
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

    public void ApplyBackground(BackgroundTheme background)
    {
        // Explicit background selection clears any custom background override
        CustomBackgroundColorHex = string.Empty;
        Apply(CurrentMode, background, CurrentAccent, persist: true);
    }

    public void ApplyAccent(AccentTheme accent)
    {
        // Explicit accent selection clears any custom accent override
        CustomAccentColorHex = string.Empty;
        Apply(CurrentMode, CurrentBackground, accent, persist: true);
    }

    /// <summary>
    /// Resets all appearance settings (mode, background, accent, custom colors, fonts)
    /// to clean factory defaults (Dark mode, Ocean background, Fluorescent accent).
    /// </summary>
    public void ResetAllAppearanceToDefaults()
    {
        CustomAccentColorHex = string.Empty;
        CustomBackgroundColorHex = string.Empty;
        CustomFontColorHex = string.Empty;
        FontFamilyName = "Segoe UI Variable";
        FontWeightName = "Normal";
        FontStyleName = "Normal";
        Apply(ThemeMode.Dark, BackgroundTheme.Ocean, AccentTheme.Fluorescent, persist: true);
    }

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

    public void ApplyCustomAccentColor(string? hex)
    {
        CustomAccentColorHex = NormalizeHex(hex);
        Apply(CurrentMode, CurrentBackground, CurrentAccent, persist: true);
    }

    public void ApplyCustomBackgroundColor(string? hex)
    {
        CustomBackgroundColorHex = NormalizeHex(hex);
        Apply(CurrentMode, CurrentBackground, CurrentAccent, persist: true);
    }

    public void ApplyCustomFontColor(string? hex)
    {
        CustomFontColorHex = NormalizeHex(hex);
        Apply(CurrentMode, CurrentBackground, CurrentAccent, persist: true);
    }

    public void ClearCustomAccentColor() => ApplyCustomAccentColor(null);
    public void ClearCustomBackgroundColor() => ApplyCustomBackgroundColor(null);
    public void ClearCustomFontColor() => ApplyCustomFontColor(null);

    public void ApplyTypography(string fontFamily, string fontWeight, string fontStyle)
    {
        FontFamilyName = string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI Variable" : fontFamily;
        FontWeightName = string.IsNullOrWhiteSpace(fontWeight) ? "Normal" : fontWeight;
        FontStyleName = string.IsNullOrWhiteSpace(fontStyle) ? "Normal" : fontStyle;
        Apply(CurrentMode, CurrentBackground, CurrentAccent, persist: true);
    }

    private void Apply(ThemeMode mode, BackgroundTheme background, AccentTheme accent, bool persist)
    {
        var effectiveMode = mode == ThemeMode.System
            ? (IsSystemInLightMode() ? ThemeMode.Light : ThemeMode.Dark)
            : mode;

        if (Application.Current != null)
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > AccentDictionaryIndex)
            {
                var backgroundUri = new Uri($"pack://application:,,,/Themes/Backgrounds/{background}{effectiveMode}.xaml", UriKind.Absolute);
                merged[ModeDictionaryIndex] = new ResourceDictionary { Source = backgroundUri };

                merged[AccentDictionaryIndex] = LoadAccentDictionary(accent, effectiveMode);

                // Layer custom color overrides on top of the freshly loaded dictionaries.
                ApplyCustomOverrides(merged[ModeDictionaryIndex], effectiveMode);
                ApplyAccentOverrides(merged[AccentDictionaryIndex], effectiveMode);
            }
            if (merged.Count > TypographyDictionaryIndex)
            {
                merged[TypographyDictionaryIndex] = BuildTypographyDictionary();
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

    /// <summary>
    /// Loads the hand-tuned XAML dictionary for accents that have one (the first five);
    /// derives one in code for every other accent so the gallery can offer 20+ themes.
    /// </summary>
    private static ResourceDictionary LoadAccentDictionary(AccentTheme accent, ThemeMode effectiveMode)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Themes/Accents/{accent}{effectiveMode}.xaml", UriKind.Absolute);
            return new ResourceDictionary { Source = uri };
        }
        catch
        {
            return BuildAccentDictionary(accent, effectiveMode);
        }
    }

    private static ResourceDictionary BuildAccentDictionary(AccentTheme accent, ThemeMode effectiveMode)
    {
        var baseColor = HexToColor(AccentColors[accent]);
        bool dark = effectiveMode == ThemeMode.Dark;
        var derived = DeriveAccent(baseColor, dark);

        var dict = new ResourceDictionary();
        dict["AccentPrimaryBrush"] = new SolidColorBrush(derived.Primary);
        dict["AccentSecondaryBrush"] = new SolidColorBrush(derived.Secondary);
        dict["AccentSoftBrush"] = new SolidColorBrush(derived.Soft);
        dict["BorderBrushStrong"] = new SolidColorBrush(derived.Strong);
        dict["UserBubbleBackgroundBrush"] = new SolidColorBrush(derived.Bubble);
        dict["UserBubbleTextBrush"] = new SolidColorBrush(derived.BubbleText);
        AddBubbleChipBrushes(dict, derived.Bubble, derived.BubbleText);
        return dict;
    }

    private static (Color Primary, Color Secondary, Color Soft, Color Strong, Color Bubble, Color BubbleText) DeriveAccent(Color accent, bool dark)
    {
        if (dark)
        {
            var primary = accent;
            var secondary = Lighter(accent, 0.28);
            var soft = Color.FromArgb(0x2E, accent.R, accent.G, accent.B);
            var bubbleText = Luminance(accent) > 0.55 ? Color.FromRgb(0x0C, 0x14, 0x16) : Color.FromRgb(0xEA, 0xF6, 0xF8);
            return (primary, secondary, soft, primary, accent, bubbleText);
        }

        var darkPrimary = Darker(accent, 0.30);
        var darkSecondary = Darker(accent, 0.45);
        var softLight = Mix(accent, Colors.White, 0.82);
        var lightBubbleText = Color.FromRgb(0x2B, 0x0A, 0x16);
        return (darkPrimary, darkSecondary, softLight, darkPrimary, accent, lightBubbleText);
    }

    /// <summary>
    /// Applies custom background + font color overrides to the mode dictionary by
    /// recomputing the derived surface/border/text shades around the chosen base color.
    /// </summary>
    private void ApplyCustomOverrides(ResourceDictionary dict, ThemeMode effectiveMode)
    {
        // No custom colors: leave the hand-tuned XAML palette untouched.
        bool hasCustomBg = !string.IsNullOrEmpty(CustomBackgroundColorHex);
        bool hasCustomFont = !string.IsNullOrEmpty(CustomFontColorHex);
        if (!hasCustomBg && !hasCustomFont) return;

        bool dark = effectiveMode == ThemeMode.Dark;

        Color bg = hasCustomBg
            ? HexToColor(CustomBackgroundColorHex)
            : HexToColor(BackgroundColors[CurrentBackground]);

        Color text = hasCustomFont
            ? HexToColor(CustomFontColorHex)
            : (Luminance(bg) < 0.45 ? Color.FromRgb(0xC7, 0xF8, 0xFE) : Color.FromRgb(0x10, 0x16, 0x18));

        double d = dark ? 1 : -1; // dark mode lightens away from bg; light mode darkens
        Color surface = Shift(bg, 0.05 * d);
        Color elevated = Shift(bg, 0.10 * d);
        Color hover = Shift(bg, 0.14 * d);
        Color sidebar = Shift(bg, 0.03 * d);
        Color input = Shift(bg, 0.05 * d);
        Color button = Shift(bg, 0.12 * d);
        Color border = Shift(bg, 0.20 * d);
        Color borderMid = Shift(bg, 0.32 * d);
        Color inverse = Luminance(text) > 0.5 ? Color.FromRgb(0x0C, 0x14, 0x16) : Color.FromRgb(0xEA, 0xF6, 0xF8);
        Color secondary = Mix(text, bg, 0.35);
        Color muted = Mix(text, bg, 0.62);

        var replacements = new Dictionary<string, Color>
        {
            ["WindowBackgroundBrush"] = bg,
            ["SidebarBackgroundBrush"] = sidebar,
            ["PanelBackgroundBrush"] = bg,
            ["TitleBarBackgroundBrush"] = bg,
            ["StatusBarBackgroundBrush"] = bg,
            ["BackgroundTertiary"] = surface,
            ["SurfaceBrush"] = surface,
            ["SurfaceElevatedBrush"] = elevated,
            ["SurfaceHoverBrush"] = hover,
            ["InputBackgroundBrush"] = input,
            ["ButtonBackgroundBrush"] = button,
            ["BorderBrush"] = border,
            ["BorderMidBrush"] = borderMid,
            ["TextPrimaryBrush"] = text,
            ["TextSecondaryBrush"] = secondary,
            ["TextMutedBrush"] = muted,
            ["TextInverse"] = inverse,
            ["AssistantBubbleBackgroundBrush"] = surface,
            ["AssistantBubbleBorderBrush"] = border,
            ["ThinkingBubbleBackgroundBrush"] = surface,
            ["ThinkingBubbleBorderBrush"] = border,
            ["ToolBubbleBackgroundBrush"] = surface
        };

        foreach (var (key, color) in replacements)
        {
            dict[key] = new SolidColorBrush(color);
        }
    }

    private void ApplyAccentOverrides(ResourceDictionary dict, ThemeMode effectiveMode)
    {
        if (string.IsNullOrEmpty(CustomAccentColorHex)) return;
        bool dark = effectiveMode == ThemeMode.Dark;
        var derived = DeriveAccent(HexToColor(CustomAccentColorHex), dark);

        dict["AccentPrimaryBrush"] = new SolidColorBrush(derived.Primary);
        dict["AccentSecondaryBrush"] = new SolidColorBrush(derived.Secondary);
        dict["AccentSoftBrush"] = new SolidColorBrush(derived.Soft);
        dict["BorderBrushStrong"] = new SolidColorBrush(derived.Strong);
        dict["UserBubbleBackgroundBrush"] = new SolidColorBrush(derived.Bubble);
        dict["UserBubbleTextBrush"] = new SolidColorBrush(derived.BubbleText);
        AddBubbleChipBrushes(dict, derived.Bubble, derived.BubbleText);
    }

    private ResourceDictionary BuildTypographyDictionary()
    {
        var dict = new ResourceDictionary();
        dict["AppFontFamily"] = new FontFamily(FontFamilyName);
        dict["AppFontWeight"] = ParseFontWeight(FontWeightName);
        dict["AppFontStyle"] = ParseFontStyle(FontStyleName);
        return dict;
    }

    private static FontWeight ParseFontWeight(string name) => name switch
    {
        "Thin" => FontWeights.Thin,
        "ExtraLight" => FontWeights.ExtraLight,
        "Light" => FontWeights.Light,
        "Medium" => FontWeights.Medium,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        "ExtraBold" => FontWeights.ExtraBold,
        "Black" => FontWeights.Black,
        _ => FontWeights.Normal
    };

    private static FontStyle ParseFontStyle(string name) => name switch
    {
        "Italic" => FontStyles.Italic,
        "Oblique" => FontStyles.Oblique,
        _ => FontStyles.Normal
    };

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
                UserUBatchSize = UserUBatchSize,
                CustomAccentColor = CustomAccentColorHex,
                CustomBackgroundColor = CustomBackgroundColorHex,
                CustomFontColor = CustomFontColorHex,
                FontFamilyName = FontFamilyName,
                FontWeightName = FontWeightName,
                FontStyleName = FontStyleName
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

    // ---- Color math helpers ----

    private static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
        hex = hex.Trim();
        if (!hex.StartsWith("#")) hex = "#" + hex;
        try
        {
            return HexToColor(hex).ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static Color HexToColor(string hex)
    {
        if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is Color c)
        {
            return c;
        }
        return Colors.Magenta;
    }

    private static double Luminance(Color c)
    {
        // Perceived luminance (Rec. 601 coefficients), 0..1.
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
    }

    /// <summary>
    /// WCAG 2.1 relative luminance (gamma-expanded), which is what a contrast ratio
    /// needs. Deliberately separate from <see cref="Luminance"/>: that one is Rec. 601
    /// perceived brightness and its callers are tuned to its thresholds.
    /// </summary>
    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Source-over composite of <paramref name="fg"/> (using its alpha) onto an opaque background.</summary>
    private static Color CompositeOver(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(a * fg.R + (1 - a) * bg.R),
            (byte)Math.Round(a * fg.G + (1 - a) * bg.G),
            (byte)Math.Round(a * fg.B + (1 - a) * bg.B));
    }

    /// <summary>
    /// Adds the attachment-chip brushes for a user bubble. The chip sits ON the bubble, and
    /// every bubble is a bright pastel in BOTH modes, so the chip is tinted with the bubble's
    /// own ink rather than white — a translucent-white wash never cleared 1.3:1 against it.
    /// The border alpha is solved per accent so it reaches the 3:1 non-text contrast floor,
    /// which keeps derived and custom accents as legible as the five hand-tuned dictionaries.
    /// </summary>
    private static void AddBubbleChipBrushes(ResourceDictionary dict, Color bubble, Color bubbleText)
    {
        dict["UserBubbleChipBackgroundBrush"] = new SolidColorBrush(
            Color.FromArgb(0x1F, bubbleText.R, bubbleText.G, bubbleText.B));

        byte alpha = 0xFF;
        for (int a = 0; a <= 255; a++)
        {
            var candidate = Color.FromArgb((byte)a, bubbleText.R, bubbleText.G, bubbleText.B);
            if (ContrastRatio(CompositeOver(candidate, bubble), bubble) >= 3.05)
            {
                alpha = (byte)a;
                break;
            }
        }
        dict["UserBubbleChipBorderBrush"] = new SolidColorBrush(
            Color.FromArgb(alpha, bubbleText.R, bubbleText.G, bubbleText.B));
    }

    private static Color Shift(Color c, double amount)
    {
        // amount > 0 lightens toward white, < 0 darkens toward black.
        if (amount >= 0) return Lighter(c, amount);
        return Darker(c, -amount);
    }

    private static Color Lighter(Color c, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return Mix(c, Colors.White, t);
    }

    private static Color Darker(Color c, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return Mix(c, Colors.Black, t);
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
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
        public string CustomAccentColor { get; set; } = string.Empty;
        public string CustomBackgroundColor { get; set; } = string.Empty;
        public string CustomFontColor { get; set; } = string.Empty;
        public string FontFamilyName { get; set; } = "Segoe UI Variable";
        public string FontWeightName { get; set; } = "Normal";
        public string FontStyleName { get; set; } = "Normal";
    }
}
