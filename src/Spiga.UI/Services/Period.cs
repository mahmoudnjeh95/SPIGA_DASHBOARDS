using System.Globalization;

namespace Spiga.UI.Services;

public enum PeriodKind
{
    Today,
    Yesterday,
    Last7Days,
    Month,
    Custom,
}

// A period of the dashboards: calendar days [From, To), the same ranges as
// the SPIGA Dashboards period selector.
public sealed record Period(PeriodKind Kind, DateTime CustomFrom, DateTime CustomTo)
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static Period Today(DateTime now) => new(PeriodKind.Today, now.Date, now.Date);

    public (DateTime From, DateTime To) Range(DateTime now)
    {
        var today = now.Date;
        return Kind switch
        {
            PeriodKind.Today => (today, today.AddDays(1)),
            PeriodKind.Yesterday => (today.AddDays(-1), today),
            PeriodKind.Last7Days => (today.AddDays(-6), today.AddDays(1)),
            PeriodKind.Month => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
            _ => (CustomFrom.Date, (CustomTo < CustomFrom ? CustomFrom : CustomTo).Date.AddDays(1)),
        };
    }

    public bool IncludesNow(DateTime now)
    {
        var (from, to) = Range(now);
        return now >= from && now < to;
    }

    public string Label(DateTime now)
    {
        var (from, to) = Range(now);
        var last = to.AddDays(-1);
        return Kind switch
        {
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
