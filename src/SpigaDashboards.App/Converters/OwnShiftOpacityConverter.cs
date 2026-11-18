using System.Globalization;
using System.Windows.Data;

namespace SpigaDashboards.App.Converters;

// A poste of another team (false) is greyed out, still clickable to say why.
public class OwnShiftOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false ? 0.38 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
