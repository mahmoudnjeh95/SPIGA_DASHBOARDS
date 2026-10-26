using System.Globalization;
using System.Windows.Data;

namespace SpigaDashboards.App.Converters;

// Binds a RadioButton's IsChecked to one value of a string property (filter
// bar segments). ConverterParameter is the value that button stands for.
public class ValueToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter?.ToString() : Binding.DoNothing;
}
