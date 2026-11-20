using System.Globalization;
using System.Text;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Staff;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// Performance of the machines a replacement held: TRS and speed while the
// person held them, against the same machines on the other postes of the
// period (their usual level).
public sealed record ActingPerformance(double? Trs, double? UsualTrs, double? Speed, double? UsualSpeed, long Bags, double Kg = 0);

// The validated pointage sheets of a period, with what the machines did:
// lateness / absence impact, machines without anyone, replacements.
public sealed class PresenceReport(
    AttendanceService attendance, IStaffStore staff, ProductionRepository repository, AppSettings settings, TeamScheduleService teams,
    PlantData plant, AppDbOptions appDb, IDbConnectionFactory production, TimeProvider clock)
{
    public sealed record Result(
        DateTime FromDate, DateTime ToDate,
        IReadOnlyList<AttendanceSheet> Sheets,
        IReadOnlyList<PresenceEvent> Events,
        IReadOnlyList<UnstaffedMachine> Unstaffed,
        IReadOnlyList<ActingRecord> Acting,
        IReadOnlyDictionary<ActingRecord, ActingPerformance> Performance,
        IReadOnlyList<RepeatLateness> Repeats,
        IReadOnlyDictionary<int, ProductionLine> Lines,
        string? ProductionError);

    private DateTime Today => Shifts.ProductionDate(clock.GetLocalNow().DateTime);

    // permission: the right the page needs (Absences: rh:voir, Polyvalence:
    // rh-nominatif:polyvalence); only the teams where it is held count.
    public async Task<Result> LoadAsync(Actor actor, string permission, DateTime fromDate, DateTime toDate)
    {
        fromDate = fromDate.Date;
        toDate = toDate.Date > Today ? Today : toDate.Date;
        // The repeat-lateness count looks 30 days back from the end.
        var readFrom = new[] { fromDate, toDate.AddDays(-PresenceAnalysis.RepeatDays + 1) }.Min();
        var all = await attendance.ValidatedAsync(actor, permission, readFrom, toDate);
        var lines = (await staff.ListLinesAsync()).ToDictionary(l => l.Id);
        var inPeriod = all.Where(s => s.ProductionDate >= fromDate).ToList();

        History history;
        string? error = null;
        try
        {
            // Machine history for the period shown only: the days before it
            // only count lateness (repeats), not its impact.
            history = await HistoryAsync(PresenceAnalysis.MachinesConcerned(inPeriod, lines),
                Shifts.Start(fromDate, 1).AddHours(-AttendanceService.EarlyHours),
                Shifts.Start(toDate, 3).AddHours(8 + AttendanceService.OvertimeHours));
        }
        catch (Exception ex)
        {
            AppLogger.Log("Pointage : historique des machines illisible : " + ex.Message);
            history = History.Empty;
            error = "La base de production est injoignable : l'impact sur les machines n'est pas calculé (" + ex.Message + ").";
        }

        var events = PresenceAnalysis.Events(inPeriod, lines, history)
            .Concat(PresenceAnalysis.Events(all.Where(s => s.ProductionDate < fromDate), lines, History.Empty))
            .ToList();
        var acting = PresenceAnalysis.Acting(inPeriod, lines);
        var machines = (await plant.GetMachinesAsync()).Select(m => m.Id).ToList();
        var performance = acting.ToDictionary(a => a, a => history.Performance(a, acting));
        return new Result(fromDate, toDate, inPeriod,
            events.Where(e => e.Date >= fromDate).ToList(),
            error == null ? PresenceAnalysis.Unstaffed(inPeriod, machines, history) : Array.Empty<UnstaffedMachine>(),
            acting, performance,
            PresenceAnalysis.Repeats(events, toDate),
            lines, error);
    }

    // ---------- payroll export ----------

    // One row per employee: postes worked, hours, overtime, absences by
    // code, lateness, renforts. Semicolons and a BOM: Excel (French) opens it as is.
    public static byte[] PayrollCsv(IReadOnlyList<AttendanceSheet> sheets, IReadOnlyList<PresenceEvent> events, DateTime from, DateTime to)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var sb = new StringBuilder();
        sb.AppendLine($"Pointage du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}");
        sb.AppendLine("Employé;Fonction;Équipe;Postes travaillés;Heures travaillées;Heures supplémentaires;Absent (A);Maladie (M);Accident de travail (AT);Congé (C);Retards;Minutes de retard;Départs anticipés;Postes en renfort;Postes en remplacement");
        var rows = sheets.SelectMany(s => s.Lines.Select(l => (s, l))).GroupBy(x => x.l.EmployeeId);
        foreach (var g in rows.OrderBy(g => g.First().l.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            var last = g.OrderBy(x => x.s.ProductionDate).Last().l;
            var present = g.Where(x => x.l.Status == LineStatus.Present && x.l.EntryAt != null && x.l.ExitAt != null).ToList();
            var hours = present.Sum(x => (x.l.ExitAt!.Value - x.l.EntryAt!.Value).TotalHours);
            var overtime = present.Sum(x => Math.Max(0, (x.s.Start - x.l.EntryAt!.Value).TotalHours) + Math.Max(0, (x.l.ExitAt!.Value - x.s.End).TotalHours));
            int Abs(AbsenceCode c) => g.Count(x => x.l.Status == LineStatus.Absent && x.l.Absence == c);
            var late = events.Where(e => e.EmployeeId == g.Key && e.Kind == PresenceEventKind.Retard).ToList();
            var cells = new[]
            {
                last.FullName, StaffFunctions.Label(last.Function), last.HomeTeam, present.Count.ToString(fr), hours.ToString("0.##", fr),
                overtime.ToString("0.##", fr), Abs(AbsenceCode.A).ToString(fr), Abs(AbsenceCode.M).ToString(fr), Abs(AbsenceCode.AT).ToString(fr),
                Abs(AbsenceCode.C).ToString(fr), late.Count.ToString(fr), late.Sum(e => e.Minutes).ToString("0", fr),
                events.Count(e => e.EmployeeId == g.Key && e.Kind == PresenceEventKind.DepartAnticipe).ToString(fr),
                present.Count(x => x.l.IsRenfort).ToString(fr), present.Count(x => x.l.IsActing).ToString(fr),
            };
            sb.AppendLine(string.Join(";", cells.Select(Csv)));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static byte[] EventsCsv(IReadOnlyList<PresenceEvent> events, bool names)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var sb = new StringBuilder();
        sb.AppendLine("Date;Poste;Équipe;Employé;Fonction;Type;Code;De;À;Minutes;Machines;Minutes d'arrêt;Paquets perdus (est.);Tonnage perdu (kg, est.);Impact;Remplacé par");
        foreach (var e in events)
            sb.AppendLine(string.Join(";", new[]
            {
                e.Date.ToString("dd/MM/yyyy", fr), Shifts.Label(e.ShiftId), e.Team, names ? e.FullName : "—", StaffFunctions.Label(e.Function),
                PresenceLabels.Kind(e.Kind), e.Absence?.ToString() ?? "", e.From.ToString("HH:mm", fr), e.To.ToString("HH:mm", fr),
                e.Minutes.ToString("0", fr), string.Join(" ", e.Machines), e.StoppedMinutes.ToString("0", fr),
                e.PacksLost?.ToString("0", fr) ?? "", e.KgLost?.ToString("0.#", fr) ?? "", PresenceLabels.Impact(e.Impact), names ? e.CoveredBy ?? "" : "",
            }.Select(Csv)));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Csv(string value)
        => value.IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    // ---------- machine history ----------

    // concerned: the machines of the people late, gone early or absent:
    // only their detailed timeline (the slow query) is read. The production
    // of every machine comes from the hour buckets.
    private async Task<History> HistoryAsync(IReadOnlyCollection<string> concerned, DateTime from, DateTime to)
    {
        var machines = await repository.GetMachinesAsync();
        var ids = machines.Select(m => m.MachineId).ToList();
        var names = machines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var segments = concerned.Count == 0
            ? new List<LineSegment>()
            : await repository.GetLineSegmentsAsync(ids.Where(id => concerned.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList(), from, to);
        var stops = StopDetector.Detect(segments, from, to, settings, teams, names);
        var qualifications = await repository.GetStopQualificationsAsync(from.AddHours(-1), to.AddHours(1));
        List<(string MachineId, DateTime Start, DateTime End)> planned;
        try
        {
            planned = await new PlanningRepository(AppData.ConnectionFor(appDb), production).GetPlannedStopWindowsAsync(from, to);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Pointage : arrêts planifiés illisibles : " + ex.Message);
            planned = new();
        }
        var lots = await repository.GetLotsForMachinesAsync(ids, from, to);
        return new History(segments, stops, qualifications, planned, lots, BreakSchedule.DayWindows(settings.PlannedBreaks), settings);
    }

    private sealed class History : IMachineHistory
    {
        // Stops a missing person can be blamed for: not qualified, or
        // qualified "Absence opérateur" / "Micro-arrêt".
        private static readonly HashSet<string> PersonCauses = new() { "OPERATEUR", "MICRO" };

        private readonly ILookup<string, ProductionStop> _stops;
        private readonly ILookup<string, (string MachineId, DateTime Start, DateTime End)> _planned;
        private readonly ILookup<string, ProductionBucket> _lots;
        private readonly IReadOnlyList<(int Start, int End)> _breaks;
        private readonly AppSettings? _settings;

        public static History Empty { get; } = new(new(), new(), new(), new(), new(), Array.Empty<(int, int)>(), null);

        public History(List<LineSegment> segments, List<ProductionStop> stops, List<StopQualification> qualifications,
            List<(string MachineId, DateTime Start, DateTime End)> planned, List<ProductionBucket> lots,
            IReadOnlyList<(int Start, int End)> breaks, AppSettings? settings)
        {
            foreach (var stop in stops)
                stop.Qualification = qualifications.FirstOrDefault(q => q.MachineId == stop.MachineId
                    && (Math.Abs((q.StopStart - stop.Start).TotalMinutes) <= 3 || Math.Abs((q.StopStart - stop.RunStart).TotalMinutes) <= 3));
            _stops = stops.Where(s => !s.IsMostlyNoData && (s.Qualification == null || PersonCauses.Contains(s.Qualification.CauseCode)))
                .ToLookup(s => s.MachineId, StringComparer.OrdinalIgnoreCase);
            _planned = planned.ToLookup(p => p.MachineId, StringComparer.OrdinalIgnoreCase);
            _lots = lots.ToLookup(l => l.MachineId, StringComparer.OrdinalIgnoreCase);
            _breaks = breaks;
            _settings = settings;
        }

        public double StoppedMinutes(string machineId, DateTime from, DateTime to)
        {
            var total = 0.0;
            foreach (var s in _stops[machineId])
            {
                var a = s.Start > from ? s.Start : from;
                var b = s.End < to ? s.End : to;
                if (b <= a)
                    continue;
                var minutes = (b - a).TotalMinutes - BreakSchedule.OverlapMinutes(_breaks, a, b)
                              - _planned[machineId].Sum(p => Overlap(p.Start, p.End, a, b));
                total += Math.Max(0, minutes);
            }
            return total;
        }

        // From the hour buckets (minutes in which the counter moved).
        public double ProducingMinutes(string machineId, DateTime from, DateTime to)
            => _lots[machineId].Where(l => l.StartTime >= from && l.StartTime < to).Sum(l => l.RunMinutes);

        // The nominal cadence of the machine for the product it was making
        // then (Paramètres › Cadences); its measured speed around that time
        // when no cadence is set for that weight.
        public double? BagsPerMinute(string machineId, DateTime from, DateTime to)
        {
            if (_settings != null && TargetGrams(machineId, from, to) is { } grams && _settings.FindNominalRate(machineId, grams) is > 0 and var nominal)
                return nominal;
            var lots = _lots[machineId].Where(l => l.EndTime > from.AddHours(-2) && l.StartTime < to.AddHours(2)).ToList();
            return KpiCalculator.AverageSpeed(lots) is > 0 and var speed ? speed : null;
        }

        // Bags-weighted target weight of the lots of the machine around that
        // time (the last product it made before, if it stood still throughout).
        public double? TargetGrams(string machineId, DateTime from, DateTime to)
        {
            var lots = _lots[machineId].Where(l => l.TargetNetWeight > 0).ToList();
            var around = lots.Where(l => l.EndTime > from.AddHours(-1) && l.StartTime < to.AddHours(1)).ToList();
            if (around.Count == 0)
                around = lots.Where(l => l.StartTime < to && l.StartTime > to.AddHours(-24)).OrderByDescending(l => l.StartTime).Take(1).ToList();
            if (around.Count == 0)
                return null;
            var bags = around.Sum(l => (double)l.TotalBags);
            return bags > 0 ? around.Sum(l => l.TargetNetWeight * l.TotalBags) / bags : around.Average(l => l.TargetNetWeight);
        }

        // The machines' TRS while the person held them, and on the other
        // postes of the period (no replacement on them).
        public ActingPerformance Performance(ActingRecord record, IReadOnlyList<ActingRecord> all)
        {
            if (record.Machines.Count == 0 || _settings == null)
                return new ActingPerformance(null, null, null, null, 0);
            var held = record.Machines.SelectMany(m => _lots[m]).Where(l => l.StartTime >= record.From.AddMinutes(-30) && l.StartTime < record.To).ToList();
            var others = all.Where(a => a.Machines.Intersect(record.Machines, StringComparer.OrdinalIgnoreCase).Any()).ToList();
            var usual = record.Machines.SelectMany(m => _lots[m])
                .Where(l => !others.Any(a => l.StartTime >= a.From.AddMinutes(-30) && l.StartTime < a.To))
                .ToList();
            KpiSummary? Kpi(List<ProductionBucket> lots) => lots.Count == 0 ? null : KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots));
            var a = Kpi(held);
            var u = Kpi(usual);
            return new ActingPerformance(a?.TrsPercent, u?.TrsPercent, KpiCalculator.AverageSpeed(held), KpiCalculator.AverageSpeed(usual), a?.TotalBags ?? 0, a?.ProductionKg ?? 0);
        }

        private static double Overlap(DateTime s, DateTime e, DateTime from, DateTime to)
        {
            var a = s > from ? s : from;
            var b = e < to ? e : to;
            return b > a ? (b - a).TotalMinutes : 0;
        }
    }
}
