using System.Globalization;
using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// Maintenance > Qualification des arrêts (web and desktop): the stops of
// today / the last 7 or 30 days that still need a cause, the form that gives
// one (to one stop, or to all the ticked ones) and the pannes declared by hand
// (the counter missed it). Same rules as the former WPF tab (PannesViewModel):
// - a cause given to one poste of a long stop goes to every poste it covers;
// - the postes a long stop runs into later get the cause it already has;
// - a stop lying in a planned stop or maintenance of the calendar is
//   qualified by itself ("Pas de production prévue" / "Maintenance préventive").
// Read and written through repositories limited to the account's machines;
// a team-bound account sees its team's postes (a long stop still gets its
// cause on every poste it covers).
public sealed class StopQualificationService(
    IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings, TeamScheduleService teams, TimeProvider clock)
{
    public static readonly string Permission = NativePages.Permission(NativePage.StopQualification);

    // Start of the comment of a cause given from the calendar.
    public const string AutoComment = "Auto (calendrier) : ";

    public static readonly IReadOnlyList<(int Days, string Label)> Periods = new[] { (1, "Aujourd'hui"), (7, "7 jours"), (30, "30 jours") };

    private DateTime Now => clock.GetLocalNow().DateTime;

    public IReadOnlyList<string> Teams => teams.TeamNames;

    public static string? TeamFor(Actor actor, string? chosen) => actor.Team ?? chosen;

    // The account's lines: active, not hidden, couscous first.
    public async Task<List<Machine>> MachinesAsync(Actor actor)
    {
        var (repository, _) = Repositories(actor, null);
        return (await repository.GetMachinesAsync())
            .Where(m => m.IsActive && !settings.HiddenMachineIds.Contains(m.MachineId))
            .OrderBy(m => Spiga.Core.Plant.Sectors.All.ToList().IndexOf(m.Sector) is var i && i < 0 ? 99 : i).ThenBy(m => m.MachineId)
            .ToList();
    }

    // Postes of the period: from 06:00 of its first production day to now.
    public (DateTime From, DateTime To) Range(int days)
    {
        var now = Now;
        var today = Postes.Of(now).ShiftDate;
        return (today.AddDays(-(Math.Max(1, days) - 1)).AddHours(6), now);
    }

    public async Task<QualBoard> LoadAsync(Actor actor, ChangeAuthor author, QualFilter filter, CancellationToken cancel = default)
    {
        var (from, to) = Range(filter.Days);
        var (productionRepo, planning) = Repositories(actor, author);
        var all = await MachinesAsync(actor);
        var machines = filter.MachineId == null ? all : all.Where(m => m.MachineId == filter.MachineId).ToList();
        var names = all.ToDictionary(m => m.MachineId, m => m.MachineName);
        var threshold = Math.Max(1, settings.StopThresholdMinutes);

        List<ProductionStop> stops = new(), micro = new();
        string? productionError = null;
        if (machines.Count > 0)
        {
            try
            {
                var segments = await productionRepo.GetLineSegmentsAsync(machines.Select(m => m.MachineId).ToList(), from, to, cancel);
                stops = StopDetector.Detect(segments, from, to, settings, teams, names);
                // Micro-stops: under the threshold, so not listed; counted for the banner.
                if (threshold > 1)
                    micro = StopDetector.Detect(segments, from, to, settings, teams, names, 1).Where(s => s.NetMinutes < threshold).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Log("Qualification des arrêts : base de production illisible : " + ex.Message);
                productionError = "Base de production injoignable : les arrêts détectés ne sont pas listés, seules les pannes déclarées le sont. " + ex.Message;
            }
        }
        cancel.ThrowIfCancellationRequested();

        var ids = machines.Select(m => m.MachineId).ToHashSet();
        var quals = (await planning.GetQualificationsAsync(from.AddHours(-1), to.AddHours(1))).Where(q => ids.Contains(q.MachineId)).ToList();
        var recent30 = await planning.GetQualificationsAsync(Now.AddDays(-30), Now.AddDays(1));

        // Each detected stop with its cause (same machine, start within 3 min, each cause once).
        var used = new HashSet<int>();
        var rows = new List<QualRow>();
        foreach (var s in stops)
        {
            var match = quals.FirstOrDefault(q => !used.Contains(q.Id) && q.MachineId == s.MachineId && Math.Abs((q.StopStart - s.Start).TotalMinutes) <= 3);
            if (match != null)
                used.Add(match.Id);
            rows.Add(new QualRow
            {
                MachineId = s.MachineId, MachineName = s.MachineName, Start = s.Start, End = s.End, RunStart = s.RunStart,
                ShiftDate = s.ShiftDate, ShiftId = s.ShiftId, Team = s.Team, Minutes = s.NetMinutes,
                IsNoData = s.IsMostlyNoData, IsLive = s.IsLive, Qualification = match,
            });
        }
        await FollowLongStopsAsync(planning, rows);
        // Pannes declared by hand (and causes whose stop the counter no longer shows).
        foreach (var q in quals.Where(q => !used.Contains(q.Id)))
            rows.Add(new QualRow
            {
                MachineId = q.MachineId, MachineName = names.GetValueOrDefault(q.MachineId, q.MachineId), Start = q.StopStart, End = q.StopEnd,
                RunStart = q.StopStart, ShiftDate = q.ShiftDate, ShiftId = q.ShiftId, Team = q.Team, Minutes = q.Minutes, Qualification = q,
            });
        // Every team's stops get their calendar cause; the list shows the chosen team's.
        var auto = await QualifyFromCalendarAsync(planning, rows, from, to);
        if (auto > 0)
            AppLogger.Log($"Qualification des arrêts : {auto} arrêt(s) qualifié(s) par le calendrier");

        return new QualBoard
        {
            From = from, To = to, Threshold = threshold, ProductionError = productionError,
            All = rows, Micro = micro, Recent30 = recent30,
            Components = (await planning.GetDistinctAsync("Component")).Concat(StopCauses.DefaultComponents)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
            Technicians = (await planning.GetDistinctAsync("Technician")).Concat(new[] { "Maintenance", "Opérateur de ligne", "Prestataire externe" })
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
        };
    }

    // The form's cause given to these stops, each with the other postes of
    // the same stop. Returns how many qualifications were written.
    public async Task<int> QualifyAsync(Actor actor, ChangeAuthor author, QualBoard board, IReadOnlyList<QualRow> targets, QualForm form)
    {
        var expected = Check(form);
        if (targets.Count == 0)
            throw new RuleViolationException("Choisissez un arrêt dans la liste.");
        var (_, planning) = Repositories(actor, author);
        var written = 0;
        foreach (var row in targets.SelectMany(r => WithSiblings(board, r)).Distinct().ToList())
        {
            var q = QualificationOf(row);
            Apply(q, form, expected);
            await planning.SaveQualificationAsync(q);
            row.Qualification = q;
            written++;
        }
        AppLogger.Log($"Arrêts qualifiés par {author.Login} : {targets.Count} arrêt(s), {written} poste(s), {form.CauseCode}");
        return written;
    }

    // A panne the counter did not see, declared by hand on one line.
    public async Task<StopQualification> DeclareAsync(Actor actor, ChangeAuthor author, IReadOnlyList<Machine> machines, ManualStop stop, QualForm form)
    {
        var expected = Check(form);
        var machine = machines.FirstOrDefault(m => m.MachineId == stop.MachineId)
                      ?? throw new RuleViolationException("Choisissez la ligne.");
        var start = stop.Date.Date + stop.From;
        var end = stop.Date.Date + stop.To;
        if (end <= start)
            end = end.AddDays(1);
        if (start > Now)
            throw new RuleViolationException("La panne ne peut pas commencer dans le futur.");
        var poste = Postes.Of(start);
        var team = teams.GetTeamForShift(poste.ShiftDate, poste.ShiftId) is { Length: > 0 } t ? t : TeamScheduleService.NoTeam;
        // A team-bound account declares the pannes of its own postes only (it would not see the others').
        if (actor.Team != null && !actor.IsOwnTeam(team))
            throw new RuleViolationException($"Le poste {Spiga.Core.Plant.Shifts.Label(poste.ShiftId)} du {poste.ShiftDate:dd/MM} est tenu par {team} : "
                                             + $"vous ne déclarez que les pannes des postes de {actor.Team}.");
        var q = new StopQualification
        {
            MachineId = machine.MachineId, StopStart = start, StopEnd = end, ShiftDate = poste.ShiftDate, ShiftId = poste.ShiftId,
            Team = team, IsManual = true,
        };
        Apply(q, form, expected);
        var (_, planning) = Repositories(actor, author);
        await planning.SaveQualificationAsync(q);
        AppLogger.Log($"Panne déclarée par {author.Login} : {q.MachineId} {q.StopStart:g} {q.CauseCode}");
        return q;
    }

    // A declared panne joins the board read before (no new read of the production database).
    public static QualRow Add(QualBoard board, StopQualification q, string machineName)
    {
        var row = new QualRow
        {
            MachineId = q.MachineId, MachineName = machineName, Start = q.StopStart, End = q.StopEnd, RunStart = q.StopStart,
            ShiftDate = q.ShiftDate, ShiftId = q.ShiftId, Team = q.Team, Minutes = q.Minutes, Qualification = q,
        };
        board.All.Add(row);
        return row;
    }

    // The cause taken off the whole stop (every poste it covers): left on
    // one poste, the next load would give it back to the others.
    public async Task<int> RemoveAsync(Actor actor, ChangeAuthor author, QualBoard board, QualRow row)
    {
        if (row.Qualification is not { Id: > 0 })
            return 0;
        var (_, planning) = Repositories(actor, author);
        var removed = 0;
        foreach (var r in WithSiblings(board, row).Where(r => r.Qualification is { Id: > 0 }).ToList())
        {
            await planning.DeleteQualificationAsync(r.Qualification!.Id);
            // A declared panne is only its cause: it leaves the list.
            if (r.IsManual)
                board.All.Remove(r);
            r.Qualification = null;
            removed++;
        }
        AppLogger.Log($"Cause retirée par {author.Login} : {row.MachineId} {row.Start:g} ({removed} poste(s))");
        return removed;
    }

    // Same component failing again on the same line in 30 days: a preventive is due.
    public static string RepeatWarning(QualBoard board, string? machineId, string machineName, string? causeCode, string component, int currentId)
    {
        if (machineId == null || string.IsNullOrWhiteSpace(component) || StopCauses.Find(causeCode)?.Family != CauseFamily.Panne)
            return "";
        var previous = board.Recent30.Count(q => q.Id != currentId && q.MachineId == machineId && q.IsPanne
            && string.Equals(q.Component, component.Trim(), StringComparison.OrdinalIgnoreCase));
        return previous >= 2
            ? $"« {component.Trim()} » sur {machineName} : {previous + 1}e panne en 30 jours. Un préventif sur ce composant est conseillé."
            : "";
    }

    // Expected return: none without a date, 06:00 without a time.
    public static DateTime? ReadExpected(DateTime? date, string time)
    {
        if (date is not { } day)
            return null;
        if (string.IsNullOrWhiteSpace(time))
            return day.Date.AddHours(6);
        if (!TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
            throw new RuleViolationException("Heure de retour prévu : format HH:mm (ex. 14:30).");
        return day.Date + t;
    }

    private static DateTime? Check(QualForm form)
    {
        if (StopCauses.Find(form.CauseCode) == null)
            throw new RuleViolationException("Choisissez une cause.");
        return ReadExpected(form.ExpectedDate, form.ExpectedTime);
    }

    private static void Apply(StopQualification q, QualForm form, DateTime? expected)
    {
        q.CauseCode = form.CauseCode!;
        q.Component = string.IsNullOrWhiteSpace(form.Component) ? null : form.Component.Trim();
        q.Technician = string.IsNullOrWhiteSpace(form.Technician) ? null : form.Technician.Trim();
        q.Comment = string.IsNullOrWhiteSpace(form.Comment) ? null : form.Comment.Trim();
        q.ExpectedEnd = expected;
    }

    private static StopQualification QualificationOf(QualRow row)
    {
        var q = row.Qualification ?? new StopQualification
        {
            MachineId = row.MachineId, StopStart = row.Start, StopEnd = row.End, ShiftDate = row.ShiftDate, ShiftId = row.ShiftId, Team = row.Team,
        };
        // A stop still running when qualified: keep its latest end.
        if (!row.IsManual)
            q.StopEnd = row.End;
        return q;
    }

    // The row and the other postes of the same stop (every team's).
    private static IEnumerable<QualRow> WithSiblings(QualBoard board, QualRow row)
        => row.IsManual
            ? new[] { row }
            : board.All.Where(r => r.MachineId == row.MachineId && r.RunStart == row.RunStart && !r.IsManual).Append(row).Distinct();

    // A long stop qualified on one of its postes: the postes it ran into
    // since then get the same cause, and a stop still going on keeps its end
    // up to date.
    private static async Task FollowLongStopsAsync(PlanningRepository planning, List<QualRow> rows)
    {
        foreach (var row in rows.Where(r => r.Qualification == null))
        {
            var model = rows.FirstOrDefault(o => o.Qualification is { IsManual: false } && o.MachineId == row.MachineId && o.RunStart == row.RunStart)?.Qualification;
            if (model == null)
                continue;
            var q = new StopQualification
            {
                MachineId = row.MachineId, StopStart = row.Start, StopEnd = row.End, ShiftDate = row.ShiftDate, ShiftId = row.ShiftId,
                Team = row.Team, CauseCode = model.CauseCode, Component = model.Component, Technician = model.Technician,
                Comment = model.Comment, ExpectedEnd = model.ExpectedEnd,
            };
            await planning.SaveQualificationAsync(q);
            row.Qualification = q;
        }
        foreach (var row in rows.Where(r => r.Qualification is { IsManual: false } q && r.End > q.StopEnd.AddMinutes(1)))
        {
            row.Qualification!.StopEnd = row.End;
            await planning.SaveQualificationAsync(row.Qualification);
        }
    }

    // A detected stop lying (80 % or more) inside a planned stop or a
    // maintenance of the calendar gets its cause by itself. Stops still
    // running wait for their end.
    private static async Task<int> QualifyFromCalendarAsync(PlanningRepository planning, List<QualRow> rows, DateTime from, DateTime to)
    {
        var open = rows.Where(r => !r.HasCause && !r.IsLive && !r.IsManual).ToList();
        if (open.Count == 0)
            return 0;
        var slots = await planning.GetEventsAsync(from, to);
        var count = 0;
        foreach (var row in open)
        {
            var length = (row.End - row.Start).TotalMinutes;
            var slot = slots.FirstOrDefault(e => e.MachineId == row.MachineId
                && (Min(e.ActualEnd, row.End) - Max(e.PlannedStart, row.Start)).TotalMinutes >= 0.8 * length);
            if (slot == null || length <= 0)
                continue;
            var q = new StopQualification
            {
                MachineId = row.MachineId, StopStart = row.Start, StopEnd = row.End, ShiftDate = row.ShiftDate, ShiftId = row.ShiftId,
                Team = row.Team, CauseCode = slot.IsStop ? "NOPLAN" : "PREV", Technician = slot.Technician, Comment = AutoComment + slot.Task,
            };
            await planning.SaveQualificationAsync(q);
            row.Qualification = q;
            count++;
        }
        return count;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private (ProductionRepository Production, PlanningRepository Planning) Repositories(Actor actor, ChangeAuthor? author)
    {
        var (productionRepo, planning) = ScopedData.For(actor, Permission, production, appDb, settings);
        planning.Author = author ?? ChangeAuthor.Account(actor.Login, actor.DisplayName);
        return (productionRepo, planning);
    }
}

// Period (1, 7 or 30 days), one line or all, one team or all, without a cause only.
public sealed record QualFilter(int Days = 7, string? MachineId = null, string? Team = null, bool OnlyOpen = true);

public sealed record QualForm(string? CauseCode, string Component = "", string Technician = "", string Comment = "",
    DateTime? ExpectedDate = null, string ExpectedTime = "");

public sealed record ManualStop(string MachineId, DateTime Date, TimeSpan From, TimeSpan To);

// One stop of the list: detected from the counter, or a panne declared by hand.
public sealed class QualRow
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    // Start of the whole stop: the rows of one long stop (one per poste / team) share it, and their cause.
    public DateTime RunStart { get; init; }
    public DateTime ShiftDate { get; init; }
    public int ShiftId { get; init; }
    public string Team { get; init; } = "";
    public double Minutes { get; init; }
    public bool IsNoData { get; init; }
    public bool IsLive { get; init; }
    public StopQualification? Qualification { get; set; }

    public string Key => $"{MachineId}|{Start:O}";
    public bool HasCause => Qualification != null;
    public bool IsManual => Qualification?.IsManual == true;
    public bool IsAuto => Qualification?.Comment?.StartsWith(StopQualificationService.AutoComment, StringComparison.Ordinal) == true;
    public CauseFamily? Family => Qualification?.Cause?.Family;
    public string CauseLabel => Qualification == null ? (IsNoData ? "Sans données" : "Sans cause")
        : StopCauses.Label(Qualification.CauseCode) + (string.IsNullOrEmpty(Qualification.Component) ? "" : " · " + Qualification.Component)
          + (IsAuto ? " · auto" : "");
}

public sealed class QualBoard
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Threshold { get; init; }
    public string? ProductionError { get; init; }
    // Every row read (all teams): siblings of a long stop.
    public List<QualRow> All { get; init; } = new();
    public List<ProductionStop> Micro { get; init; } = new();
    public List<StopQualification> Recent30 { get; init; } = new();
    public List<string> Components { get; init; } = new();
    public List<string> Technicians { get; init; } = new();

    // What the page shows for a team (null = all) and the "sans cause" box:
    // computed again after each save, without reading the databases.
    public QualView View(string? team, bool onlyOpen)
    {
        var mine = team == null ? All : All.Where(r => r.Team == team).ToList();
        var micro = team == null ? Micro : Micro.Where(s => s.Team == team).ToList();
        var detected = mine.Where(r => !r.IsManual && !r.IsLive).ToList();
        var open = mine.Where(r => !r.HasCause).ToList();
        return new QualView(
            mine.Where(r => !onlyOpen || !r.HasCause).OrderByDescending(r => r.Start).ToList(),
            mine.Where(r => r.HasCause).OrderByDescending(r => r.Qualification!.CreatedAt).Take(8).ToList(),
            open.Count, open.Sum(r => r.Minutes),
            detected.Count == 0 ? 100 : Math.Round(100.0 * detected.Count(r => r.HasCause) / detected.Count),
            mine.Count(r => r.IsAuto), micro.Count, micro.Sum(s => s.NetMinutes));
    }
}

public sealed record QualView(List<QualRow> Rows, List<QualRow> Recent, int OpenCount, double OpenMinutes, double Rate,
    int AutoCount, int MicroCount, double MicroMinutes);
