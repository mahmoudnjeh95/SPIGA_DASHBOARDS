using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One editable row of the "Pauses planifiees" table in Parametres. Start
// kept as text so "10:00", "10h00" and "10h" are all accepted.
public partial class PlannedBreakRowViewModel : ObservableObject
{
    [ObservableProperty] private string startText = "";
    [ObservableProperty] private string durationText = "";

    public static string Format(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    public static bool TryParseStart(string text, out TimeSpan start)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace('h', ':');
        if (normalized.EndsWith(':'))
            normalized += "00";
        return TimeSpan.TryParseExact(normalized, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out start)
               && start < TimeSpan.FromDays(1);
    }
}
