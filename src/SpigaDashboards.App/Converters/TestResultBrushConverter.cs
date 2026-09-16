using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SpigaDashboards.App.Converters;

// bool? -> Brush for the connection-test message: null (not tested yet) is muted,
// true is green, false is red.
public class TestResultBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
        false => new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
        _ => new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
