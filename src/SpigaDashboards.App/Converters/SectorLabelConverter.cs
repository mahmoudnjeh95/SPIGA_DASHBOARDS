using System.Globalization;
using System.Windows.Data;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Converters;

// Stored sector value ("Pate courte") -> display text ("Pâte courte").
public class SectorLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string sector ? Machine.SectorLabel(sector) : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
