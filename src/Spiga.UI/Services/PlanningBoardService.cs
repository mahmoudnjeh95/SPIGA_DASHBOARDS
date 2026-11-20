using System.Globalization;
using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.UI.Services;

// Maintenance > Planification des arrêts (web and desktop): the day, week or
// month of every line (maintenance, planned stops, pannes, and the orders
// recorded before), the preventive rules and what is due, the alerts a
// planner acts on. A click or a drag on an empty part of a line picks a slot
// to plan a maintenance or a stop in; a planned slot can be moved or
// stretched. Same rules as the former WPF board (BoardViewModel); read and
// written through repositories limited to the account's machines.
public sealed class PlanningBoardService(
    IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings, TimeProvider clock)
{
    public static readonly string Permission = NativePages.Permission(NativePage.StopPlanning);

    // Production orders are not part of this version: their button only says so.
    public const string OrdersUnavailableText =
        "Cette fonctionnalité n'est pas disponible dans cette version. Contactez le service client pour l'activer.";

    public const string NoValidation = "Valider une intervention demande le droit « Maintenance › Valider les interventions ».";

    public DateTime Now => clock.GetLocalNow().DateTime;

    // The account's lines: active, not hidden in Paramètres, couscous first.
    public async Task<List<Machine>> MachinesAsync(Actor actor)
    {
        var (repository, _) = Repositories(actor, null);
        return (await repository.GetMachinesAsync())
            .Where(m => m.IsActive && !settings.HiddenMachineIds.Contains(m.MachineId))
            .OrderBy(m => Spiga.Core.Plant.Sectors.All.ToList().IndexOf(m.Sector) is var i && i < 0 ? 99 : i).ThenBy(m => m.MachineId)
            .ToList();
    }

    // Everything the board shows for the period; lines: those shown
    // (states, blocks), the alerts and rules keep every line of the account.
    public async Task<PlanData> LoadAsync(Actor actor, IReadOnlyList<Machine> machines, IReadOnlyCollection<string> shownIds,
        PlanRange range, DateTime start, CancellationToken cancel = default)
    {
        var now = Now;
        var end = PlanningBoard.EndOf(start, range);
        var (productionRepo, planning) = Repositories(actor, null);
        var orders = await planning.GetOrdersAsync(start, end);
        var events = await planning.GetEventsAsync(start, end);
        var plans = await planning.GetPlansAsync();
        var openEvents = await planning.GetOpenEventsAsync();
        var pannes = (await planning.GetQualificationsAsync(start, end)).Where(q => q.IsPanne).ToList();
        cancel.ThrowIfCancellationRequested();

        // Real production of the orders already started (hour buckets).
        var produced = new Dictionary<int, double>();
        var started = orders.Where(o => o.PlannedStart < now).ToList();
        string? productionError = null;
        try
        {
            if (started.Count > 0)
            {
                var buckets = await productionRepo.GetLotsForMachinesAsync(started.Select(o => o.MachineId).Distinct().ToList(), started.Min(o => o.PlannedStart), now);
                foreach (var o in started)
                {
                    var next = orders.Where(x => x.MachineId == o.MachineId && x.PlannedStart > o.PlannedStart).Select(x => (DateTime?)x.PlannedStart).Min();
                    var until = new[] { now, next ?? DateTime.MaxValue, o.PlannedEnd.AddHours(8) }.Min();
                    var mine = buckets.Where(b => b.MachineId == o.MachineId && b.StartTime >= o.PlannedStart.AddMinutes(-30) && b.StartTime < until).ToList();
                    if (o.TargetWeightGrams > 0 && mine.Any(b => Math.Abs(b.TargetNetWeight - o.TargetWeightGrams) < 1))
                        mine = mine.Where(b => Math.Abs(b.TargetNetWeight - o.TargetWeightGrams) < 1).ToList();
                    produced[o.Id] = mine.Sum(b => b.TotalBags);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Log("Planification : avancement des ordres illisible : " + ex.Message);
            productionError = "Base de production injoignable : l'état des lignes, l'avancement des ordres et les règles d'usage ne sont pas calculés. " + ex.Message;
        }
        cancel.ThrowIfCancellationRequested();

        // Live state of the lines shown.
        var states = new Dictionary<string, MachineStatusSnapshot?>();
        if (productionError == null)
            foreach (var m in machines.Where(m => shownIds.Contains(m.MachineId)))
            {
                try { states[m.MachineId] = await productionRepo.GetLatestStatusAsync(m.MachineId); }
                catch { states[m.MachineId] = null; }
            }

        // Usage rules: the bags made since they were last done.
        var bagsSince = new Dictionary<int, double>();
        var usage = plans.Where(p => p.Kind == MaintenanceKind.Usage && p.LastDoneAt != null).ToList();
        if (usage.Count > 0 && productionError == null)
        {
            try
            {
                var buckets = await productionRepo.GetLotsForMachinesAsync(usage.Select(p => p.MachineId).Distinct().ToList(),
                    usage.Min(p => p.LastDoneAt!.Value).Date, now, BucketSize.Day);
                foreach (var p in usage)
                    bagsSince[p.Id] = buckets.Where(b => b.MachineId == p.MachineId && b.StartTime.Date >= p.LastDoneAt!.Value.Date).Sum(b => b.TotalBags);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Log("Planification : sacs des règles d'usage illisibles : " + ex.Message);
            }
        }

        return new PlanData
        {
            Now = now, Range = range, Start = start, End = end, ProductionError = productionError,
            Machines = machines.ToList(), Shown = machines.Where(m => shownIds.Contains(m.MachineId)).ToList(),
            Orders = orders, Events = events, Plans = plans, OpenEvents = openEvents, Pannes = pannes,
            Produced = produced, States = states, BagsSince = bagsSince,
        };
    }

    // ---- writes ----

    // A maintenance or a planned stop, new (from a rule: planId) or changed.
    public async Task<MaintenanceEvent> SaveEventAsync(Actor actor, ChangeAuthor author, IReadOnlyList<Machine> machines, EventForm form,
        MaintenanceEvent? editing, int? planId)
    {
        var machine = machines.FirstOrDefault(m => m.MachineId == form.MachineId);
        if (machine == null || string.IsNullOrWhiteSpace(form.Task))
            throw new RuleViolationException(form.Kind == SlotKind.PlannedStop ? "Ligne et motif sont nécessaires." : "Ligne et tâche sont nécessaires.");
        var (start, end) = PlanningBoard.Span(form.StartDate, form.StartTime, form.EndDate, form.EndTime);
        var e = editing ?? new MaintenanceEvent { PlanId = planId };
        e.Kind = form.Kind;
        e.MachineId = machine.MachineId;
        e.Task = form.Task.Trim();
        e.PlannedStart = start;
        e.DurationMinutes = (int)Math.Round((end - start).TotalMinutes);
        e.Technician = form.Kind == SlotKind.PlannedStop || string.IsNullOrWhiteSpace(form.Technician) ? null : form.Technician.Trim();
        var (_, planning) = Repositories(actor, author);
        await planning.SaveEventAsync(e);
        return e;
    }

    // A planned slot moved or stretched on the board.
    public async Task MoveEventAsync(Actor actor, ChangeAuthor author, MaintenanceEvent e, DateTime start, DateTime end)
    {
        if (e.Status != PlanStatus.Planned)
            throw new RuleViolationException("Seul un créneau encore planifié se déplace.");
        if (end <= start)
            throw new RuleViolationException("La fin doit être après le début.");
        var (oldStart, oldMinutes) = (e.PlannedStart, e.DurationMinutes);
        e.PlannedStart = start;
        e.DurationMinutes = (int)Math.Round((end - start).TotalMinutes);
        try
        {
            var (_, planning) = Repositories(actor, author);
            await planning.SaveEventAsync(e);
        }
        catch
        {
            (e.PlannedStart, e.DurationMinutes) = (oldStart, oldMinutes);
            throw;
        }
    }

    // "Terminer" / "Valider" of the detail panel.
    public async Task CloseAsync(Actor actor, ChangeAuthor author, PlanData data, object source, bool canValidate)
    {
        var now = Now;
        var (_, planning) = Repositories(actor, author);
        switch (source)
        {
            case ProductionOrder o:
                o.Status = PlanStatus.Done;
                if (o.PlannedEnd > now && o.PlannedStart < now)
                    o.PlannedEnd = now;
                await planning.SaveOrderAsync(o);
                break;
            case MaintenanceEvent { IsStop: true } e:
                // Ended early: only the time really stopped stays out of the TRS.
                e.Status = PlanStatus.Done;
                e.DoneAt = now;
                e.DurationMinutes = Math.Max(1, (int)Math.Round((now - e.PlannedStart).TotalMinutes));
                await planning.SaveEventAsync(e);
                break;
            case MaintenanceEvent when !canValidate:
                throw new RuleViolationException(NoValidation);
            case MaintenanceEvent e:
                // Ended early: the rest of the slot goes back to the TRS.
                if (e.IsActiveAt(now))
                    e.DurationMinutes = Math.Max(1, (int)Math.Round((now - e.PlannedStart).TotalMinutes));
                e.Status = PlanStatus.Done;
                e.DoneAt = now;
                await planning.SaveEventAsync(e);
                if (data.Plans.FirstOrDefault(p => p.Id == e.PlanId) is { } plan)
                {
                    plan.LastDoneAt = now;
                    await planning.SavePlanAsync(plan);
                }
                break;
        }
    }

    // "Supprimer": cancelled (kept for the history and the audit).
    public async Task DeleteAsync(Actor actor, ChangeAuthor author, object source)
    {
        var (_, planning) = Repositories(actor, author);
        switch (source)
        {
            case ProductionOrder o:
                o.Status = PlanStatus.Cancelled;
                await planning.SaveOrderAsync(o);
                break;
            case MaintenanceEvent e:
                e.Status = PlanStatus.Cancelled;
                await planning.SaveEventAsync(e);
                break;
        }
    }

    public async Task<MaintenancePlan> SavePlanAsync(Actor actor, ChangeAuthor author, IReadOnlyList<Machine> machines, PlanForm form, MaintenancePlan? editing)
    {
        var machine = machines.FirstOrDefault(m => m.MachineId == form.MachineId);
        var interval = PlanningBoard.Number(form.Interval);
        var minutes = (int)PlanningBoard.Number(form.Duration);
        if (machine == null || string.IsNullOrWhiteSpace(form.Task) || interval <= 0 || minutes <= 0)
            throw new RuleViolationException("Ligne, tâche, intervalle et durée sont nécessaires.");
        var p = editing ?? new MaintenancePlan();
        p.MachineId = machine.MachineId;
        p.Task = form.Task.Trim();
        p.Kind = form.Kind;
        p.IntervalDays = p.Kind == MaintenanceKind.Calendar ? (int)interval : 0;
        p.IntervalBags = p.Kind == MaintenanceKind.Usage ? (long)interval : 0;
        p.DurationMinutes = minutes;
        p.LastDoneAt = form.LastDone?.Date.AddHours(6);
        var (_, planning) = Repositories(actor, author);
        await planning.SavePlanAsync(p);
        return p;
    }

    public async Task DeletePlanAsync(Actor actor, ChangeAuthor author, MaintenancePlan p)
    {
        p.IsActive = false;
        var (_, planning) = Repositories(actor, author);
        await planning.SavePlanAsync(p);
    }

    // "Fait": the rule done now (its due date starts again), and its open slots validated.
    public async Task MarkPlanDoneAsync(Actor actor, ChangeAuthor author, MaintenancePlan p, bool canValidate)
    {
        if (!canValidate)
            throw new RuleViolationException(NoValidation);
        var now = Now;
        var (_, planning) = Repositories(actor, author);
        p.LastDoneAt = now;
        await planning.SavePlanAsync(p);
        foreach (var e in (await planning.GetOpenEventsAsync()).Where(e => e.PlanId == p.Id))
        {
            e.Status = PlanStatus.Done;
            e.DoneAt = now;
            await planning.SaveEventAsync(e);
        }
    }

    private (ProductionRepository Production, PlanningRepository Planning) Repositories(Actor actor, ChangeAuthor? author)
    {
        var (productionRepo, planning) = ScopedData.For(actor, Permission, production, appDb, settings);
        planning.Author = author ?? ChangeAuthor.Account(actor.Login, actor.DisplayName);
        return (productionRepo, planning);
    }
}

// What the board shows: a day (postes cut in hours), a week (days cut in
// postes) or a month (days). Every period starts at 06:00, with the Matin.
public enum PlanRange
{
    Day,
    Week,
    Month,
}

public enum PlanBlockKind
{
    Order,
    Maintenance,
    MaintenanceDone,
    MaintenanceLate,
    Panne,
    PlannedStop,
}

// What a drag on a planned slot does: move it, or move its start / end edge.
public enum PlanDrag
{
    Move,
    Start,
    End,
}

public sealed record EventForm(SlotKind Kind, string MachineId, string Task, DateTime? StartDate, string StartTime,
    DateTime? EndDate, string EndTime, string Technician = "");

public sealed record PlanForm(string MachineId, string Task, MaintenanceKind Kind, string Interval, string Duration, DateTime? LastDone);

public sealed class PlanData
{
    public DateTime Now { get; init; }
    public PlanRange Range { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string? ProductionError { get; init; }
    public List<Machine> Machines { get; init; } = new();
    public List<Machine> Shown { get; init; } = new();
    public List<ProductionOrder> Orders { get; init; } = new();
    public List<MaintenanceEvent> Events { get; init; } = new();
    public List<MaintenancePlan> Plans { get; init; } = new();
    public List<MaintenanceEvent> OpenEvents { get; init; } = new();
    public List<StopQualification> Pannes { get; init; } = new();
    public Dictionary<int, double> Produced { get; init; } = new();
    public Dictionary<string, MachineStatusSnapshot?> States { get; init; } = new();
    public Dictionary<int, double> BagsSince { get; init; } = new();

    public double Hours => (End - Start).TotalHours;
    public double Frac(DateTime t) => Math.Clamp((t - Start).TotalHours / Hours, 0, 1);
    public string NameOf(string machineId) => Machines.FirstOrDefault(m => m.MachineId == machineId)?.MachineName ?? machineId;
}

// One block on a line, placed by fractions of the period shown.
public sealed record PlanBlock(string Key, PlanBlockKind Kind, double StartFrac, double EndFrac, string Label, string Sub, string Tooltip,
    bool ShowLabel, string Icon, bool HasProgress, double Progress, double ProducedBags, object Source)
{
    public bool Draggable => Source is MaintenanceEvent { Status: PlanStatus.Planned };
}

public sealed record PlanRow(string MachineId, string MachineName, string Sector, string StateText, string StateCss, List<PlanBlock> Blocks);
public sealed record PlanSector(string Name, string Key, List<PlanRow> Rows);
public sealed record PlanColumn(string Name, IReadOnlyList<(string Label, bool First)> Ticks, bool IsToday);
public sealed record PlanAlert(string Machine, string Text, string Css);
public sealed record PlanRule(MaintenancePlan Plan, string MachineName, string Rule, string StatusText, string StatusCss, bool HasGauge, double Gauge,
    DateTime Due, bool HasOpenSlot);
public sealed record PlanKpi(string Value, string Sub);
public sealed record PlanBoardView(string RangeLabel, string PeriodSuffix, double NowFrac, List<PlanColumn> Columns, List<PlanSector> Sectors,
    PlanKpi Stops, PlanKpi Maintenances, PlanKpi Pannes, List<PlanRule> Rules, List<PlanAlert> Alerts);
public sealed record PlanDetail(string Kind, string KindCss, string Title, string Sub, string Status, string StatusCss, List<(string Key, string Value)> Rows,
    bool HasProgress, double Progress, double Expected, string ProgressText, string Note, string NoteCss, bool CanEdit, bool CanClose, string CloseText, bool CanDelete);

// The board computed from what was read: pure, so the page recomputes it
// after each change without reading the databases again.
public static class PlanningBoard
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] PosteNames = { "Matin", "Après-midi", "Nuit" };

    // A day follows the postes (the Nuit after midnight still belongs to the
    // day before); a week or a month follows the calendar date.
    public static DateTime StartOf(DateTime t, PlanRange range)
    {
        var day = range == PlanRange.Day ? Postes.Of(t).ShiftDate : t.Date;
        return range switch
        {
            PlanRange.Day => day.AddHours(6),
            PlanRange.Month => new DateTime(day.Year, day.Month, 1).AddHours(6),
            _ => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).AddHours(6),
        };
    }

    public static DateTime EndOf(DateTime start, PlanRange range) => range switch
    {
        PlanRange.Day => start.AddDays(1),
        PlanRange.Month => start.AddMonths(1),
        _ => start.AddDays(7),
    };

    public static DateTime Step(DateTime start, PlanRange range, int count) => range switch
    {
        PlanRange.Day => start.AddDays(count),
        PlanRange.Month => start.AddMonths(count),
        _ => start.AddDays(7 * count),
    };

    // Another range: stays on now if it was shown, else on the start of what was shown.
    public static DateTime Rescope(DateTime start, PlanRange from, PlanRange to, DateTime now)
        => StartOf(now >= start && now < EndOf(start, from) ? now : start, to);

    public static PlanBoardView Build(PlanData d)
    {
        var now = d.Now;
        var week = ISOWeek.GetWeekOfYear(d.Start);
        var (label, suffix) = d.Range switch
        {
            PlanRange.Day => (Capitalize(d.Start.ToString("dddd d MMMM", French)) + $" · S{week}", "du jour"),
            PlanRange.Month => (Capitalize(d.Start.ToString("MMMM yyyy", French)), "du mois"),
            _ => ($"Semaine {week} · {d.Start.ToString("d MMM", French)} → {d.End.AddDays(-1).ToString("d MMM", French)}", "de la semaine"),
        };

        var sectors = new List<PlanSector>();
        foreach (var sector in d.Shown.GroupBy(m => m.Sector))
        {
            var rows = new List<PlanRow>();
            foreach (var m in sector)
            {
                var active = d.Events.Where(e => e.MachineId == m.MachineId && e.IsActiveAt(now)).ToList();
                var st = d.States.GetValueOrDefault(m.MachineId);
                var (stateText, stateCss) = active.Any(e => e.IsStop) ? ("Arrêt planifié", "stop")
                    : active.Count > 0 ? ("En maintenance", "maint")
                    : st == null || !st.IsConnected ? ("Pas de données", "nodata")
                    : st.BeltRunning ? ("En production", "run")
                    : ("À l'arrêt", "down");
                var blocks = new List<PlanBlock>();
                foreach (var o in d.Orders.Where(o => o.MachineId == m.MachineId))
                {
                    var hours = (Clamp(d, o.PlannedEnd) - Clamp(d, o.PlannedStart)).TotalHours;
                    var done = d.Produced.GetValueOrDefault(o.Id);
                    var progress = o.QuantityBags > 0 ? Math.Min(1, done / o.QuantityBags) : 0;
                    if (o.Status == PlanStatus.Done)
                        progress = Math.Max(progress, 1);
                    blocks.Add(new PlanBlock("O" + o.Id, PlanBlockKind.Order, d.Frac(o.PlannedStart), d.Frac(o.PlannedEnd), o.OrderNo, o.Product,
                        $"{o.OrderNo} · {o.Product} · {When(o.PlannedStart)} → {When(o.PlannedEnd)}", hours / d.Hours >= 0.06, "",
                        o.PlannedStart < now || o.Status == PlanStatus.Done, progress, done, o));
                }
                // The colour says what a slot is; short ones keep a minimum width and show an icon.
                foreach (var e in d.Events.Where(e => e.MachineId == m.MachineId && e.IsStop))
                {
                    var (a, b) = SlotFracs(d, e);
                    blocks.Add(new PlanBlock("E" + e.Id, PlanBlockKind.PlannedStop, a, b, e.Task, ShortSpan(e.PlannedStart, e.PlannedEnd),
                        $"Arrêt planifié · {e.Task} · {Span(e.PlannedStart, e.PlannedEnd)} · {Duration(e.DurationMinutes)}", b - a >= 0.036, "⏸",
                        false, 0, 0, e));
                }
                // Done ones first, so one still to do is drawn on top and can be clicked.
                foreach (var e in d.Events.Where(e => e.MachineId == m.MachineId && !e.IsStop).OrderBy(e => e.Status == PlanStatus.Done ? 0 : 1))
                {
                    var kind = e.Status == PlanStatus.Done ? PlanBlockKind.MaintenanceDone
                        : e.PlannedEnd < now ? PlanBlockKind.MaintenanceLate : PlanBlockKind.Maintenance;
                    var late = kind == PlanBlockKind.MaintenanceLate;
                    var (a, b) = SlotFracs(d, e);
                    blocks.Add(new PlanBlock("E" + e.Id, kind, a, b, late ? "Non validée" : e.Task, late ? e.Task : ShortSpan(e.PlannedStart, e.ActualEnd),
                        $"Maintenance{(late ? " non validée" : "")} · {e.Task} · {Span(e.PlannedStart, e.ActualEnd)} · {Duration((e.ActualEnd - e.PlannedStart).TotalMinutes)}",
                        b - a >= 0.036, late ? "⚠" : "🔧", false, 0, 0, e));
                }
                foreach (var q in d.Pannes.Where(q => q.MachineId == m.MachineId))
                    blocks.Add(new PlanBlock("Q" + q.Id, PlanBlockKind.Panne, d.Frac(q.StopStart), Math.Max(d.Frac(q.StopEnd), d.Frac(q.StopStart) + 0.004), "", "",
                        $"Panne · {StopCauses.Label(q.CauseCode)}{(q.Component == null ? "" : " · " + q.Component)} · {Duration(q.Minutes)}", false, "", false, 0, 0, q));
                rows.Add(new PlanRow(m.MachineId, m.MachineName, m.Sector, stateText, stateCss, blocks));
            }
            sectors.Add(new PlanSector(Machine.SectorLabel(sector.Key), SectorCss(sector.Key), rows));
        }

        // KPIs
        var running = d.Orders.Where(o => o.Status == PlanStatus.Planned && o.PlannedStart <= now && o.PlannedEnd > now).ToList();
        var lateOrders = running.Where(o => Behind(o, d.Produced.GetValueOrDefault(o.Id), now) > 0.25).ToList();
        var stops = d.Events.Where(e => e.IsStop).ToList();
        var maintenances = d.Events.Where(e => !e.IsStop).ToList();
        var stopMinutes = stops.Sum(e => (Clamp(d, e.PlannedEnd) - Clamp(d, e.PlannedStart)).TotalMinutes);
        var stopsNow = stops.Count(e => e.IsActiveAt(now));
        var overdue = d.Plans.Select(p => (p, s: State(p, now, null))).Where(x => x.s.Overdue && !d.OpenEvents.Any(e => e.PlanId == x.p.Id)).ToList();

        // Preventive rules, the closest to due first.
        var rules = new List<PlanRule>();
        foreach (var p in d.Plans.OrderBy(p => -State(p, now, d.BagsSince.GetValueOrDefault(p.Id)).Ratio))
        {
            var s = State(p, now, d.BagsSince.GetValueOrDefault(p.Id));
            var open = d.OpenEvents.Where(e => e.PlanId == p.Id).OrderBy(e => e.PlannedStart).FirstOrDefault();
            var (text, css) = open != null ? ($"Planifiée · {When(open.PlannedStart)}", "planned")
                : s.Overdue ? (s.DueText, "bad") : s.Soon ? (s.DueText, "warn") : (s.DueText, "ok");
            rules.Add(new PlanRule(p, d.NameOf(p.MachineId), Rule(p) + s.Detail, text, css, p.LastDoneAt != null, Math.Clamp(s.Ratio, 0, 1), s.Due, open != null));
        }

        // Alerts
        var alerts = new List<PlanAlert>();
        foreach (var (p, s) in overdue)
            alerts.Add(new PlanAlert(d.NameOf(p.MachineId), $"Préventif « {p.Task} » : {s.DueText.ToLowerInvariant()}, pas encore planifié.", "bad"));
        foreach (var o in lateOrders)
            alerts.Add(new PlanAlert(d.NameOf(o.MachineId), $"{o.OrderNo} en retard de {Duration(Behind(o, d.Produced.GetValueOrDefault(o.Id), now) * 60)} au rythme actuel.", "orange"));
        foreach (var e in maintenances.Where(e => e.Status == PlanStatus.Planned && e.PlannedEnd < now))
            alerts.Add(new PlanAlert(d.NameOf(e.MachineId), $"Maintenance « {e.Task} » du {When(e.PlannedStart)} non validée.", "orange"));
        foreach (var e in d.Events.Where(e => e.Status == PlanStatus.Planned))
            foreach (var o in d.Orders.Where(o => o.MachineId == e.MachineId && o.PlannedStart < e.PlannedEnd && o.PlannedEnd > e.PlannedStart))
                alerts.Add(new PlanAlert(d.NameOf(e.MachineId), $"« {e.Task} » tombe pendant {o.OrderNo} ({When(e.PlannedStart)}).", "warn"));
        var planned = d.Orders.Where(o => o.Status == PlanStatus.Planned).OrderBy(o => o.PlannedStart).ToList();
        for (var i = 0; i < planned.Count; i++)
            foreach (var other in planned.Skip(i + 1).Where(x => x.MachineId == planned[i].MachineId && x.PlannedStart < planned[i].PlannedEnd))
                alerts.Add(new PlanAlert(d.NameOf(other.MachineId), $"{planned[i].OrderNo} et {other.OrderNo} se chevauchent ({When(other.PlannedStart)}).", "orange"));

        return new PlanBoardView(label, suffix, (now - d.Start).TotalHours / d.Hours, Columns(d, now), sectors,
            new PlanKpi(stops.Count.ToString(CultureInfo.InvariantCulture), $"{Duration(stopMinutes)} d'arrêt prévu" + (stopsNow > 0 ? $" · {stopsNow} en cours" : "")),
            new PlanKpi(maintenances.Count.ToString(CultureInfo.InvariantCulture),
                overdue.Count == 0 ? "aucun préventif en retard" : $"{overdue.Count} préventif{(overdue.Count > 1 ? "s" : "")} en retard"),
            new PlanKpi(d.Pannes.Count.ToString(CultureInfo.InvariantCulture), $"{Duration(d.Pannes.Sum(q => q.Minutes))} d'arrêt"),
            rules, alerts);
    }

    // Header columns: the 3 postes cut in hours for a day, the days cut in
    // postes for a week, the days for a month.
    private static List<PlanColumn> Columns(PlanData d, DateTime now)
    {
        var list = new List<PlanColumn>();
        if (d.Range == PlanRange.Day)
        {
            for (var p = 0; p < 3; p++)
            {
                var start = d.Start.AddHours(8 * p);
                list.Add(new PlanColumn($"{PosteNames[p]} · {start:HH} h – {start.AddHours(8):HH} h",
                    Enumerable.Range(0, 8).Select(h => (start.AddHours(h).ToString("HH"), h == 0)).ToList(), now >= start && now < start.AddHours(8)));
            }
            return list;
        }
        var postes = new[] { ("M", true), ("AM", false), ("N", false) };
        for (var day = d.Start.Date; day < d.End.Date; day = day.AddDays(1))
            list.Add(d.Range == PlanRange.Week
                ? new PlanColumn(Day(day), postes, day == now.Date)
                : new PlanColumn(day.Day.ToString(CultureInfo.InvariantCulture),
                    new[] { (French.DateTimeFormat.GetShortestDayName(day.DayOfWeek), true) }, day == now.Date));
        return list;
    }

    public static PlanDetail Detail(PlanData d, PlanBlock block, bool canValidate)
    {
        var now = d.Now;
        var rows = new List<(string, string)>();
        switch (block.Source)
        {
            case ProductionOrder o:
            {
                var running = o.Status == PlanStatus.Planned && o.PlannedStart <= now;
                var (status, statusCss) = o.Status == PlanStatus.Done ? ("Terminé", "grey") : running ? ("En cours", "ok") : ("Planifié", "planned");
                rows.Add(("Ligne", d.NameOf(o.MachineId)));
                rows.Add(("Début prévu", When(o.PlannedStart)));
                rows.Add(("Fin prévue", When(o.PlannedEnd)));
                rows.Add(("Quantité", Bags(o.QuantityBags) + " sacs"));
                if (o.Comment is { Length: > 0 })
                    rows.Add(("Commentaire", o.Comment));
                var hasProgress = o.PlannedStart < now || o.Status == PlanStatus.Done;
                var expected = Math.Clamp((now - o.PlannedStart).TotalHours / Math.Max(0.01, (o.PlannedEnd - o.PlannedStart).TotalHours), 0, 1);
                var (note, noteCss) = ("", "");
                if (running && block.ProducedBags > 0)
                {
                    var behind = Behind(o, block.ProducedBags, now);
                    var elapsed = (now - o.PlannedStart).TotalHours;
                    rows.Add(("Fin estimée", When(now.AddHours(elapsed * Math.Max(0, o.QuantityBags - block.ProducedBags) / block.ProducedBags))));
                    (note, noteCss) = behind > 0.25
                        ? ($"Retard estimé de {Duration(behind * 60)} au rythme actuel : l'ordre suivant sur cette ligne sera décalé.", "orange")
                        : ("Dans les temps au rythme actuel.", "ok");
                }
                else if (running && (now - o.PlannedStart).TotalHours > 1)
                    (note, noteCss) = ("Aucun sac compté depuis le début prévu : l'ordre a-t-il démarré ?", "orange");
                return new PlanDetail("Ordre de fabrication", "planned", o.OrderNo, o.Product + (o.TargetWeightGrams > 0 ? $" · {o.TargetWeightGrams:0.##} g" : ""),
                    status, statusCss, rows, hasProgress, block.Progress, expected,
                    $"{Bags(block.ProducedBags)} / {Bags(o.QuantityBags)} sacs · trait = attendu maintenant ({expected * 100:0} %)",
                    note, noteCss, false, o.Status == PlanStatus.Planned, "Terminer", o.Status == PlanStatus.Planned);
            }
            case MaintenanceEvent { IsStop: true } e:
            {
                var (status, statusCss) = e.Status == PlanStatus.Done || e.PlannedEnd <= now ? ("Terminé", "grey")
                    : e.PlannedStart <= now ? ("En cours", "warn") : ("Planifié", "planned");
                rows.Add(("Début", When(e.PlannedStart)));
                rows.Add(("Fin", When(e.PlannedEnd)));
                rows.Add(("Durée", Duration(e.DurationMinutes)));
                if (e.DoneAt is { } endedAt)
                    rows.Add(("Terminé le", When(endedAt)));
                return new PlanDetail("Arrêt planifié", "stop", e.Task, d.NameOf(e.MachineId), status, statusCss, rows, false, 0, 0, "",
                    "Hors TRS : ce temps n'est pas compté dans le temps requis de la ligne.", "grey",
                    e.Status == PlanStatus.Planned && e.PlannedEnd > now, e.IsActiveAt(now), "Terminer", e.Status == PlanStatus.Planned);
            }
            case MaintenanceEvent e:
            {
                var (status, statusCss) = e.Status == PlanStatus.Done ? ("Réalisée", "ok")
                    : e.PlannedEnd < now ? ("Non validée", "bad")
                    : e.PlannedStart <= now ? ("En cours", "warn") : ("Planifiée", "planned");
                rows.Add(("Début prévu", When(e.PlannedStart)));
                rows.Add(("Fin prévue", When(e.PlannedEnd)));
                rows.Add(("Durée", Duration(e.DurationMinutes)));
                if (d.Plans.FirstOrDefault(p => p.Id == e.PlanId) is { } plan)
                    rows.Add(("Règle", Rule(plan)));
                if (e.Technician is { Length: > 0 })
                    rows.Add(("Intervenant", e.Technician));
                if (e.DoneAt is { } doneAt)
                    rows.Add(("Réalisée le", When(doneAt)));
                var (note, noteCss) = ("", "");
                if (e.ActualEnd < e.PlannedEnd)
                {
                    rows.Add(("Fin réelle", When(e.ActualEnd)));
                    (note, noteCss) = ("Validée avant la fin prévue : le reste du créneau compte à nouveau dans le TRS.", "ok");
                }
                if (e.Status == PlanStatus.Planned && e.PlannedEnd < now)
                    (note, noteCss) = ("Le créneau est passé : validez-la si elle a été faite, sinon replanifiez-la.", "bad");
                return new PlanDetail("Maintenance", "maint", e.Task, d.NameOf(e.MachineId), status, statusCss, rows, false, 0, 0, "", note, noteCss,
                    e.Status == PlanStatus.Planned, e.Status == PlanStatus.Planned && canValidate, e.IsActiveAt(now) ? "Terminer" : "Valider",
                    e.Status == PlanStatus.Planned);
            }
            case StopQualification q:
                rows.Add(("Début", When(q.StopStart)));
                rows.Add(("Fin", When(q.StopEnd)));
                rows.Add(("Durée", Duration(q.Minutes)));
                rows.Add(("Poste", $"{Spiga.Core.Plant.Shifts.Label(q.ShiftId)} · {q.Team}"));
                if (q.Technician is { Length: > 0 })
                    rows.Add(("Intervenant", q.Technician));
                if (q.Comment is { Length: > 0 })
                    rows.Add(("Commentaire", q.Comment));
                return new PlanDetail("Panne", "bad", q.Component ?? StopCauses.Label(q.CauseCode), $"{d.NameOf(q.MachineId)} · {StopCauses.Label(q.CauseCode)}",
                    "Qualifiée", "ok", rows, false, 0, 0, "", "", "", false, false, "", false);
        }
        throw new ArgumentException("Bloc inconnu", nameof(block));
    }

    // ---- slots picked and moved on the board ----

    // In hours, what a drag snaps to and what a single click picks:
    // 15 min / 1 h on a day, a poste / a poste on a week, a poste / a day on a month.
    public static (double Snap, double Click) SlotUnits(PlanRange range) => range switch
    {
        PlanRange.Day => (0.25, 1),
        PlanRange.Month => (8, 24),
        _ => (8, 8),
    };

    // A click (a == b) or a drag from a to b on an empty part of a line (fractions of the period).
    public static (DateTime Start, DateTime End) PickSlot(DateTime start, PlanRange range, double a, double b)
    {
        var end = EndOf(start, range);
        var hours = (end - start).TotalHours;
        var (snap, click) = SlotUnits(range);
        DateTime At(double frac, double unit, bool up)
        {
            var steps = Math.Clamp(frac, 0, 1) * hours / unit;
            return start.AddHours((up ? Math.Ceiling(steps) : Math.Floor(steps)) * unit);
        }
        if (Math.Abs(a - b) * hours < snap / 2)
        {
            var s = At(Math.Clamp(a, 0, 0.9999), click, false);
            return (s, s.AddHours(click) > end ? end : s.AddHours(click));
        }
        var (lo, hi) = (Math.Min(a, b), Math.Max(a, b));
        var from = At(lo, snap, false);
        var to = At(hi, snap, true);
        if (to <= from)
            to = from.AddHours(snap);
        return (from, to > end ? end : to);
    }

    // A planned slot dragged by deltaFrac (part of the period): moved, or its
    // start / end edge moved, snapped like a picked slot, never shorter than a step.
    public static (DateTime Start, DateTime End) Moved(MaintenanceEvent e, PlanRange range, DateTime rangeStart, PlanDrag mode, double deltaFrac)
    {
        var hours = (EndOf(rangeStart, range) - rangeStart).TotalHours;
        var delta = TimeSpan.FromHours(deltaFrac * hours);
        var snap = SlotUnits(range).Snap;
        DateTime Snap(DateTime t) => rangeStart.AddHours(Math.Round((t - rangeStart).TotalHours / snap) * snap);
        var length = e.PlannedEnd - e.PlannedStart;
        var (start, end) = mode switch
        {
            PlanDrag.Start => (Snap(e.PlannedStart + delta), e.PlannedEnd),
            PlanDrag.End => (e.PlannedStart, Snap(e.PlannedEnd + delta)),
            _ => (Snap(e.PlannedStart + delta), Snap(e.PlannedStart + delta) + length),
        };
        if (end <= start)
            (start, end) = mode == PlanDrag.Start ? (end.AddHours(-snap), end) : (start, start.AddHours(snap));
        return (start, end);
    }

    // What already takes the slot on that line.
    public static List<string> Busy(PlanData d, string machineId, DateTime start, DateTime end)
        => d.Orders.Where(o => o.MachineId == machineId && o.Status == PlanStatus.Planned && o.PlannedStart < end && o.PlannedEnd > start)
            .Select(o => o.OrderNo)
            .Concat(d.Events.Where(e => e.MachineId == machineId && e.Status == PlanStatus.Planned && e.PlannedStart < end && e.PlannedEnd > start)
                .Select(e => $"« {e.Task} »"))
            .ToList();

    // ---- times ----

    // "6", "06:30", "14h", "14h30" → that time of the day; null if not a time.
    public static DateTime? At(DateTime? date, string time)
    {
        var parts = (time ?? "").Trim().ToLowerInvariant().Replace('h', ':').Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0], out var h) || h is < 0 or > 23)
            return null;
        var m = 0;
        if (parts.Length == 2 && parts[1].Length > 0 && (!int.TryParse(parts[1], out m) || m is < 0 or > 59))
            return null;
        return (date ?? DateTime.Today).Date.AddHours(h).AddMinutes(m);
    }

    public static (DateTime Start, DateTime End) Span(DateTime? startDate, string startTime, DateTime? endDate, string endTime)
    {
        var (start, end) = (At(startDate, startTime), At(endDate, endTime));
        if (SpanError(start, end) is { } error)
            throw new RuleViolationException(error);
        return (start!.Value, end!.Value);
    }

    public static string? SpanError(DateTime? start, DateTime? end)
        => start == null ? "Heure de début invalide (ex. 06:00, 14h30)."
            : end == null ? "Heure de fin invalide (ex. 22:00)."
            : end <= start ? "La fin doit être après le début."
            : null;

    // "Durée 8 h · poste Matin → Après-midi"
    public static string Length(DateTime start, DateTime end)
        => $"Durée {Duration((end - start).TotalMinutes)} · poste {PosteNames[Postes.Of(start).ShiftId - 1]} → {PosteNames[Postes.Of(end).ShiftId - 1]}";

    // The end of the Matin (1) / Après-midi (2) / Nuit (3) that comes after the start.
    public static DateTime PosteEnd(DateTime start, int poste)
    {
        var end = start.Date.AddHours(poste switch { 2 => 22, 3 => 6, _ => 14 });
        return end <= start ? end.AddDays(1) : end;
    }

    public static double Number(string text)
        => double.TryParse((text ?? "").Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ---- rules ----

    public sealed record RuleState(bool Overdue, bool Soon, double Ratio, string DueText, string Detail, DateTime Due);

    public static RuleState State(MaintenancePlan p, DateTime now, double? bagsSince)
    {
        if (p.LastDoneAt is not { } last)
            return new RuleState(true, false, 1, "Jamais réalisée", "", now);
        if (p.Kind == MaintenanceKind.Calendar)
        {
            var due = last.AddDays(p.IntervalDays);
            var left = (due - now).TotalDays;
            var ratio = p.IntervalDays > 0 ? (now - last).TotalDays / p.IntervalDays : 1;
            var text = left < 0 ? $"En retard de {Math.Ceiling(-left):0} j" : left < 1 ? "Aujourd'hui" : $"Dans {Math.Floor(left):0} j";
            return new RuleState(left < 0, left <= 2 || ratio >= 0.85, ratio, text, "", due);
        }
        var used = bagsSince ?? 0;
        var remaining = p.IntervalBags - used;
        var r = p.IntervalBags > 0 ? used / p.IntervalBags : 1;
        var label = remaining <= 0 ? $"Dépassée de {Bags(-remaining)} sacs" : $"Reste {Bags(remaining)} sacs";
        return new RuleState(remaining <= 0, r >= 0.85, r, label, $" · {Bags(used)} faits", now);
    }

    public static string Rule(MaintenancePlan p) => p.Kind == MaintenanceKind.Calendar
        ? $"Calendaire · tous les {p.IntervalDays} j"
        : $"Usage · tous les {Bags(p.IntervalBags)} sacs";

    // Hours an order will finish after its planned end, at the pace so far.
    private static double Behind(ProductionOrder o, double produced, DateTime now)
    {
        if (o.QuantityBags <= 0 || now <= o.PlannedStart)
            return 0;
        var elapsed = (now - o.PlannedStart).TotalHours;
        if (produced <= 0)
            return elapsed > 1 ? (o.PlannedEnd - o.PlannedStart).TotalHours : 0;
        var estimate = now.AddHours(elapsed * (o.QuantityBags - produced) / produced);
        return Math.Max(0, (estimate - o.PlannedEnd).TotalHours);
    }

    // ---- text ----

    // Outside the day view a short slot keeps a minimum width (about half a day on a week).
    private static (double Start, double End) SlotFracs(PlanData d, MaintenanceEvent e)
    {
        var start = d.Frac(e.PlannedStart);
        var min = d.Range == PlanRange.Day ? 0 : 0.02;
        return (start, Math.Min(1, Math.Max(d.Frac(e.ActualEnd), start + min)));
    }

    private static DateTime Clamp(PlanData d, DateTime t) => t < d.Start ? d.Start : t > d.End ? d.End : t;

    public static string SectorCss(string sector) => sector switch
    {
        "Couscous" => "sec-cc",
        "Pate courte" => "sec-pc",
        "Pate longue" => "sec-pl",
        _ => "sec-other",
    };

    // "7 min", "1 h 05", and from a day on "2 j", "1 j 6 h", "2 j 4 h 30"
    public static string Duration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        if (total < 24 * 60)
            return ProductionStop.FormatDuration(total);
        var (days, hours, mins) = (total / 1440, total % 1440 / 60, total % 60);
        return $"{days} j" + (hours > 0 || mins > 0 ? $" {hours} h" : "") + (mins > 0 ? $" {mins:00}" : "");
    }

    // "Jeu 29"
    public static string Day(DateTime date)
    {
        var name = French.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek).TrimEnd('.');
        return char.ToUpper(name[0]) + name[1..] + " " + date.Day;
    }

    public static string Bags(double count) => count.ToString("N0", French);

    public static string When(DateTime t) => Day(t) + " · " + t.ToString("HH:mm");

    // "Jeu 29 · 06:00 → 14:00", or "Jeu 29 · 06:00 → Sam 31 · 06:00"
    public static string Span(DateTime start, DateTime end)
        => start.Date == end.Date ? $"{When(start)} → {end:HH:mm}" : $"{When(start)} → {When(end)}";

    // "08:00 → 19:00", or "Mar 3 08:00 → Mer 4 19:00" over several days.
    public static string ShortSpan(DateTime start, DateTime end)
        => start.Date == end.Date || end == end.Date && end.AddDays(-1).Date == start.Date
            ? $"{start:HH:mm} → {end:HH:mm}"
            : $"{Day(start)} {start:HH:mm} → {Day(end)} {end:HH:mm}";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];
}
