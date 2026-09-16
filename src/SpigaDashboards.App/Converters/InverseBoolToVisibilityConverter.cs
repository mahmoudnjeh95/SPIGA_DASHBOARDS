using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpigaDashboards.App.Converters;

// true -> Collapsed, false -> Visible. Used to show a banner only while something is unhealthy.
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
