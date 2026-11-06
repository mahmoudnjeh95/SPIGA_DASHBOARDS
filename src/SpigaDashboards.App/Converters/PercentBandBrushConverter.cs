using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Converters;

// Color-codes a "higher is better" percentage with the limits of Parametres
// > Couleurs (ColorThresholds). Default: Disponibilite.
// ConverterParameter=Quality: taux de conformite / Qualite.
// ConverterParameter=Performance: also orange above its upper limit (the
// nominal rate is probably set too low).
// Null (unknown) = neutral grey.
// Reads the current theme's brushes (not fixed colors) so the bands stay
// legible in both dark and light mode.
public class PercentBandBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value as double?;
        var limits = ColorThresholds.Current;
        var severity = (parameter as string) switch
        {
            "Quality" => limits.Quality(percent),
            "Performance" => limits.Performance(percent),
            _ => limits.Availability(percent),
        };
        return SeverityBrushConverter.BrushFor(severity);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
