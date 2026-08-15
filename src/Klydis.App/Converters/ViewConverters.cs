using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

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
