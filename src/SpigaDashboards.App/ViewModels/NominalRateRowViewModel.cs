using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One editable row of the "Cadence et sachet vide" table in Parametres
// (machine + weight). Numbers kept as text so both "12,5" and "12.5" are
// accepted. MachineId "" = every machine.
public partial class NominalRateRowViewModel : ObservableObject
{
    [ObservableProperty] private string machineId = "";
    [ObservableProperty] private string weightText = "";
    [ObservableProperty] private string rateText = "";
    [ObservableProperty] private string tareText = "";

    public static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    public static bool TryParse(string text, out double value)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (normalized.Length == 0)
        {
            value = 0;
            return true;
        }
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0;
    }
}

// Machine choice of a cadence row ("" = Toutes).
public sealed record MachineChoice(string Id, string Label);
