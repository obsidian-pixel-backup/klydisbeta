using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Klydis.App.Converters;

public class BoolToColorConverter : IValueConverter
{
    public Brush TrueColor { get; set; } = new SolidColorBrush(Color.FromRgb(39, 194, 76)); // Green
    public Brush FalseColor { get; set; } = new SolidColorBrush(Color.FromRgb(160, 160, 160)); // Gray

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? TrueColor : FalseColor;
        }
        return FalseColor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
