using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SpigaDashboards.App.Converters;

// null -> Visible, non-null -> Collapsed. Used to show a fallback message
// only while a bound object (e.g. a rendered PDF page) isn't available.
public class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
