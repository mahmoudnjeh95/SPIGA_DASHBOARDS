using Spiga.Core.Plant;
using Spiga.Core.Staff;
using Spiga.Core.Waste;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// A stretch of a poste in which the person held a post: their own, or from
// ActingFrom on the one they took over (Acting).
public sealed record HeldPost(
    DateTime Date, int ShiftId, string Team, StaffFunction Function, string? MachineId, int? LineId,
    IReadOnlyList<string> Machines, DateTime From, DateTime To, bool Acting, bool StepUp)
{
    public double Hours => Math.Max(0, (To - From).TotalHours);
}

// Occasionnel: under FormedPostes postes on that post; Forme: under
// AutonomousPostes, or more but the machine runs clearly below its usual
// level under them; Autonome: the rest.
public enum SkillLevel
{
    Occasionnel,
    Forme,
    Autonome,
}

// One post the person held over the period: a function on a machine, on a
// ligne, or with no machine (nettoyage, chef de conditionnement...).
public sealed record Skill(
    StaffFunction Function, string? MachineId, int? LineId, int Postes, double Hours, double? Trs, double? UsualTrs, SkillLevel Level)
{
    public double? Gap => Trs - UsualTrs;
}

// What the machines did over a set of hour buckets. StopPerHour: minutes
// stopped per hour the machines had to run (planned breaks left out);
// KgPerHour: net production per such hour (per machine).
public sealed record Figures(
    double? Trs, double? Speed, double? Quality, double? UnderPct, double? OverPct, double? StopPerHour, double? KgPerHour,
    double Kg, long Bags, long MetalRejects, double? GiveawayKg, double? DeviationPct, double RequiredHours, double StoppedMinutes)
{
    public static readonly Figures None = new(null, null, null, null, null, null, null, 0, 0, 0, null, null, 0, 0);
}

// One poste the person held a machine, for the trend chart.
public sealed record PostePoint(DateTime Date, int ShiftId, double? Trs, double? UsualTrs, double Kg);

// Minutes the held machines stood still for one cause (Code null: not
// qualified) while the person held them.
public sealed record CauseMinutes(string? Code, string Label, CauseFamily? Family, double Minutes);

// The pointage lines of the period. Rate leaves the congés (C) out: they
// are planned. Bradford = spells² x days (A and M only): many short
// absences weigh more than one long one.
public sealed record AttendanceFigures(
    int Postes, int Present, int A, int M, int AT, int C, int Spells,
    int Lates, double LateMinutes, int Early, double EarlyMinutes, double OvertimeMinutes, double WorkedHours,
    IReadOnlyList<CalEntry> Entries)
{
    public double? Rate => Present + A + M + AT is var due and > 0 ? 100.0 * Present / due : null;
    public int Bradford => Spells * Spells * (A + M);
}

// Everything the Polyvalence page shows about one person, in its four
// groups: polyvalence, performance on the machine, stops and quality,
// attendance and reliability.
public sealed class EmployeeProfile
{
    public int EmployeeId { get; init; }
    public string Name { get; init; } = "";
    public StaffFunction Function { get; init; }
    public string Team { get; init; } = "";

    // 1. Polyvalence
    public IReadOnlyList<HeldPost> Held { get; init; } = Array.Empty<HeldPost>();
    public IReadOnlyList<ActingRecord> Acting { get; init; } = Array.Empty<ActingRecord>();
    public IReadOnlyList<Skill> Skills { get; init; } = Array.Empty<Skill>();
    public int RenfortPostes { get; init; }
    public IReadOnlyList<string> TeamsWorked { get; init; } = Array.Empty<string>();
    public int StepUps => Acting.Count(a => a.IsStepUp);
    public double StepUpHours => Acting.Where(a => a.IsStepUp).Sum(a => a.Hours);
    public double ActingHours => Acting.Sum(a => a.Hours);

    // 2. Performance on the machine (Usual: the same machines on the other
    // postes of the period, weighted by the time the person held each one)
    public Figures Own { get; init; } = Figures.None;
    public Figures Usual { get; init; } = Figures.None;
    public double? TrsSpread { get; init; }
    public IReadOnlyList<PostePoint> Trend { get; init; } = Array.Empty<PostePoint>();
    public bool HoldsMachines => Held.Any(h => h.Machines.Count > 0);

    // 3. Stops and quality (waste: packaging waste of the machine on the
    // postes the person held it, per tonne made)
    public IReadOnlyList<CauseMinutes> Causes { get; init; } = Array.Empty<CauseMinutes>();
    public double WasteKg { get; init; }
    public double? WastePerTonne { get; init; }
    public double? UsualWastePerTonne { get; init; }

    // 4. Attendance and reliability
    public AttendanceFigures Presence { get; init; } = null!;
}

public static class EmployeeProfiles
{
    public const int FormedPostes = 5;
    public const int AutonomousPostes = 20;

    // TRS points below the machine's usual level that keep a post at "formé".
    public const double AutonomousGap = 5;

    // Buckets counted in a held stretch: the hour buckets starting in it (the
    // one starting up to 30 minutes before, for an early start).
    private static bool In(ProductionBucket b, DateTime from, DateTime to) => b.StartTime >= from.AddMinutes(-30) && b.StartTime < to;

    public static IReadOnlyList<EmployeeProfile> Build(PresenceReport.Result r, IReadOnlyList<WasteEntry> waste, IPlantReferential plant)
    {
        var entries = PresenceCalendar.Entries(r.Sheets, plant);
        var lotsByMachine = r.Lots
            .Where(b => Shifts.ProductionDate(b.StartTime) is var d && d >= r.FromDate && d <= r.ToDate)
            .ToLookup(b => b.MachineId, StringComparer.OrdinalIgnoreCase);
        var packaging = waste.Where(w => w.Counts && w.Kind == WasteKind.Emballage && w.MachineId != null).ToList();
        var actingBy = r.Acting.ToLookup(a => a.EmployeeId);
        var profiles = new List<EmployeeProfile>();
        foreach (var person in entries.GroupBy(e => e.Line.EmployeeId))
        {
            var mine = person.OrderBy(e => e.Sheet.Start).ToList();
            var last = mine.LastOrDefault(e => !e.Line.IsRenfort)?.Line ?? mine[^1].Line;
            var held = Held(mine, r.Lines);
            var buckets = held.ToDictionary(h => h, h => h.Machines.SelectMany(m => lotsByMachine[m]).Where(b => In(b, h.From, h.To)).ToList());
            var ownSet = buckets.Values.SelectMany(b => b).ToHashSet();
            var usual = UsualByMachine(ownSet, lotsByMachine);
            var (perPoste, spread) = Trend(held, buckets, usual);
            var (wasteKg, wastePerTonne, usualWaste) = Waste(held, packaging, lotsByMachine, ownSet);
            profiles.Add(new EmployeeProfile
            {
                EmployeeId = person.Key,
                Name = last.FullName,
                Function = last.Function,
                Team = last.HomeTeam,
                Held = held,
                Acting = actingBy[person.Key].ToList(),
                Skills = Skills(held, buckets, usual),
                RenfortPostes = mine.Count(e => e.Line.IsRenfort && e.Line.Status == LineStatus.Present),
                TeamsWorked = mine.Where(e => e.Line.Status == LineStatus.Present).Select(e => e.Sheet.Team).Distinct().Order().ToList(),
                Own = Of(ownSet),
                Usual = Weighted(ownSet, usual),
                TrsSpread = spread,
                Trend = perPoste,
                Causes = Causes(held, r.Qualifications, Of(ownSet).StoppedMinutes),
                WasteKg = wasteKg,
                WastePerTonne = wastePerTonne,
                UsualWastePerTonne = usualWaste,
                Presence = Presence(mine),
            });
        }
        return profiles;
    }

    // The stretches of each poste the person was present: their own post
    // until ActingFrom, the post they took over from then on.
    public static List<HeldPost> Held(IEnumerable<CalEntry> entries, IReadOnlyDictionary<int, ProductionLine> lines)
    {
        var held = new List<HeldPost>();
        foreach (var e in entries)
        {
            var l = e.Line;
            if (l.Status != LineStatus.Present || l.EntryAt is not { } entry || l.ExitAt is not { } exit || exit <= entry)
                continue;
            var switchAt = l.IsActing && l.ActingFrom is { } af ? (af < entry ? entry : af > exit ? exit : af) : exit;
            if (switchAt > entry)
                held.Add(new HeldPost(e.Date, e.Sheet.ShiftId, e.Sheet.Team, l.Function, l.MachineId, l.LineId,
                    PresenceAnalysis.Machines(l, lines, DateTime.MinValue), entry, switchAt, false, false));
            if (l.IsActing && exit > switchAt)
            {
                var function = l.ActingFunction ?? l.Function;
                held.Add(new HeldPost(e.Date, e.Sheet.ShiftId, e.Sheet.Team, function, l.ActingMachineId, l.ActingLineId,
                    PresenceAnalysis.Machines(l, lines, DateTime.MaxValue), switchAt, exit, true,
                    StaffFunctions.Rank(function) > StaffFunctions.Rank(l.Function)));
            }
        }
        return held;
    }

    public static Figures Of(IReadOnlyCollection<ProductionBucket> lots)
    {
        if (lots.Count == 0)
            return Figures.None;
        var k = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots));
        var required = lots.Sum(b => Math.Max(0, b.RequiredMinutes)) / 60;
        var stopped = lots.Sum(b => Math.Max(0, b.RequiredMinutes - b.RunMinutes));
        double? Share(long n) => k.TotalBags > 0 ? 100.0 * n / k.TotalBags : null;
        return new Figures(k.TrsPercent, KpiCalculator.AverageSpeed(lots), k.QualityPercent, Share(k.BagsUnderTolerance), Share(k.BagsOverTolerance),
            required > 0 ? stopped / required : null, required > 0 ? k.ProductionKg / required : null,
            k.ProductionKg, k.TotalBags, k.MetalRejects, k.GiveawayKg, k.WeightDeviationPercent, required, stopped);
    }

    // Each machine's figures on the buckets the person was not on.
    private static Dictionary<string, Figures> UsualByMachine(HashSet<ProductionBucket> own, ILookup<string, ProductionBucket> lots)
        => own.Select(b => b.MachineId).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(m => m, m => Of(lots[m].Where(b => !own.Contains(b)).ToList()), StringComparer.OrdinalIgnoreCase);

    // The machines' usual figures, weighted by the hours the person was
    // required to run each one in these buckets.
    private static Figures Weighted(IReadOnlyCollection<ProductionBucket> own, IReadOnlyDictionary<string, Figures> usual)
    {
        var weights = own.GroupBy(b => b.MachineId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Figures: usual.GetValueOrDefault(g.Key) ?? Figures.None, Weight: Math.Max(1, g.Sum(b => Math.Max(0, b.RequiredMinutes)))))
            .ToList();
        if (weights.Count == 0)
            return Figures.None;
        double? Avg(Func<Figures, double?> f)
        {
            var known = weights.Where(w => f(w.Figures) != null).ToList();
            return known.Count == 0 ? null : known.Sum(w => f(w.Figures)!.Value * w.Weight) / known.Sum(w => w.Weight);
        }
        return new Figures(Avg(f => f.Trs), Avg(f => f.Speed), Avg(f => f.Quality), Avg(f => f.UnderPct), Avg(f => f.OverPct),
            Avg(f => f.StopPerHour), Avg(f => f.KgPerHour), 0, 0, 0, null, Avg(f => f.DeviationPct), 0, 0);
    }

    private static (List<PostePoint>, double?) Trend(
        List<HeldPost> held, Dictionary<HeldPost, List<ProductionBucket>> buckets, Dictionary<string, Figures> usual)
    {
        var points = held.Where(h => h.Machines.Count > 0)
            .GroupBy(h => (h.Date, h.ShiftId))
            .Select(g =>
            {
                var lots = g.SelectMany(h => buckets[h]).Distinct().ToList();
                var f = Of(lots);
                return new PostePoint(g.Key.Date, g.Key.ShiftId, f.Trs, Weighted(lots, usual).Trs, f.Kg);
            })
            .OrderBy(p => p.Date).ThenBy(p => p.ShiftId)
            .ToList();
        var trs = points.Where(p => p.Trs != null).Select(p => p.Trs!.Value).ToList();
        double? spread = trs.Count < 2 ? null : Math.Sqrt(trs.Sum(t => (t - trs.Average()) * (t - trs.Average())) / (trs.Count - 1));
        return (points, spread);
    }

    private static List<Skill> Skills(List<HeldPost> held, Dictionary<HeldPost, List<ProductionBucket>> buckets, Dictionary<string, Figures> usual)
        => held.GroupBy(h => (h.Function, Machine: h.MachineId?.ToUpperInvariant(), h.LineId))
            .Select(g =>
            {
                var postes = g.Select(h => (h.Date, h.ShiftId)).Distinct().Count();
                var lots = g.SelectMany(h => buckets[h]).Distinct().ToList();
                var trs = Of(lots).Trs;
                var usualTrs = Weighted(lots, usual).Trs;
                var level = postes < FormedPostes ? SkillLevel.Occasionnel
                    : postes < AutonomousPostes || trs - usualTrs < -AutonomousGap ? SkillLevel.Forme
                    : SkillLevel.Autonome;
                return new Skill(g.Key.Function, g.First().MachineId, g.Key.LineId, postes, g.Sum(h => h.Hours), trs, usualTrs, level);
            })
            .OrderByDescending(s => s.Postes)
            .ToList();

    // The qualified stops of the held machines, cut to the held stretches;
    // what is left of the stopped minutes is "non qualifié".
    private static List<CauseMinutes> Causes(List<HeldPost> held, IReadOnlyList<StopQualification> qualifications, double stoppedMinutes)
    {
        var byCode = new Dictionary<string, double>();
        foreach (var h in held.Where(h => h.Machines.Count > 0))
            foreach (var q in qualifications.Where(q => h.Machines.Contains(q.MachineId, StringComparer.OrdinalIgnoreCase)))
            {
                var a = q.StopStart > h.From ? q.StopStart : h.From;
                var b = q.StopEnd < h.To ? q.StopEnd : h.To;
                if (b > a)
                    byCode[q.CauseCode] = byCode.GetValueOrDefault(q.CauseCode) + (b - a).TotalMinutes;
            }
        var list = byCode.Select(kv => new CauseMinutes(kv.Key, StopCauses.Label(kv.Key), StopCauses.Find(kv.Key)?.Family, kv.Value)).ToList();
        var unqualified = stoppedMinutes - list.Where(c => c.Family != CauseFamily.Planifie).Sum(c => c.Minutes);
        if (unqualified >= 1)
            list.Add(new CauseMinutes(null, "Non qualifié", null, unqualified));
        return list.OrderByDescending(c => c.Minutes).ToList();
    }

    // Packaging waste entered for the held machines on the postes the
    // person held them, per tonne those machines made then; usual: the same
    // machines on their other postes, weighted by what the person made on each.
    private static (double Kg, double? PerTonne, double? Usual) Waste(
        List<HeldPost> held, List<WasteEntry> waste, ILookup<string, ProductionBucket> lots, HashSet<ProductionBucket> own)
    {
        var postes = held.SelectMany(h => h.Machines.Select(m => (h.Date.Date, h.ShiftId, Machine: m.ToUpperInvariant()))).ToHashSet();
        if (postes.Count == 0)
            return (0, null, null);
        var machines = postes.Select(p => p.Machine).Distinct().ToList();
        var kg = 0.0;
        var made = 0.0;
        var usualSum = 0.0;
        var usualWeight = 0.0;
        foreach (var m in machines)
        {
            bool Mine(DateTime date, int shift) => postes.Contains((date.Date, shift, m));
            var entries = waste.Where(w => string.Equals(w.MachineId, m, StringComparison.OrdinalIgnoreCase)).ToList();
            var production = lots[m].GroupBy(b => (Date: Shifts.ProductionDate(b.StartTime), b.ShiftId))
                .Select(g => (g.Key.Date, g.Key.ShiftId, Kg: KpiCalculator.Summarize(g.ToList(), 0).ProductionKg)).ToList();
            var myKg = entries.Where(w => Mine(w.ProductionDate, w.ShiftId)).Sum(w => w.QuantityKg);
            var myMade = production.Where(p => Mine(p.Date, p.ShiftId)).Sum(p => p.Kg);
            var otherKg = entries.Where(w => !Mine(w.ProductionDate, w.ShiftId)).Sum(w => w.QuantityKg);
            var otherMade = production.Where(p => !Mine(p.Date, p.ShiftId)).Sum(p => p.Kg);
            kg += myKg;
            made += myMade;
            if (otherMade > 0 && myMade > 0)
            {
                usualSum += otherKg / (otherMade / 1000) * myMade;
                usualWeight += myMade;
            }
        }
        return (kg, made > 0 ? kg / (made / 1000) : null, usualWeight > 0 ? usualSum / usualWeight : null);
    }

    public static AttendanceFigures Presence(IReadOnlyList<CalEntry> entries)
    {
        var ordered = entries.OrderBy(e => e.Sheet.Start).ToList();
        int Count(AbsenceCode c) => ordered.Count(e => e.Line.Status == LineStatus.Absent && e.Line.Absence == c);
        // A spell: A or M postes with no other poste in between.
        var spells = 0;
        var inSpell = false;
        foreach (var e in ordered)
        {
            var away = e.Line.Status == LineStatus.Absent && e.Line.Absence is AbsenceCode.A or AbsenceCode.M;
            if (away && !inSpell)
                spells++;
            inSpell = away;
        }
        var present = ordered.Where(e => e.Line.Status == LineStatus.Present).ToList();
        return new AttendanceFigures(
            ordered.Count, present.Count, Count(AbsenceCode.A), Count(AbsenceCode.M), Count(AbsenceCode.AT), Count(AbsenceCode.C), spells,
            present.Count(e => e.LateMinutes > 0), present.Sum(e => e.LateMinutes),
            present.Count(e => e.EarlyMinutes > 0), present.Sum(e => e.EarlyMinutes),
            ordered.Sum(e => e.OvertimeMinutes), present.Sum(e => e.WorkedHours), ordered);
    }
}
