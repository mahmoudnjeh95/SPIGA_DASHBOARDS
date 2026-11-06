using System.Globalization;

namespace SpigaDashboards.App.Models;

public enum OngoingStopKind
{
    // Nobody gave it a cause yet: "cause à définir", pop-up on the TV.
    Unqualified,
    // Qualified with a cause of the Panne family.
    Panne,
    // Qualified "Planifié", or inside an arrêt planifié slot of the calendar.
    Planned,
    // Inside a maintenance slot of the calendar.
    Maintenance,
    // Qualified with another cause (manque produit, absence opérateur...).
    Other,
}

// A line standing still right now (OngoingStopService), from the start of the
// stop, however long ago, with what the Planification window says about it.
public sealed class OngoingStop
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public DateTime Start { get; init; }
    // StatsViewer has no snapshot of the line right now (switched off...).
    public bool IsNoData { get; init; }
    public StopQualification? Qualification { get; init; }
    public MaintenanceEvent? Slot { get; init; }

    public OngoingStopKind Kind => Slot != null
        ? Slot.IsStop ? OngoingStopKind.Planned : OngoingStopKind.Maintenance
        : Qualification?.Cause?.Family switch
        {
            null => OngoingStopKind.Unqualified,
            CauseFamily.Panne => OngoingStopKind.Panne,
            CauseFamily.Planifie => OngoingStopKind.Planned,
            _ => OngoingStopKind.Other,
        };

    public bool IsUnqualified => Kind == OngoingStopKind.Unqualified;

    // Panne / planned stop / maintenance: the machine slide gives the screen
    // to this stop until the line runs again.
    public bool IsDeclared => Kind is OngoingStopKind.Panne or OngoingStopKind.Planned or OngoingStopKind.Maintenance;

    public string Title => Kind switch
    {
        OngoingStopKind.Panne => "EN PANNE",
        OngoingStopKind.Planned => "ARRÊT PLANIFIÉ",
        OngoingStopKind.Maintenance => "MAINTENANCE",
        _ => "EN ARRÊT",
    };

    public string SinceText => Start.Date == DateTime.Today
        ? $"depuis {Start:HH:mm}"
        : string.Create(French, $"depuis le {Start:dd/MM} à {Start:HH:mm}");

    public string DurationText => FormatDuration(DateTime.Now - Start);

    // "Panne mécanique · Mâchoires de soudure · Karim", the calendar slot's
    // reason, or "Cause à définir".
    public string CauseText
    {
        get
        {
            if (Slot != null)
                return string.IsNullOrWhiteSpace(Slot.Task) ? (Slot.IsStop ? "Arrêt planifié" : "Maintenance") : Slot.Task.Trim();
            if (Qualification == null)
                return IsNoData ? "Cause à définir · aucune donnée de StatsViewer" : "Cause à définir";
            return string.Join(" · ", new[] { StopCauses.Label(Qualification.CauseCode), Qualification.Component, Qualification.Technician, Qualification.Comment }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    // Planned end of the calendar slot, else the one given when qualifying
    // the stop (Pannes); flagged once passed with the line still stopped.
    public DateTime? ExpectedEnd => Slot?.PlannedEnd ?? Qualification?.ExpectedEnd;
    public bool IsOverdue => ExpectedEnd is { } end && end < DateTime.Now;

    public string ExpectedEndText => ExpectedEnd is { } end
        ? string.Create(French, $"Retour prévu le {end:dd/MM} à {end:HH:mm}") + (IsOverdue ? " — dépassé" : "")
        : "Retour prévu : non renseigné";

    // One line for the slide header and the pop-up.
    public string BannerText => $"{Title} {SinceText} ({DurationText})  ·  {CauseText}";

    // "12 min", "5 h 07", "3 j 04 h"
    public static string FormatDuration(TimeSpan span)
    {
        var minutes = (int)Math.Max(0, Math.Round(span.TotalMinutes));
        if (minutes < 60)
            return $"{minutes} min";
        if (minutes < 24 * 60)
            return $"{minutes / 60} h {minutes % 60:00}";
        return $"{minutes / (24 * 60)} j {minutes % (24 * 60) / 60:00} h";
    }
}
