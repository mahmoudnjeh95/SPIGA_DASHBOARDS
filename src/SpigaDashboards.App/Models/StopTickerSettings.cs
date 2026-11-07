namespace SpigaDashboards.App.Models;

// Scrolling bar under the machine photo while the line is stopped (StopTicker,
// Parametres > Couleurs, shared by every PC). Colours are "#RRGGBB" or a WPF
// colour name.
public class StopTickerSettings
{
    // The values in force (AppSettings.StopTicker), for OngoingStop and the
    // bar, which have no AppSettings at hand.
    public static StopTickerSettings Current { get; set; } = new();

    public const int MinSpeed = 20;
    public const int MaxSpeed = 600;

    // Scrolling speed, in pixels per second of the 1600 x 900 slide (so the
    // same on the dashboard and on the TV, whatever the screen size).
    public int SpeedPixelsPerSecond { get; set; } = 120;

    // Panne, or a stop nobody gave a cause yet.
    public string PanneColor { get; set; } = "#E74C3C";
    // Stop with another cause (manque produit, absence opérateur...).
    public string OtherColor { get; set; } = "#E67E22";
    // Arrêt planifié / maintenance (only on the period slides: the poste
    // slide gives them the whole view).
    public string PlannedColor { get; set; } = "#7F8C8D";
    public string TextColor { get; set; } = "#FFFFFF";

    public string ColorFor(OngoingStopKind kind) => kind switch
    {
        OngoingStopKind.Panne or OngoingStopKind.Unqualified => PanneColor,
        OngoingStopKind.Planned or OngoingStopKind.Maintenance => PlannedColor,
        _ => OtherColor,
    };
}
