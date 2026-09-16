using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SpigaDashboards.App.Converters;

public class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isOn = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            isOn = !isOn;

        return isOn ? new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71))
                    : new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
