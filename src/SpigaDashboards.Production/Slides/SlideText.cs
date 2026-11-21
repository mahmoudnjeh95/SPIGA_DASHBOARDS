using System.Globalization;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Slides;

// Texts of the dashboard slides, shared by the kiosk (WPF) and the web deck.
public static class SlideText
{
    // Banner of a run-state photo while the line is in a slot of the
    // Planification board (grey for a planned stop, yellow for a maintenance).
    public static string? Planned(SlotKind? slot) => slot switch
    {
        SlotKind.PlannedStop => "ARRÊT PLANIFIÉ",
        SlotKind.Maintenance => "MAINTENANCE",
        _ => null,
    };

    // Same French number format as the tiles (WPF bindings use fr-FR, see App).
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string Production(ProductionInfo? production, IReadOnlyCollection<ProductionBucket> lots)
    {
        if (production == null)
            return "Aucun relevé reçu de la machine depuis 6 h";

        var state = production.IsTerminated ? "Terminée" : "En cours";
        // Lots of this production in the loaded period first (real lot
        // averages); otherwise Stiavelli's running average, if it looks sane.
        var avgWeight = KpiCalculator.ProductionAverageWeight(lots, production.ProductionId, production.TargetNetWeight) ?? production.AvgWeight;
        var weight = avgWeight is { } avg ? string.Create(French, $"  ·  moyen {avg:0.0} g") : "";
        var quality = production.TotalBags > 0 ? string.Create(French, $"{production.QualityPercent:0.0} %") : "— %";
        var since = production.StartTime.Date == DateTime.Now.Date
            ? $"depuis {production.StartTime:HH:mm}"
            : $"depuis le {production.StartTime:dd/MM HH:mm}";
        return string.Create(French, $"{state} : {production.ProductName} {production.TargetNetWeight:0} g  ·  {since}  ·  ") +
               string.Create(French, $"{production.TotalBags:N0} sacs  ·  {quality} conformes{weight}").Replace(' ', ' ');
    }
}
