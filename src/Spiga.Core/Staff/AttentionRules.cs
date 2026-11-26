namespace Spiga.Core.Staff;

// The rules of the « À traiter » card of the Vue d'ensemble, kept in the
// settings (SettingKeys.AttentionRules) and changed in Administration ›
// Règles (right « Paramétrer À traiter et la permutation »). Every rule can be
// switched off; nothing is hard-coded in the page.
public enum AlertLevel
{
    Avertissement,
    Critique,
}

// What a repeat rule counts over: the last N postes the person was expected
// on (present or absent), or the last N days.
public enum WindowUnit
{
    Postes,
    Jours,
}

public sealed class AttentionRules
{
    public MachineRules Machines { get; set; } = new();
    public PersonnelRules Personnel { get; set; } = new();

    public const int MaxMinutes = 24 * 60;
    public const int MaxListed = 50;
    public const int MaxCount = 60;
    public const int MaxWindowPostes = 60;
    public const int MaxWindowDays = 90;

    // The first thing wrong, in words for the screen; null = valid.
    public string? Problem()
    {
        var m = Machines;
        if (m.OfflineMinutes is < 0 or > MaxMinutes || m.StopMinutes is < 0 or > MaxMinutes)
            return $"Les durées des machines se donnent en minutes, de 0 à {MaxMinutes}.";
        if (m.TrsPointsBelowTarget is < 0 or > 100)
            return "L'écart sous la cible TRS se donne en points, de 0 à 100 (vide = la bande rouge du tableau de bord).";
        if (m.TrsMinBags is < 0 or > 1_000_000)
            return "La production minimale pour juger le TRS se donne en sacs, de 0 à 1 000 000.";
        if (m.TrsMaxListed is < 1 or > MaxListed)
            return $"Le nombre de machines listées pour le TRS va de 1 à {MaxListed}.";
        foreach (var (name, r) in Personnel.All())
        {
            if (r.ToleranceMinutes is < 0 or > 480)
                return $"{name} : la tolérance va de 0 à 480 minutes.";
            if (r.Count < 1 || r.Count > MaxCount)
                return $"{name} : le nombre de fois va de 1 à {MaxCount}.";
            var max = r.Unit == WindowUnit.Postes ? MaxWindowPostes : MaxWindowDays;
            if (r.Window < 1 || r.Window > max)
                return $"{name} : la période va de 1 à {max} {(r.Unit == WindowUnit.Postes ? "postes" : "jours")}.";
            if (r.Unit == WindowUnit.Postes && r.Count > r.Window)
                return $"{name} : le nombre de fois ne peut dépasser le nombre de postes regardés.";
        }
        if (Personnel.AbsenceCodes.Count == 0)
            return "Absences : cochez au moins un code compté.";
        return null;
    }
}

public sealed class MachineRules
{
    public bool Offline { get; set; } = true;
    // Shown once the machine sent nothing for this long.
    public int OfflineMinutes { get; set; } = 10;

    public bool Stops { get; set; } = true;
    // A stop shorter than this is not listed (a stop whose start is unknown always is).
    public int StopMinutes { get; set; } = 15;

    public bool LowTrs { get; set; } = true;
    // TRS more than this many points under the machine's target; null = the
    // red band of the dashboard (the machine's tolerance, else the colour thresholds).
    public double? TrsPointsBelowTarget { get; set; }
    // Not judged before the machine made this many bags over the period.
    public long TrsMinBags { get; set; }
    public int TrsMaxListed { get; set; } = 3;

    public bool WastePending { get; set; } = true;
    public bool WasteReturned { get; set; } = true;
    public bool ProductWasteMissing { get; set; } = true;
}

public sealed class RepeatRule
{
    public bool Enabled { get; set; } = true;
    public AlertLevel Level { get; set; } = AlertLevel.Avertissement;
    // Retard / départ: under this many minutes it does not count.
    public int ToleranceMinutes { get; set; }
    // Alert from this many times...
    public int Count { get; set; } = 3;
    // ...over this many postes or days.
    public int Window { get; set; } = 5;
    public WindowUnit Unit { get; set; } = WindowUnit.Postes;

    public string Describe(string what)
        => Unit == WindowUnit.Postes && Count == Window
            ? $"{Count} {what} d'affilée"
            : $"{Count} {what} ou plus sur les {Window} derniers {(Unit == WindowUnit.Postes ? "postes" : "jours")}";
}

public sealed class PersonnelRules
{
    public RepeatRule Lateness { get; set; } = new() { ToleranceMinutes = 5, Count = 3, Window = 5, Unit = WindowUnit.Postes };
    public RepeatRule Absence { get; set; } = new() { Level = AlertLevel.Critique, Count = 2, Window = 30, Unit = WindowUnit.Jours };
    public RepeatRule EarlyLeave { get; set; } = new() { ToleranceMinutes = 5, Count = 2, Window = 30, Unit = WindowUnit.Jours };

    // The absence codes an absence alert counts (M, AT, C are justified by default).
    public List<AbsenceCode> AbsenceCodes { get; set; } = new() { AbsenceCode.A };

    public IEnumerable<(string Name, RepeatRule Rule)> All()
    {
        yield return ("Retards", Lateness);
        yield return ("Absences", Absence);
        yield return ("Départs anticipés", EarlyLeave);
    }

    // Days of sheets the analysis needs before the day it looks from: a
    // poste window is read over twice as many days (rest days, other postes).
    public int LookbackDays()
        => All().Where(x => x.Rule.Enabled)
            .Select(x => x.Rule.Unit == WindowUnit.Jours ? x.Rule.Window : Math.Max(14, x.Rule.Window * 2))
            .DefaultIfEmpty(0).Max();
}

public enum PersonAlertKind
{
    Retards,
    Absences,
    DepartsAnticipes,
}

// One person over a rule: how many times, out of what, for how long in all.
public sealed record PersonAlert(
    PersonAlertKind Kind, AlertLevel Level, int EmployeeId, string FullName, string Team,
    int Count, int Window, WindowUnit Unit, int Minutes, DateTime Last, IReadOnlyList<DateTime> Dates)
{
    public string KindLabel => Kind switch
    {
        PersonAlertKind.Retards => Count > 1 ? "retards" : "retard",
        PersonAlertKind.Absences => Count > 1 ? "absences" : "absence",
        _ => Count > 1 ? "départs anticipés" : "départ anticipé",
    };

    public string Scope => Unit == WindowUnit.Postes ? $"sur ses {Window} derniers postes" : $"sur {Window} jours";
}

// The people the personnel rules point at, from validated sheets. Pure.
public static class PersonnelAnalysis
{
    public static IReadOnlyList<PersonAlert> Alerts(IEnumerable<AttendanceSheet> sheets, PersonnelRules rules, DateTime today)
    {
        today = today.Date;
        // Each poste a person was expected on: present or absent (not the
        // "ailleurs" rows, the poste he went to has the row).
        var rows = sheets
            .Where(s => s.Status == SheetStatus.Validee && s.ProductionDate.Date <= today)
            .SelectMany(s => s.Lines.Where(l => l.Status is LineStatus.Present or LineStatus.Absent).Select(l => (Sheet: s, Line: l)))
            .GroupBy(x => x.Line.EmployeeId);
        var list = new List<PersonAlert>();
        foreach (var person in rows)
        {
            var postes = person.OrderBy(x => x.Sheet.ProductionDate).ThenBy(x => x.Sheet.Start).ToList();
            var last = postes[^1];
            Check(PersonAlertKind.Retards, rules.Lateness, x => Late(x.Sheet, x.Line, rules.Lateness.ToleranceMinutes));
            Check(PersonAlertKind.DepartsAnticipes, rules.EarlyLeave, x => Early(x.Sheet, x.Line, rules.EarlyLeave.ToleranceMinutes));
            Check(PersonAlertKind.Absences, rules.Absence,
                x => x.Line.Status == LineStatus.Absent && x.Line.Absence is { } a && rules.AbsenceCodes.Contains(a) ? Overtime.PosteMinutes : null);

            void Check(PersonAlertKind kind, RepeatRule rule, Func<(AttendanceSheet Sheet, AttendanceLine Line), int?> minutesOf)
            {
                if (!rule.Enabled)
                    return;
                var window = rule.Unit == WindowUnit.Postes
                    ? postes.TakeLast(rule.Window).ToList()
                    : postes.Where(x => x.Sheet.ProductionDate.Date > today.AddDays(-rule.Window)).ToList();
                var hits = window.Select(x => (x.Sheet, Minutes: minutesOf(x))).Where(x => x.Minutes != null).ToList();
                if (hits.Count < rule.Count)
                    return;
                list.Add(new PersonAlert(kind, rule.Level, person.Key, last.Line.FullName, last.Line.HomeTeam, hits.Count, rule.Window, rule.Unit,
                    hits.Sum(h => h.Minutes!.Value), hits[^1].Sheet.ProductionDate, hits.Select(h => h.Sheet.ProductionDate).ToList()));
            }
        }
        return list.OrderByDescending(a => a.Level).ThenByDescending(a => a.Count).ThenByDescending(a => a.Minutes)
            .ThenBy(a => a.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Minutes late past the tolerance (counted from the poste start), null = on time.
    public static int? Late(AttendanceSheet s, AttendanceLine l, int toleranceMinutes)
    {
        if (l.Status != LineStatus.Present || l.EntryAt is not { } entry)
            return null;
        var minutes = (int)((entry < s.End ? entry : s.End) - s.Start).TotalMinutes;
        return minutes > toleranceMinutes ? minutes : null;
    }

    public static int? Early(AttendanceSheet s, AttendanceLine l, int toleranceMinutes)
    {
        if (l.Status != LineStatus.Present || l.ExitAt is not { } exit)
            return null;
        var minutes = (int)(s.End - (exit > s.Start ? exit : s.Start)).TotalMinutes;
        return minutes > toleranceMinutes ? minutes : null;
    }
}
