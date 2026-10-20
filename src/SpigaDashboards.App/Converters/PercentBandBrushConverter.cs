using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SpigaDashboards.App.Converters;

// Color-codes a "higher is better" percentage. Default (TRS, Disponibilite):
// green >= 85 (world-class OEE threshold), yellow >= 60, red below.
// ConverterParameter=Quality (taux de conformite / Qualite): green >= 98,
// yellow >= 95, red below -- 90 % of bags in tolerance is not "good".
// ConverterParameter=Performance: as default, but above 105 % is a warning
// (the nominal rate is probably set too low), not a success.
// Null (unknown) = neutral grey.
// Reads the current theme's brushes (not fixed colors) so the bands stay
// legible in both dark and light mode.
public class PercentBandBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = parameter as string;
        var key = value is not double percent
            ? "SecondaryTextBrush"
            : mode switch
            {
                "Quality" => percent >= 98 ? "SuccessBrush" : percent >= 95 ? "WarningBrush" : "DangerBrush",
                "Performance" when percent > 105 => "WarningBrush",
                _ => percent >= 85 ? "SuccessBrush" : percent >= 60 ? "WarningBrush" : "DangerBrush",
            };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
