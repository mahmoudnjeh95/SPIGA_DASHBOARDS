using System.Globalization;
using SpigaDashboards.App.Models;

namespace Spiga.UI.Services;

// French number formats of the screens ("78,4 %", "1 240 kg").
public static class Fmt
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public const string Missing = "N/D";

    public static string Pct(double? value, int decimals = 1)
        => value is { } v ? v.ToString("N" + decimals, French) + " %" : Missing;

    public static string SignedPct(double? value, int decimals = 2)
        => value is { } v ? (v > 0 ? "+" : "") + v.ToString("N" + decimals, French) + " %" : Missing;

    public static string Grams(double? value)
        => value is { } v ? (v > 0 ? "+" : "") + v.ToString("N1", French) + " g" : Missing;

    public static string Num(double? value, int decimals = 0)
        => value is { } v ? v.ToString("N" + decimals, French) : Missing;

    public static string Kg(double kg) => kg.ToString(kg >= 100 ? "N0" : "0.#", French) + " kg";

    public static string Mass(double kg) => kg >= 1000 ? (kg / 1000).ToString("N1", French) + " t" : Kg(kg);

    public static string Css(KpiSeverity severity) => severity switch
    {
        KpiSeverity.Good => "ok",
        KpiSeverity.Warning => "warn",
        KpiSeverity.Bad => "bad",
        _ => "nd",
    };

    public static string Date(DateTime? value) => value?.ToString("dd/MM/yyyy HH:mm", French) ?? "—";

    public static string Ago(DateTime? value, DateTime now)
    {
        if (value is not { } v)
            return "jamais";
        var d = now - v;
        if (d < TimeSpan.FromMinutes(1))
            return "à l'instant";
        if (d < TimeSpan.FromHours(1))
            return $"il y a {(int)d.TotalMinutes} min";
        if (v.Date == now.Date)
            return "aujourd'hui " + v.ToString("HH:mm", French);
        if (v.Date == now.Date.AddDays(-1))
            return "hier " + v.ToString("HH:mm", French);
        return v.ToString("dd/MM/yyyy HH:mm", French);
    }
}
