using System.Globalization;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels;

// Top-bar period selector. Day / PreviousShift use the poste layout
// (MachineDaySlideViewModel), the others the period layout.
public enum DashboardPeriod
{
    Day,
    PreviousShift,
    Yesterday,
    Week,
    Month,
    ThreeMonths,
    Custom,
}

// Period of the synthesis slides (selector on the slide itself).
public enum SynthesisPeriod
{
    Month,
    ThreeMonths,
    Year,
}

// Dashboard filters (Filtres panel), also used by Historique and the exports.
// Sector / machines restrict which machines are shown; poste, team and weight
// restrict which buckets the figures are computed from. null / empty = all.
public sealed record DashboardFilter
{
    public string? Sector { get; init; }
    public IReadOnlyList<string> MachineIds { get; init; } = Array.Empty<string>();
    // Display names of MachineIds, same order: shown in place of the ids.
    public IReadOnlyList<string> MachineNames { get; init; } = Array.Empty<string>();
    public int? ShiftId { get; init; }
    public string? Team { get; init; }
    public double? TargetWeight { get; init; }

    public static DashboardFilter None { get; } = new();

    public bool FiltersByPosteOrTeam => ShiftId.HasValue || Team != null;
    public bool IsEmpty => Sector == null && MachineIds.Count == 0 && !FiltersByPosteOrTeam && !TargetWeight.HasValue;

    public bool IncludesMachine(Machine machine)
        => (Sector == null || machine.Sector == Sector) && (MachineIds.Count == 0 || MachineIds.Contains(machine.MachineId));

    // Same 5 % tolerance as the cadence table (1005 g runs count as 1 kg).
    public bool MatchesWeight(double target)
        => !TargetWeight.HasValue || (target > 0 && Math.Abs(target - TargetWeight.Value) <= 0.05 * TargetWeight.Value);

    public List<ProductionBucket> Apply(IEnumerable<ProductionBucket> lots, TeamScheduleService teams)
        => lots.Where(l => (!ShiftId.HasValue || l.ShiftId == ShiftId.Value)
                           && (Team == null || teams.TeamOf(l) == Team)
                           && MatchesWeight(l.TargetNetWeight))
            .ToList();

    // "Controle CC01, Controle CC02" ("" = every machine).
    public string MachinesLabel => string.Join(", ", MachineNames.Count == MachineIds.Count ? MachineNames : MachineIds);

    // "Couscous · Controle CC01, Controle CC02 · Poste Matin · Equipe A · 500 g", "" when no filter.
    public string Describe()
    {
        var parts = new List<string>();
        if (Sector != null)
            parts.Add(Sector);
        if (MachineIds.Count > 0)
            parts.Add(MachinesLabel);
        if (ShiftId is { } shift)
            parts.Add("Poste " + KpiCalculator.ShiftName(shift));
        if (Team != null)
            parts.Add(Team);
        if (TargetWeight is { } weight)
            parts.Add(weight.ToString("0.##", CultureInfo.InvariantCulture) + " g");
        return string.Join(" · ", parts);
    }

    public string CacheKey => $"{Sector}|{string.Join(",", MachineIds)}|{ShiftId}|{Team}|{TargetWeight}";
}

public enum TimeGrain
{
    Hour,
    Day,
    Week,
    Month,
}

// Date ranges of the periods, and the time steps their charts are drawn with.
public static class PeriodRanges
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    // [From, To) of a period-layout view. PreviousShift / Day are only used
    // here as the fallback when no poste matches the filters (whole day).
    public static (DateTime From, DateTime To) Get(DashboardPeriod period, DateTime now, DateTime? customFrom = null, DateTime? customTo = null)
    {
        var today = now.Date;
        return period switch
        {
            DashboardPeriod.Yesterday => (today.AddDays(-1), today),
            DashboardPeriod.Week => (StartOfWeek(today), today.AddDays(1)),
            DashboardPeriod.Month => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
            DashboardPeriod.ThreeMonths => (new DateTime(today.Year, today.Month, 1).AddMonths(-2), today.AddDays(1)),
            DashboardPeriod.Custom => CustomRange(today, customFrom, customTo),
            _ => (today, today.AddDays(1)),
        };
    }

    private static (DateTime, DateTime) CustomRange(DateTime today, DateTime? from, DateTime? to)
    {
        var start = (from ?? today.AddDays(-7)).Date;
        var end = (to ?? today).Date;
        if (end < start)
            (start, end) = (end, start);
        return (start, end.AddDays(1));
    }

    public static string Label(DashboardPeriod period, DateTime from, DateTime to) => period switch
    {
        DashboardPeriod.Day => "Aujourd'hui",
        DashboardPeriod.PreviousShift => "Poste precedent",
        DashboardPeriod.Yesterday => $"Hier ({from:dd/MM})",
        DashboardPeriod.Week => "Cette semaine",
        DashboardPeriod.Month => "Ce mois",
        DashboardPeriod.ThreeMonths => "3 derniers mois (" + from.ToString("MMM", French) + " - " + to.AddDays(-1).ToString("MMM yyyy", French) + ")",
        _ => $"Du {from:dd/MM/yyyy} au {to.AddDays(-1):dd/MM/yyyy}",
    };

    public static (DateTime From, DateTime To, string Label) Synthesis(SynthesisPeriod period, DateTime now)
    {
        var today = now.Date;
        var month = new DateTime(today.Year, today.Month, 1);
        return period switch
        {
            SynthesisPeriod.Month => (month, today.AddDays(1), month.ToString("MMMM yyyy", French)),
            SynthesisPeriod.ThreeMonths => (month.AddMonths(-2), today.AddDays(1),
                "3 derniers mois (" + month.AddMonths(-2).ToString("MMM", French) + " - " + month.ToString("MMM yyyy", French) + ")"),
            _ => (new DateTime(today.Year, 1, 1), today.AddDays(1), $"Annee {today.Year}"),
        };
    }

    // Chart step for a range: hours for a day, days up to ~6 weeks, weeks up
    // to ~7 months, months beyond.
    public static TimeGrain GrainFor(DateTime from, DateTime to) => (to - from).TotalDays switch
    {
        <= 1 => TimeGrain.Hour,
        <= 45 => TimeGrain.Day,
        <= 200 => TimeGrain.Week,
        _ => TimeGrain.Month,
    };

    public static string GrainName(TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => "heure",
        TimeGrain.Day => "jour",
        TimeGrain.Week => "semaine",
        _ => "mois",
    };

    // Buckets grouped per chart step, every step of [from, to) up to now
    // listed (empty ones too, so the axis has no holes). Days are shift days:
    // a Nuit poste stays on the day it started.
    public static List<(DateTime Key, string Label, List<ProductionBucket> Lots)> Group(
        IEnumerable<ProductionBucket> lots, DateTime from, DateTime to, TimeGrain grain, DateTime now)
    {
        var byKey = lots.GroupBy(l => KeyOf(l, grain)).ToDictionary(g => g.Key, g => g.ToList());
        var end = to < now ? to : now;
        var steps = new List<DateTime>();
        for (var t = KeyOf(from, grain); t < end; t = Next(t, grain))
            steps.Add(t);
        // Buckets outside the listed steps (e.g. the tail of a Nuit) are dropped.
        return steps.Select(t => (t, LabelOf(t, grain), byKey.GetValueOrDefault(t) ?? new List<ProductionBucket>())).ToList();
    }

    private static DateTime KeyOf(ProductionBucket lot, TimeGrain grain)
        => grain == TimeGrain.Hour ? lot.StartTime : KeyOf(TeamScheduleService.GetShiftDate(lot.StartTime, lot.ShiftId), grain);

    private static DateTime KeyOf(DateTime t, TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => t.Date.AddHours(t.Hour),
        TimeGrain.Day => t.Date,
        TimeGrain.Week => StartOfWeek(t.Date),
        _ => new DateTime(t.Year, t.Month, 1),
    };

    private static DateTime Next(DateTime t, TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => t.AddHours(1),
        TimeGrain.Day => t.AddDays(1),
        TimeGrain.Week => t.AddDays(7),
        _ => t.AddMonths(1),
    };

    private static string LabelOf(DateTime t, TimeGrain grain) => grain switch
    {
        TimeGrain.Hour => t.ToString("HH"),
        TimeGrain.Day => t.ToString("dd"),
        TimeGrain.Week => "S" + ISOWeek.GetWeekOfYear(t),
        _ => t.ToString("MMM", French).TrimEnd('.'),
    };

    private static DateTime StartOfWeek(DateTime day)
        => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
