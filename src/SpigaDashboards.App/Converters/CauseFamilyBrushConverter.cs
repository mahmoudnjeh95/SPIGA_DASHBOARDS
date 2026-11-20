using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Converters;

// Colour of a stop cause's family, as on the planning pages (Panne red,
// Process blue, ...; spiga.css .cause-dot); no cause = nothing drawn.
public class CauseFamilyBrushConverter : IValueConverter
{
    private static readonly Dictionary<CauseFamily, Brush> Brushes_ = new()
    {
        [CauseFamily.Panne] = Frozen(0xD0, 0x3B, 0x3B),
        [CauseFamily.Process] = Frozen(0x39, 0x87, 0xE5),
        [CauseFamily.Organisation] = Frozen(0x9A, 0xA4, 0xB2),
        [CauseFamily.Planifie] = Frozen(0xFA, 0xB2, 0x19),
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is CauseFamily family ? Brushes_[family] : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
