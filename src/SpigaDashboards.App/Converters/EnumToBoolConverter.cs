using System.Globalization;
using System.Windows.Data;

namespace SpigaDashboards.App.Converters;

// Binds a RadioButton's IsChecked to one value of an enum-typed property.
// ConverterParameter is the enum member name to compare against.
public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;
        return value.ToString() == parameter.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null || targetType == null)
            return Binding.DoNothing;
        return Enum.Parse(Nullable.GetUnderlyingType(targetType) ?? targetType, parameter.ToString()!);
    }
}
