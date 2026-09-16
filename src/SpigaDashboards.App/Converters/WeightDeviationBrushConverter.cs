using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SpigaDashboards.App.Converters;

// Color-codes weight deviation from target (grams): closer to zero is better,
// regardless of sign. green within 1g, yellow within 3g, red beyond.
// Reads the current theme's brushes (not fixed colors) so it stays legible
// in both dark and light mode.
public class WeightDeviationBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var deviation = Math.Abs(value is double d ? d : 0);
        var key = deviation switch
        {
            <= 1 => "SuccessBrush",
            <= 3 => "WarningBrush",
            _ => "DangerBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
