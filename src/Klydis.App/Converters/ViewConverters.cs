using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Klydis.App.Converters;

public class RoleToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? role = value is ViewModels.ChatMessageViewModel msg ? msg.Role : value?.ToString();
        var targetRole = parameter?.ToString();

        return string.Equals(role, targetRole, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// Converts a remaining-percent value (0-100) into the StrokeDashOffset needed to render a
/// progress ring on an Ellipse whose StrokeDashArray is set to its full circumference.
/// Pass the circumference as the ConverterParameter (must match the dash length in XAML).
/// </summary>
public class PercentToDashOffsetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : double.TryParse(value?.ToString(), out var p) ? p : 100.0;
        double circumference = double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 100.0;
        double fraction = Math.Clamp(percent / 100.0, 0.0, 1.0);
        return circumference * (1.0 - fraction);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }
}

/// <summary>
/// Visible when the bound string is empty/null, collapsed when it has content — the inverse
/// of <see cref="StringToVisibilityConverter"/>. Used for text-box watermarks/placeholders.
/// </summary>
public class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// Collapsed when the bound numeric value is 0 (or null), Visible otherwise. Used to hide
/// empty sections (e.g. no drives detected) without a code-behind bool.
/// </summary>
public class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double n = value is double d ? d : double.TryParse(value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
        // ConverterParameter=Inverse flips the test so an empty collection becomes Visible. The
        // parameter used to be ignored, which showed the "no models found" empty state exactly
        // when the list DID have results.
        bool inverse = string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase);
        return (n > 0) != inverse ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// Maps a severity string ("Normal" / "Warning" / "Critical") to the matching themed brush
/// so monitor values can be colored amber/red at a glance.
/// </summary>
public class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string severity = value?.ToString() ?? "Normal";
        var app = System.Windows.Application.Current;
        return severity switch
        {
            "Critical" => app?.TryFindResource("ErrorBrush") as Brush ?? Brushes.Red,
            "Warning" => app?.TryFindResource("WarningBrush") as Brush ?? Brushes.Gold,
            _ => app?.TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class CountToFormattedStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null) return "0";
        if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double n))
        {
            if (n >= 1_000_000) return $"{(n / 1_000_000.0):F1}M";
            if (n >= 1_000) return $"{(n / 1_000.0):F1}K";
            return n.ToString("N0", CultureInfo.InvariantCulture);
        }
        return value.ToString() ?? "0";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? valStr = value?.ToString();
        string? paramStr = parameter?.ToString();
        bool match = string.Equals(valStr, paramStr, StringComparison.OrdinalIgnoreCase);
        return match ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // ConverterParameter=Inverse flips the test so a null value maps to Visible. The parameter
        // used to be ignored, which left the model-hub empty-state placeholder visible exactly when
        // an item WAS selected — it then painted on top of the detail pane.
        bool hasValue = value != null;
        bool inverse = string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase);
        return hasValue != inverse ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter _brushConverter = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str && !string.IsNullOrWhiteSpace(str))
        {
            try
            {
                var brush = _brushConverter.ConvertFromString(str) as Brush;
                if (brush != null) return brush;
            }
            catch { }
        }
        return Brushes.Transparent;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}


