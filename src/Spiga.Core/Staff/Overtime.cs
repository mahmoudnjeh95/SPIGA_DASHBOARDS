using Spiga.Core.Plant;

namespace Spiga.Core.Staff;

// Heures supplémentaires, worked out from the pointage sheets (nothing more
// to type). On a production day, a person's overtime is:
// * the time outside the poste of each row (in before it starts, out after
//   it ends), plus
// * the time inside postes beyond those of his own team that day: his team
//   works one poste (8 h); a second poste the same day, or a poste on a day
//   his team does not work (renfort on a rest day), is overtime whole.
// Being moved to another team's poste instead of his own (his team works
// the night, he works the morning) is not overtime.
//
// The chef de production forbids overtime to a person (Interdiction), sets a
// monthly limit for everybody (reached = forbidden until the next month),
// and can let one person go over the limit for one month (Dérogation).
public enum OvertimeDecisionKind
{
    Interdiction,
    Derogation,
}

public sealed class OvertimeDecision
{
    public long Id { get; set; }
    public int EmployeeId { get; set; }
    public OvertimeDecisionKind Kind { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string Reason { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? LiftedBy { get; set; }
    public DateTime? LiftedAt { get; set; }

    public bool IsActive => LiftedAt == null;

    public bool Covers(DateTime date) => IsActive && FromDate.Date <= date.Date && (ToDate == null || date.Date <= ToDate.Value.Date);
}

// Stored in the settings (SettingKeys.OvertimeRule). 0 = no limit.
public sealed class OvertimeRule
{
    public int MonthlyLimitHours { get; set; }
}

public interface IOvertimeStore
{
    Task<IReadOnlyList<OvertimeDecision>> ListAsync();
    Task<long> InsertAsync(OvertimeDecision decision);
    Task<bool> LiftAsync(long id, string by, DateTime at);
}

// Where a person stands for overtime on a day, before the sheet being
// entered: hours already made this month, the limit, a ban, a dérogation.
public sealed record OvertimeState(int MonthMinutes, int LimitMinutes, OvertimeDecision? Ban, OvertimeDecision? Derogation)
{
    public bool HasLimit => LimitMinutes > 0 && Derogation == null;
    public bool LimitReached => HasLimit && MonthMinutes >= LimitMinutes;
    public bool Restricted => Ban != null || LimitReached;

    // Overtime minutes still allowed this month, null = no cap.
    public int? Remaining => Ban != null ? 0 : HasLimit ? Math.Max(0, LimitMinutes - MonthMinutes) : null;

    public string Label => Ban != null ? "Interdit d'heures sup."
        : LimitReached ? "Interdit · limite atteinte"
        : Derogation != null ? "Dérogation ce mois"
        : "Autorisé";

    public string Detail => Ban != null
        ? "Interdit par " + Ban.CreatedBy + (Ban.Reason.Length > 0 ? $" : « {Ban.Reason} »" : "") + (Ban.ToDate is { } to ? $" (jusqu'au {to:dd/MM/yyyy})" : "")
        : LimitReached ? $"Limite de {Overtime.Hours(LimitMinutes)} par mois atteinte ({Overtime.Hours(MonthMinutes)} ce mois) : interdit jusqu'au mois prochain."
        : HasLimit ? $"{Overtime.Hours(MonthMinutes)} sur {Overtime.Hours(LimitMinutes)} ce mois"
        : Derogation != null ? "Peut dépasser la limite ce mois" + (Derogation.Reason.Length > 0 ? $" : « {Derogation.Reason} »" : "")
        : $"{Overtime.Hours(MonthMinutes)} ce mois";
}

public static class Overtime
{
    public const int PosteMinutes = 8 * 60;

    public static DateTime MonthStart(DateTime date) => new(date.Year, date.Month, 1);

    public static DateTime MonthEnd(DateTime date) => MonthStart(date).AddMonths(1).AddDays(-1);

    // "19 h 30", "20 h", "45 min"
    public static string Hours(int minutes)
    {
        if (minutes < 60)
            return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00}";
    }

    public static bool Worked(AttendanceLine l)
        => l.Status is LineStatus.Present or LineStatus.AConfirmer && l.EntryAt is { } a && l.ExitAt is { } b && b > a;

    // Minutes of the row inside the poste of the sheet, and outside it.
    public static (int Inside, int Outside) Split(AttendanceSheet sheet, AttendanceLine l)
    {
        if (!Worked(l))
            return (0, 0);
        var entry = l.EntryAt!.Value;
        var exit = l.ExitAt!.Value;
        var total = (int)(exit - entry).TotalMinutes;
        var from = entry > sheet.Start ? entry : sheet.Start;
        var to = exit < sheet.End ? exit : sheet.End;
        var inside = to > from ? (int)(to - from).TotalMinutes : 0;
        return (inside, total - inside);
    }

    // Minutes of postes the person's own team works that day.
    public static int Allowance(IPlantReferential plant, DateTime date, string homeTeam)
        => Shifts.All.Count(s => string.Equals(plant.TeamOnShift(date.Date, s), homeTeam, StringComparison.OrdinalIgnoreCase)) * PosteMinutes;

    // Overtime of one person on one day, from his rows of that day.
    public static int Day(IEnumerable<(AttendanceSheet Sheet, AttendanceLine Line)> rows, int allowance)
    {
        var inside = 0;
        var outside = 0;
        foreach (var (s, l) in rows)
        {
            var (i, o) = Split(s, l);
            inside += i;
            outside += o;
        }
        return outside + Math.Max(0, inside - allowance);
    }
}

// The sheets of a month and the decisions in force: every overtime figure
// and check of a pointage sheet. Built by OvertimeService.BookAsync; used
// the same way by the screen (as the chef types) and by the save.
public sealed class OvertimeBook
{
    private readonly IReadOnlyList<AttendanceSheet> _sheets;
    private readonly IReadOnlyList<OvertimeDecision> _decisions;
    private readonly IPlantReferential _plant;

    public OvertimeBook(DateTime month, IReadOnlyList<AttendanceSheet> sheets, IReadOnlyList<OvertimeDecision> decisions, int limitMinutes, IPlantReferential plant)
    {
        Month = Overtime.MonthStart(month);
        _sheets = sheets;
        _decisions = decisions;
        LimitMinutes = limitMinutes;
        _plant = plant;
    }

    public DateTime Month { get; }
    public int LimitMinutes { get; }
    public IReadOnlyList<AttendanceSheet> Sheets => _sheets;

    private static bool Same(AttendanceSheet a, AttendanceSheet b)
        => a.Id != 0 && a.Id == b.Id
           || a.ProductionDate.Date == b.ProductionDate.Date && a.ShiftId == b.ShiftId && string.Equals(a.Team, b.Team, StringComparison.OrdinalIgnoreCase);

    // The stored sheets, the edited one in place of its stored version
    // (with = null: as stored; skip: without that sheet at all).
    private IEnumerable<AttendanceSheet> View(AttendanceSheet? with, AttendanceSheet? skip)
    {
        var drop = with ?? skip;
        foreach (var s in _sheets)
            if (drop == null || !Same(s, drop))
                yield return s;
        if (with != null)
            yield return with;
    }

    public OvertimeDecision? Ban(int employeeId, DateTime date)
        => _decisions.Where(d => d.EmployeeId == employeeId && d.Kind == OvertimeDecisionKind.Interdiction && d.Covers(date)).MaxBy(d => d.CreatedAt);

    public OvertimeDecision? Derogation(int employeeId, DateTime date)
        => _decisions.Where(d => d.EmployeeId == employeeId && d.Kind == OvertimeDecisionKind.Derogation && d.Covers(date)).MaxBy(d => d.CreatedAt);

    public IReadOnlyList<OvertimeDecision> Decisions => _decisions;

    private int Day(IEnumerable<AttendanceSheet> sheets, int employeeId, DateTime date)
    {
        var rows = sheets.Where(s => s.ProductionDate.Date == date.Date)
            .SelectMany(s => s.Lines.Where(l => l.EmployeeId == employeeId).Select(l => (s, l)))
            .ToList();
        if (rows.Count == 0)
            return 0;
        return Overtime.Day(rows, Overtime.Allowance(_plant, date, rows[0].l.HomeTeam));
    }

    private int MonthTotal(IEnumerable<AttendanceSheet> sheets, int employeeId)
    {
        var list = sheets.Where(s => s.ProductionDate >= Month && s.ProductionDate < Month.AddMonths(1)).ToList();
        return list.Where(s => s.Lines.Any(l => l.EmployeeId == employeeId))
            .Select(s => s.ProductionDate.Date).Distinct()
            .Sum(d => Day(list, employeeId, d));
    }

    // Overtime of each day of the month, by person (the statistics).
    public IReadOnlyDictionary<int, IReadOnlyDictionary<DateTime, int>> ByDay()
    {
        var result = new Dictionary<int, IReadOnlyDictionary<DateTime, int>>();
        var list = _sheets.Where(s => s.ProductionDate >= Month && s.ProductionDate < Month.AddMonths(1)).ToList();
        foreach (var id in list.SelectMany(s => s.Lines).Select(l => l.EmployeeId).Distinct())
        {
            var days = new Dictionary<DateTime, int>();
            foreach (var d in list.Where(s => s.Lines.Any(l => l.EmployeeId == id)).Select(s => s.ProductionDate.Date).Distinct())
            {
                var m = Day(list, id, d);
                if (m > 0)
                    days[d] = m;
            }
            if (days.Count > 0)
                result[id] = days;
        }
        return result;
    }

    public int MonthMinutes(int employeeId) => MonthTotal(_sheets, employeeId);

    // Before `sheet` (without it): what the chef sees next to the name.
    public OvertimeState State(int employeeId, DateTime date, AttendanceSheet? sheet = null)
        => new(MonthTotal(View(null, sheet), employeeId), LimitMinutes, Ban(employeeId, date), Derogation(employeeId, date));

    // Present (or still to confirm) in another sheet of the same production
    // day: for a forbidden person, he was moved to that poste.
    public bool WorksElsewhere(int employeeId, AttendanceSheet sheet)
        => View(null, sheet).Any(s => s.ProductionDate.Date == sheet.ProductionDate.Date
                                      && s.Lines.Any(l => l.EmployeeId == employeeId && l.Status == LineStatus.Present && Overtime.Worked(l)));

    // Overtime this row may still take (minutes), null = no cap: for the
    // hour pickers.
    public int? Cap(AttendanceSheet sheet, AttendanceLine line) => State(line.EmployeeId, sheet.ProductionDate, sheet).Remaining;

    // What the sheet would add that a ban or the limit refuses. Only what
    // the sheet adds: a sheet saved before a ban still saves as it was.
    public IReadOnlyList<(int EmployeeId, string Message)> Problems(AttendanceSheet sheet)
    {
        var list = new List<(int, string)>();
        var date = sheet.ProductionDate.Date;
        var after = View(sheet, null).ToList();
        var before = _sheets;
        foreach (var l in sheet.Lines.Where(Overtime.Worked).GroupBy(l => l.EmployeeId).Select(g => g.First()))
        {
            var ban = Ban(l.EmployeeId, date);
            var derogation = Derogation(l.EmployeeId, date);
            var limited = LimitMinutes > 0 && derogation == null;
            if (ban == null && !limited)
                continue;
            var dayAfter = Day(after, l.EmployeeId, date);
            var dayBefore = Day(before, l.EmployeeId, date);
            if (dayAfter <= dayBefore)
                continue;
            var (_, outside) = Overtime.Split(sheet, l);
            var why = outside > 0
                ? $"heures du poste seulement ({sheet.Start:HH:mm} – {sheet.End:HH:mm})"
                : Overtime.Allowance(_plant, date, l.HomeTeam) == 0
                    ? "son équipe ne travaille pas ce jour-là : ce poste serait en heures supplémentaires"
                    : "il a déjà fait son poste ce jour-là : un deuxième poste serait en heures supplémentaires";
            if (ban != null)
            {
                list.Add((l.EmployeeId, $"{l.FullName} est interdit d'heures supplémentaires" + (ban.Reason.Length > 0 ? $" (« {ban.Reason} »)" : "") + $" : {why}."));
                continue;
            }
            var monthAfter = MonthTotal(after, l.EmployeeId);
            var monthBefore = MonthTotal(before, l.EmployeeId);
            if (monthAfter > LimitMinutes && monthAfter > monthBefore)
            {
                var others = MonthTotal(View(null, sheet), l.EmployeeId);
                var left = Math.Max(0, LimitMinutes - others);
                list.Add((l.EmployeeId, others >= LimitMinutes
                    ? $"{l.FullName} a atteint la limite de {Overtime.Hours(LimitMinutes)} d'heures sup. ce mois ({Overtime.Hours(others)}) : {why}."
                    : $"{l.FullName} : {Overtime.Hours(left)} d'heures sup. au plus ce mois (limite {Overtime.Hours(LimitMinutes)}, {Overtime.Hours(others)} déjà faites)."));
            }
        }
        return list;
    }

    // Why this person cannot be added as a renfort at the poste's hours,
    // null when he can.
    public string? RenfortRefusal(AttendanceSheet sheet, Employee e, EmployeePosting posting)
    {
        var trial = sheet.Clone();
        trial.Lines.Add(new AttendanceLine
        {
            EmployeeId = e.Id, FullName = e.FullName, Function = posting.Function, HomeTeam = posting.Team, IsRenfort = true,
            Status = LineStatus.Present, EntryAt = sheet.Start, ExitAt = sheet.End,
        });
        var p = Problems(trial).FirstOrDefault(x => x.EmployeeId == e.Id);
        return p.Message;
    }
}
