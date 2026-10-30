using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App.Converters;

// Colour of a stop cause's family, as in the Planification window
// (Panne red, Process blue, ...); no cause = nothing drawn.
public class CauseFamilyBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CauseFamily family ? PlanningContext.Brush(PlanningContext.FamilyColor(family)) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
