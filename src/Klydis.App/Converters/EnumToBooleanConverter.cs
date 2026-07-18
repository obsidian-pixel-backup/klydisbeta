using System;
using System.Globalization;
using System.Windows.Data;

namespace Klydis.App.Converters;

public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        string checkValue = value.ToString() ?? string.Empty;
        string targetValue = parameter.ToString() ?? string.Empty;
        return checkValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked)
        {
            return Enum.Parse(targetType, parameter.ToString() ?? string.Empty);
        }
        return Binding.DoNothing;
    }
}
