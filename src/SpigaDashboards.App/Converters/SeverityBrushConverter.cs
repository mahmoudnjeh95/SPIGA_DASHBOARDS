using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Converters;

// Maps a KpiSeverity computed by the view-model (e.g. weight deviation as a
// % of the target) to the theme's brushes; Unknown = neutral grey.
// Reads the current theme's brushes (not fixed colors) so it stays legible
// in both dark and light mode.
public class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            KpiSeverity.Good => "SuccessBrush",
            KpiSeverity.Warning => "WarningBrush",
            KpiSeverity.Bad => "DangerBrush",
            _ => "SecondaryTextBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
