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
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
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
