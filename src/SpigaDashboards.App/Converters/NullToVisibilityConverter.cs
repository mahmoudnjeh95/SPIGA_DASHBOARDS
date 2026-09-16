using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpigaDashboards.App.Converters;

// null -> Collapsed, non-null -> Visible. Used to hide the second machine
// panel on sector slides that have an odd machine out (e.g. PC2's third machine).
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
