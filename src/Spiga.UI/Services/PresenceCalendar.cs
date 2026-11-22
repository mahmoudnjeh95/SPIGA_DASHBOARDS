using System.Globalization;
using System.Text;
using Spiga.Core.Plant;
using Spiga.Core.Staff;

namespace Spiga.UI.Services;

// What one pointage line says about a person on a day.
public enum CalKind
{
    Present,
    Retard,
    Depart,
    HeuresSup,
    A,
    M,
    AT,
    C,
    Renfort,
    Remplacement,
    AConfirmer,
}

public static class CalKinds
{
    public static readonly IReadOnlyList<CalKind> All = Enum.GetValues<CalKind>();

    public static string Label(CalKind k) => k switch
    {
        CalKind.Present => "Présent",
        CalKind.Retard => "Retard",
        CalKind.Depart => "Départ anticipé",
        CalKind.HeuresSup => "Heures sup.",
        CalKind.A => "Absent (A)",
        CalKind.M => "Maladie (M)",
        CalKind.AT => "Accident de travail (AT)",
        CalKind.C => "Congé (C)",
        CalKind.Renfort => "Renfort",
        CalKind.Remplacement => "Remplacement",
        _ => "À confirmer",
    };

    // The short code written in the calendar cell.
    public static string Code(CalKind k) => k switch
    {
        CalKind.Present => "✓",
        CalKind.Retard => "R",
        CalKind.Depart => "D",
        CalKind.HeuresSup => "+",
        CalKind.A => "A",
        CalKind.M => "M",
        CalKind.AT => "AT",
        CalKind.C => "C",
        CalKind.Renfort => "⇄",
        CalKind.Remplacement => "↑",
        _ => "?",
    };

    public static string Css(CalKind k) => k switch
    {
        CalKind.Present => "ok",
        CalKind.Retard or CalKind.Depart or CalKind.AConfirmer => "warn",
        CalKind.A or CalKind.M or CalKind.AT => "bad",
        CalKind.C => "leave",
        _ => "acc",
    };
}

// One person on one poste: the line of a sheet, with what it means.
// OvertimeMinutes: this poste's share of the day's overtime, by the rules of
// the Heures supplémentaires screen (PresenceCalendar.Entries).
public sealed record CalEntry(AttendanceSheet Sheet, AttendanceLine Line, IReadOnlySet<CalKind> Kinds, double OvertimeMinutes = 0)
{
    public DateTime Date => Sheet.ProductionDate;
    public double LateMinutes => Line.EntryAt is { } e && e > Sheet.Start ? (e - Sheet.Start).TotalMinutes : 0;
    public double EarlyMinutes => Line.ExitAt is { } x && x < Sheet.End ? (Sheet.End - x).TotalMinutes : 0;
    public double WorkedHours => Line.Status == LineStatus.Present && Line.EntryAt is { } e && Line.ExitAt is { } x ? (x - e).TotalHours : 0;

    // "18/11 Matin · Equipe B · 06:40–14:00 · retard 40 min · remplace KERDMINE Salem sur CC04"
    public string Describe(string? replacedName, Func<string?, string>? machineName = null)
    {
        var parts = new List<string> { $"{Date:dd/MM} {Shifts.Label(Sheet.ShiftId)} · {Sheet.Team}" };
        if (Line.Status == LineStatus.Absent && Line.Absence is { } a)
            parts.Add($"{a} · {AbsenceCodes.Label(a)}");
        else if (Line.Status == LineStatus.AConfirmer)
            parts.Add("à confirmer (feuille non terminée)");
        else
            parts.Add($"{Line.EntryAt:HH:mm}–{Line.ExitAt:HH:mm}");
        if (LateMinutes > 0) parts.Add($"retard {LateMinutes:0} min");
        if (EarlyMinutes > 0) parts.Add($"parti {EarlyMinutes:0} min avant la fin");
        if (OvertimeMinutes > 0) parts.Add($"{OvertimeMinutes:0} min d'heures sup.");
        if (Line.IsRenfort) parts.Add($"renfort (équipe {Line.HomeTeam})");
        if (Line.IsActing) parts.Add($"remplace {replacedName ?? "?"} sur {(Line.ActingMachineId is { } m ? machineName?.Invoke(m) ?? m : "sa ligne")}");
        if (Sheet.Status == SheetStatus.Brouillon) parts.Add("feuille en brouillon");
        return string.Join(" · ", parts);
    }
}

public static class PresenceCalendar
{
    public static CalEntry Entry(AttendanceSheet s, AttendanceLine l, int overtimeMinutes = 0)
    {
        var kinds = new HashSet<CalKind>();
        switch (l.Status)
        {
            case LineStatus.Present:
                kinds.Add(CalKind.Present);
                if (l.EntryAt > s.Start) kinds.Add(CalKind.Retard);
                if (l.ExitAt < s.End) kinds.Add(CalKind.Depart);
                if (overtimeMinutes > 0) kinds.Add(CalKind.HeuresSup);
                if (l.IsRenfort) kinds.Add(CalKind.Renfort);
                if (l.IsActing) kinds.Add(CalKind.Remplacement);
                break;
            case LineStatus.Absent when l.Absence is { } a:
                kinds.Add(a switch { AbsenceCode.A => CalKind.A, AbsenceCode.M => CalKind.M, AbsenceCode.AT => CalKind.AT, _ => CalKind.C });
                break;
            case LineStatus.AConfirmer:
                kinds.Add(CalKind.AConfirmer);
                break;
        }
        return new CalEntry(s, l, kinds, overtimeMinutes);
    }

    // Every line of the sheets but "en renfort ailleurs" (the other team's
    // sheet holds that day). Overtime as Overtime.Day counts it: the minutes
    // outside each poste, and the postes beyond those his own team works that
    // day (a renfort on a rest day, a second poste), the later ones first.
    public static List<CalEntry> Entries(IEnumerable<AttendanceSheet> sheets, IPlantReferential plant)
    {
        var rows = sheets.SelectMany(s => s.Lines.Where(l => l.Status != LineStatus.Ailleurs).Select(l => (Sheet: s, Line: l))).ToList();
        var overtime = new int[rows.Count];
        foreach (var day in Enumerable.Range(0, rows.Count).GroupBy(i => (rows[i].Line.EmployeeId, rows[i].Sheet.ProductionDate.Date)))
        {
            var left = Overtime.Allowance(plant, day.Key.Date, rows[day.First()].Line.HomeTeam);
            foreach (var i in day.OrderBy(i => rows[i].Sheet.Start))
            {
                var (inside, outside) = Overtime.Split(rows[i].Sheet, rows[i].Line);
                var allowed = Math.Min(inside, left);
                left -= allowed;
                overtime[i] = outside + inside - allowed;
            }
        }
        return rows.Select((r, i) => Entry(r.Sheet, r.Line, overtime[i])).ToList();
    }

    // The most telling kind of a day's entries, for the cell's colour.
    public static CalKind Main(IEnumerable<CalEntry> day)
    {
        var kinds = day.SelectMany(e => e.Kinds).ToHashSet();
        foreach (var k in new[] { CalKind.AT, CalKind.M, CalKind.A, CalKind.C, CalKind.Retard, CalKind.Depart, CalKind.AConfirmer, CalKind.Remplacement, CalKind.Renfort, CalKind.HeuresSup })
            if (kinds.Contains(k))
                return k;
        return CalKind.Present;
    }

    public static byte[] Csv(IEnumerable<CalEntry> entries, Func<int?, string?> nameOf, string title, Func<string?, string>? machineName = null)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine("Date;Poste;Équipe;Employé;Fonction;Équipe de rattachement;Statut;Code;Entrée;Sortie;Heures travaillées;Retard (min);Départ anticipé (min);Heures sup. (min);Renfort;Remplacement;Feuille");
        foreach (var e in entries.OrderBy(e => e.Date).ThenBy(e => e.Sheet.ShiftId).ThenBy(e => e.Line.FullName))
        {
            var l = e.Line;
            var cells = new[]
            {
                e.Date.ToString("dd/MM/yyyy", fr), Shifts.Label(e.Sheet.ShiftId), e.Sheet.Team, l.FullName, StaffFunctions.Label(l.Function), l.HomeTeam,
                l.Status switch { LineStatus.Present => "Présent", LineStatus.Absent => "Absent", _ => "À confirmer" },
                l.Absence?.ToString() ?? "", l.EntryAt?.ToString("HH:mm", fr) ?? "", l.ExitAt?.ToString("HH:mm", fr) ?? "",
                e.WorkedHours.ToString("0.##", fr), e.LateMinutes.ToString("0", fr), e.EarlyMinutes.ToString("0", fr), e.OvertimeMinutes.ToString("0", fr),
                l.IsRenfort ? "oui" : "", l.IsActing ? $"{nameOf(l.ReplacesEmployeeId)} sur {(l.ActingMachineId is { } m ? machineName?.Invoke(m) ?? m : "ligne")}" : "",
                e.Sheet.Status == SheetStatus.Validee ? "validée" : "brouillon",
            };
            sb.AppendLine(string.Join(";", cells.Select(c => c.IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}
