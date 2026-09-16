using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Converters;

public class SqlLoginVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SqlAuthMode.SqlLogin ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
