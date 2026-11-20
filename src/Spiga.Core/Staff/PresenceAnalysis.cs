namespace Spiga.Core.Staff;

public enum PresenceEventKind
{
    Retard,
    DepartAnticipe,
    Absence,
}

// SansImpact: the machine produced while the person was missing (someone
// started it). AvecImpact: it stood still. Compensee: an absence covered by
// a replacement, the machine kept producing. SansMachine: the person holds
// no machine (nettoyage, chef de conditionnement): minutes only.
public enum ImpactKind
{
    SansImpact,
    AvecImpact,
    Compensee,
    SansMachine,
}

public static class PresenceLabels
{
    public static string Kind(PresenceEventKind k) => k switch
    {
        PresenceEventKind.Retard => "Retard",
        PresenceEventKind.DepartAnticipe => "Départ anticipé",
        _ => "Absence",
    };

    public static string Impact(ImpactKind k) => k switch
    {
        ImpactKind.SansImpact => "Sans impact",
        ImpactKind.AvecImpact => "Avec impact",
        ImpactKind.Compensee => "Compensée",
        _ => "Sans machine",
    };

    public static string Css(ImpactKind k) => k switch
    {
        ImpactKind.AvecImpact => "bad",
        ImpactKind.Compensee or ImpactKind.SansImpact => "ok",
        _ => "",
    };
}

// One lateness, early departure or absence, and what it did to production.
public sealed record PresenceEvent(
    DateTime Date, int ShiftId, string Team, int EmployeeId, string FullName, StaffFunction Function,
    PresenceEventKind Kind, AbsenceCode? Absence, DateTime From, DateTime To,
    IReadOnlyList<string> Machines, double StoppedMinutes, double? PacksLost, ImpactKind Impact, string? CoveredBy, double? KgLost = null)
{
    public double Minutes => (To - From).TotalMinutes;
}

// A machine that produced during a poste while no one pointed present held
// it: a forgotten row, or an assignment to correct.
public sealed record UnstaffedMachine(DateTime Date, int ShiftId, string Team, string MachineId, double ProducingMinutes);

// One temporary assignment: who held whose post, from when to when.
public sealed record ActingRecord(
    DateTime Date, int ShiftId, string Team, int EmployeeId, string FullName, StaffFunction OwnFunction, StaffFunction ActingFunction,
    IReadOnlyList<string> Machines, string Post, string ReplacedName, DateTime From, DateTime To)
{
    public bool IsStepUp => StaffFunctions.Rank(ActingFunction) > StaffFunctions.Rank(OwnFunction);
    public double Hours => Math.Max(0, (To - From).TotalHours);
}

public sealed record RepeatLateness(int EmployeeId, string FullName, string Team, int Count, int WithImpact, double Minutes);

// What the machines did, read from the production database by the host.
public interface IMachineHistory
{
    // Minutes the machine stood still in [from, to) that can be put down to
    // a missing person: detected stops not qualified with another cause
    // (panne, changeover, planned stop...), planned breaks left out.
    double StoppedMinutes(string machineId, DateTime from, DateTime to);

    double ProducingMinutes(string machineId, DateTime from, DateTime to);

    // Cadence (bags per minute) of the machine for what it was making over
    // that time: its nominal rate for that product, else its measured speed;
    // null = unknown.
    double? BagsPerMinute(string machineId, DateTime from, DateTime to);

    // Target net weight (grams per bag) of what the machine was making
    // then, null = unknown.
    double? TargetGrams(string machineId, DateTime from, DateTime to);
}

// The impact rules, on validated sheets. Pure: the host supplies the
// machines' history.
public static class PresenceAnalysis
{
    // A machine producing this long in a poste needs someone on it.
    public const double UnstaffedMinutes = 30;

    public const int RepeatDays = 30;
    public const int RepeatCount = 3;

    // The machines a person holds on a poste: his own (or his ligne's), or
    // the post he took over from `at` on.
    public static IReadOnlyList<string> Machines(AttendanceLine l, IReadOnlyDictionary<int, ProductionLine> lines, DateTime? at = null)
    {
        if (l.IsActing && (at == null || l.ActingFrom <= at))
            return l.ActingMachineId != null ? new[] { l.ActingMachineId }
                : l.ActingLineId is { } al && lines.TryGetValue(al, out var acting) ? acting.Machines : Array.Empty<string>();
        if (l.MachineId != null)
            return new[] { l.MachineId };
        return l.LineId is { } id && lines.TryGetValue(id, out var line) ? line.Machines : Array.Empty<string>();
    }

    // The machines whose history the events of these sheets need.
    public static IReadOnlyCollection<string> MachinesConcerned(IEnumerable<AttendanceSheet> sheets, IReadOnlyDictionary<int, ProductionLine> lines)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
            foreach (var l in s.Lines)
            {
                var concerned = l.Status == LineStatus.Absent
                                || l.Status == LineStatus.Present && (l.EntryAt > s.Start || l.ExitAt < s.End);
                if (!concerned)
                    continue;
                foreach (var m in Machines(l, lines))
                    set.Add(m);
                if (l.IsActing && l.MachineId != null)
                    set.Add(l.MachineId);
            }
        return set;
    }

    public static IReadOnlyList<PresenceEvent> Events(
        IEnumerable<AttendanceSheet> sheets, IReadOnlyDictionary<int, ProductionLine> lines, IMachineHistory history)
    {
        var events = new List<PresenceEvent>();
        foreach (var s in sheets.Where(s => s.Status == SheetStatus.Validee))
        {
            foreach (var l in s.Lines)
            {
                if (l.Status == LineStatus.Present && l.EntryAt is { } entry && l.ExitAt is { } exit)
                {
                    if (entry > s.Start)
                        events.Add(Build(s, l, PresenceEventKind.Retard, s.Start, Min(entry, s.End), Machines(l, lines, s.Start),
                            history, new[] { (s.Start, Min(entry, s.End)) }, null));
                    if (exit < s.End)
                        events.Add(Build(s, l, PresenceEventKind.DepartAnticipe, Max(exit, s.Start), s.End, Machines(l, lines, exit),
                            history, new[] { (Max(exit, s.Start), s.End) }, null));
                }
                else if (l.Status == LineStatus.Absent)
                {
                    var cover = s.Lines.FirstOrDefault(x => x.ReplacesEmployeeId == l.EmployeeId && x.Status == LineStatus.Present);
                    var uncovered = new List<(DateTime, DateTime)>();
                    if (cover?.ActingFrom is { } from && cover.ExitAt is { } until)
                    {
                        if (from > s.Start)
                            uncovered.Add((s.Start, Min(from, s.End)));
                        if (until < s.End)
                            uncovered.Add((Max(until, s.Start), s.End));
                    }
                    else
                        uncovered.Add((s.Start, s.End));
                    events.Add(Build(s, l, PresenceEventKind.Absence, s.Start, s.End, Machines(l, lines), history, uncovered, cover?.FullName));
                }
            }
        }
        return events.OrderBy(e => e.Date).ThenBy(e => e.ShiftId).ThenBy(e => e.FullName).ToList();
    }

    private static PresenceEvent Build(
        AttendanceSheet s, AttendanceLine l, PresenceEventKind kind, DateTime from, DateTime to, IReadOnlyList<string> machines,
        IMachineHistory history, IReadOnlyList<(DateTime From, DateTime To)> exposed, string? coveredBy)
    {
        ImpactKind impact;
        double stopped = 0;
        double? packs = null;
        double? kg = null;
        if (machines.Count == 0)
            impact = ImpactKind.SansMachine;
        else
        {
            foreach (var m in machines)
                foreach (var (a, b) in exposed.Where(x => x.To > x.From))
                {
                    var minutes = history.StoppedMinutes(m, a, b);
                    if (minutes <= 0)
                        continue;
                    stopped += minutes;
                    // Cadence (bags/min) × minutes impacted × target weight.
                    if (history.BagsPerMinute(m, a, b) is { } rate)
                    {
                        packs = (packs ?? 0) + minutes * rate;
                        if (history.TargetGrams(m, a, b) is { } grams)
                            kg = (kg ?? 0) + minutes * rate * grams / 1000;
                    }
                }
            stopped = Math.Round(stopped, 1);
            impact = stopped >= 1 ? ImpactKind.AvecImpact : coveredBy != null ? ImpactKind.Compensee : ImpactKind.SansImpact;
        }
        return new PresenceEvent(s.ProductionDate, s.ShiftId, s.Team, l.EmployeeId, l.FullName, l.Function, kind, l.Absence, from, to,
            machines, stopped, packs is { } p ? Math.Round(p) : null, impact, coveredBy, kg is { } k ? Math.Round(k, 1) : null);
    }

    public static IReadOnlyList<UnstaffedMachine> Unstaffed(
        IEnumerable<AttendanceSheet> sheets, IEnumerable<string> machineIds, IMachineHistory history)
    {
        var list = new List<UnstaffedMachine>();
        var all = machineIds.ToList();
        foreach (var poste in sheets.Where(s => s.Status == SheetStatus.Validee).GroupBy(s => (s.ProductionDate, s.ShiftId)))
        {
            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in poste.SelectMany(s => s.Lines).Where(l => l.Status == LineStatus.Present))
            {
                if (l.IsActing && l.ActingMachineId != null)
                    held.Add(l.ActingMachineId);
                // A person moved to another post leaves his machine only if
                // someone else is not on it; until then it counts as his.
                if (l.MachineId != null && (!l.IsActing || l.ActingFrom > poste.First().Start))
                    held.Add(l.MachineId);
            }
            var first = poste.First();
            foreach (var m in all.Where(m => !held.Contains(m)))
            {
                var producing = history.ProducingMinutes(m, first.Start, first.End);
                if (producing >= UnstaffedMinutes)
                    list.Add(new UnstaffedMachine(first.ProductionDate, first.ShiftId, string.Join(", ", poste.Select(s => s.Team)), m, Math.Round(producing)));
            }
        }
        return list.OrderByDescending(u => u.Date).ThenByDescending(u => u.ShiftId).ThenBy(u => u.MachineId).ToList();
    }

    public static IReadOnlyList<ActingRecord> Acting(IEnumerable<AttendanceSheet> sheets, IReadOnlyDictionary<int, ProductionLine> lines)
    {
        var list = new List<ActingRecord>();
        foreach (var s in sheets.Where(s => s.Status == SheetStatus.Validee))
            foreach (var l in s.Lines.Where(l => l.Status == LineStatus.Present && l.IsActing && l.ActingFunction != null))
            {
                var replaced = s.Lines.FirstOrDefault(x => x.EmployeeId == l.ReplacesEmployeeId);
                var post = l.ActingMachineId ?? (l.ActingLineId is { } id && lines.TryGetValue(id, out var line) ? "Ligne " + line.Name : "Ligne");
                list.Add(new ActingRecord(s.ProductionDate, s.ShiftId, s.Team, l.EmployeeId, l.FullName, l.Function, l.ActingFunction!.Value,
                    Machines(l, lines, l.ActingFrom), post, replaced?.FullName ?? "?", l.ActingFrom ?? s.Start, l.ExitAt ?? s.End));
            }
        return list.OrderBy(a => a.Date).ThenBy(a => a.ShiftId).ToList();
    }

    // People late `RepeatCount` times or more in the `RepeatDays` days up to `today`.
    public static IReadOnlyList<RepeatLateness> Repeats(IEnumerable<PresenceEvent> events, DateTime today)
        => events.Where(e => e.Kind == PresenceEventKind.Retard && e.Date > today.AddDays(-RepeatDays) && e.Date <= today)
            .GroupBy(e => e.EmployeeId)
            .Where(g => g.Count() >= RepeatCount)
            .Select(g => new RepeatLateness(g.Key, g.First().FullName, g.Last().Team, g.Count(),
                g.Count(e => e.Impact == ImpactKind.AvecImpact), g.Sum(e => e.Minutes)))
            .OrderByDescending(r => r.WithImpact).ThenByDescending(r => r.Count)
            .ToList();

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
