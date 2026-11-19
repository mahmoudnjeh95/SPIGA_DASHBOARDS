using System.Globalization;
using Spiga.Core.Plant;

namespace Spiga.UI.Services;

public enum PeriodKind
{
    Today,
    Yesterday,
    Last7Days,
    Month,
    Custom,
}

// A period of the screens, in production days: a production day runs from
// 06:00 to 06:00 the next morning (the night poste belongs to the day it
// started, as the waste entries do). "Aujourd'hui" at 00:55 is therefore the
// day whose night poste is in progress: production, postes and waste of a
// period always describe the same postes.
public sealed record Period(PeriodKind Kind, DateTime CustomFrom, DateTime CustomTo)
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public const int DayStartHour = 6;

    public static Period Today(DateTime now) => new(PeriodKind.Today, Shifts.ProductionDate(now), Shifts.ProductionDate(now));

    // First and last production day, both included.
    public (DateTime First, DateTime Last) Days(DateTime now)
    {
        var today = Shifts.ProductionDate(now);
        return Kind switch
        {
            PeriodKind.Today => (today, today),
            PeriodKind.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            PeriodKind.Last7Days => (today.AddDays(-6), today),
            PeriodKind.Month => (new DateTime(today.Year, today.Month, 1), today),
            _ => (CustomFrom.Date, (CustomTo < CustomFrom ? CustomFrom : CustomTo).Date),
        };
    }

    // [From, To) in time: first day 06:00 to the morning after the last day.
    // Waste entries are dated by production day: From.Date .. To.AddDays(-1).Date.
    public (DateTime From, DateTime To) Range(DateTime now)
    {
        var (first, last) = Days(now);
        return (first.AddHours(DayStartHour), last.AddDays(1).AddHours(DayStartHour));
    }

    // Calendar days (midnight to midnight), for timestamps such as the audit trail.
    public (DateTime From, DateTime To) CalendarRange(DateTime now)
    {
        var (first, last) = Days(now);
        return (first, last.AddDays(1));
    }

    public bool IncludesNow(DateTime now)
    {
        var (from, to) = Range(now);
        return now >= from && now < to;
    }

    public string Label(DateTime now)
    {
        var (from, last) = Days(now);
        return Kind switch
        {
            PeriodKind.Today when from != now.Date
                => $"Aujourd'hui : journée du {from.ToString("dddd d MMMM yyyy", French)}, poste de nuit en cours",
            PeriodKind.Today => "Aujourd'hui, " + from.ToString("dddd d MMMM yyyy", French),
            PeriodKind.Yesterday => "Hier, " + from.ToString("dddd d MMMM yyyy", French),
            PeriodKind.Month => from.ToString("MMMM yyyy", French),
            _ when from == last => from.ToString("dddd d MMMM yyyy", French),
            _ => $"du {from.ToString("d MMM", French)} au {last.ToString("d MMM yyyy", French)}",
        };
    }

    public static string KindLabel(PeriodKind kind) => kind switch
    {
        PeriodKind.Today => "Aujourd'hui",
        PeriodKind.Yesterday => "Hier",
        PeriodKind.Last7Days => "7 jours",
        PeriodKind.Month => "Mois",
        _ => "Dates",
    };
}
