using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SpigaDashboards.App.Converters;

// Color-codes a "higher is better" percentage (TRS, taux de conformite):
// green >= 85 (world-class OEE threshold), yellow >= 60, red below.
// Reads the current theme's brushes (not fixed colors) so the bands stay
// legible in both dark and light mode.
public class PercentBandBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        var key = percent switch
        {
            >= 85 => "SuccessBrush",
            >= 60 => "WarningBrush",
            _ => "DangerBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
