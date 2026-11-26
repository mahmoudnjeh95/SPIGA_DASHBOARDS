using Spiga.Core.Security;
using Spiga.Data;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Slides;

namespace Spiga.UI.Services;

// Production > Tableau de bord détaillé (web): the slides of the dashboard
// (by production line: its overview and its machines, then a synthesis per
// line and the plant view, see WebDeck) in the order and with the
// durations of Paramètres > Affichage, filtered and rotated as on the kiosk. Same figures as the kiosk's slide view models
// (MachineDaySlideViewModel...), from the shared period, filter and KPI code;
// read through repositories limited to the account's machines; a team-bound
// account counts its team's postes only.
public sealed partial class DashboardDeckService(
    IDbConnectionFactory production, AppDbOptions appDb, AppSettings settings, TeamScheduleService teams, TimeProvider clock)
{
    public static readonly string Permission = NativePages.Permission(NativePage.Dashboard);

    public DateTime Now => clock.GetLocalNow().DateTime;
    public AppSettings Settings => settings;
    public IReadOnlyList<string> Teams => teams.TeamNames;

    // Target weights of the cadence table (filter "Poids").
    public IReadOnlyList<double> Weights
        => settings.NominalRates.Where(r => r.WeightGrams > 0).Select(r => r.WeightGrams).Distinct().Order().ToList();

    // The machines of the rotation the account may see: active, not hidden
    // in Paramètres, line by line (Paramètres > Lignes), then the machines
    // in no line, sector then id.
    public async Task<List<Machine>> MachinesAsync(Actor actor)
    {
        var repository = Repository(actor);
        var order = Lines.SelectMany(l => l.MachineIds).Select((id, i) => (id, i))
            .GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        return (await repository.GetMachinesAsync())
            .Where(m => m.IsActive && !settings.HiddenMachineIds.Contains(m.MachineId))
            .OrderBy(m => order.TryGetValue(m.MachineId, out var i) ? i : int.MaxValue)
            .ThenBy(m => Array.IndexOf(SlideDeck.SectorOrder, m.Sector) is var s && s < 0 ? int.MaxValue : s).ThenBy(m => m.MachineId)
            .ToList();
    }

    // The deck for these filters (WebDeck: by line): its own Paramètres
    // order, slides switched off left out. Filter.MachineIds = the machines
    // kept by the sector / line / machine filters (empty = all).
    public List<SlideDescriptor> Deck(IReadOnlyList<Machine> machines, DashboardFilter filter)
    {
        var shown = filter.MachineIds.Count == 0 ? machines : machines.Where(m => filter.MachineIds.Contains(m.MachineId)).ToList();
        var off = settings.WebSlides.Where(s => !s.Enabled).Select(s => s.Key).ToHashSet();
        return WebDeck.Arrange(WebDeck.Default(machines, shown, Lines), settings.WebSlides)
            .Where(s => !off.Contains(s.Key))
            .ToList();
    }

    public double SecondsFor(SlideDescriptor slide) => Math.Max(5, settings.WebSlideSecondsFor(slide.Key));

    // A team-bound account: its team, whatever was chosen.
    public static DashboardFilter ForActor(Actor actor, DashboardFilter filter) => actor.Team is { } team ? filter with { Team = team } : filter;

    // ---- machine slide ----

    // Aujourd'hui / Poste précédent: the poste in full (KPIs, charts) and the
    // postes before it that day; any other period, or no poste matching the
    // filters that day: the period layout.
    public async Task<MachineSlide> MachineAsync(Actor actor, Machine machine, DashboardPeriod period, DateTime? customFrom, DateTime? customTo,
        DashboardFilter filter, CancellationToken cancel = default)
    {
        filter = ForActor(actor, filter);
        var repository = Repository(actor);
        var now = Now;
        var status = await repository.GetLatestStatusAsync(machine.MachineId);
        var ongoing = (await OngoingStopService.GetAsync(repository, settings)).GetValueOrDefault(machine.MachineId);
        var slot = (await repository.GetActiveSlotKindsAsync()).TryGetValue(machine.MachineId, out var s) ? s : (SlotKind?)null;
        cancel.ThrowIfCancellationRequested();
        var header = new MachineHeader(machine.MachineId, machine.MachineName, machine.Sector, status?.IsConnected ?? false,
            (status?.BeltRunning ?? false) && ongoing == null, slot, ongoing, ongoing == null ? OngoingStopService.RecentRecovery(machine.MachineId) : null);
        var trsBand = settings.TrsBandFor(machine.MachineId);
        var weightBand = settings.WeightBandFor(machine.MachineId);

        if (period is DashboardPeriod.Day or DashboardPeriod.PreviousShift)
        {
            var previous = period == DashboardPeriod.PreviousShift;
            var currentShift = KpiCalculator.CurrentShiftId(now);
            var shiftDate = TeamScheduleService.GetShiftDate(now, currentShift);
            var (date, shiftId) = previous
                ? currentShift > 1 ? (shiftDate, currentShift - 1) : (shiftDate.AddDays(-1), 3)
                : (shiftDate, currentShift);
            var postes = Enumerable.Range(1, shiftId)
                .Where(id => filter.ShiftId is null || filter.ShiftId == id)
                .Where(id => filter.Team is null || teams.GetTeamForShift(date, id) == filter.Team)
                .ToList();
            if (postes.Count > 0)
            {
                var allLots = await repository.GetLotsAsync(machine.MachineId, date, date.AddDays(1).AddHours(6));
                var lots = filter.Apply(allLots, teams);
                var mainId = postes[^1];
                var mainStart = date.AddHours(KpiCalculator.ShiftStartHour(mainId));
                var earlierEnd = now - KpiTrend.TrendWindow;
                var earlier = !previous && mainId == currentShift && earlierEnd > mainStart
                    ? filter.Apply(await repository.GetLotsAsync(machine.MachineId, date, earlierEnd), teams)
                    : null;
                // Weight per lot over its real time (2 h past the poste end:
                // lots that complete its last 1000-bag group).
                var spans = (await repository.GetLotSpansAsync(machine.MachineId, mainStart, mainStart.AddHours(10)))
                    ?.Where(l => filter.MatchesWeight(l.Target)).ToList();
                return new MachineSlide(header, previous ? "Poste précédent" : "", SlideText.Production(status?.CurrentProduction, allLots),
                    filter.Describe(),
                    Poste(mainId, date, true, !previous && mainId == currentShift, lots, now, trsBand, weightBand, earlier, spans),
                    postes.Take(postes.Count - 1).Select(id => Poste(id, date, false, false, lots, now, trsBand, weightBand, null)).ToList(),
                    null);
            }
            // The whole production day: 06:00 to 06:00 the next morning.
            return await PeriodAsync(repository, header, machine, date.AddHours(PeriodRanges.DayStartHour),
                date.AddDays(1).AddHours(PeriodRanges.DayStartHour), previous ? "Poste précédent" : "Aujourd'hui",
                filter, status, trsBand, weightBand, now);
        }

        var (from, to) = PeriodRanges.Get(period, now, customFrom, customTo);
        var label = PeriodRanges.Label(period, from, to);
        // Nothing before DataStartDate is read: start there, and say so.
        if (from < settings.DataStartDate && to > settings.DataStartDate)
        {
            from = settings.DataStartDate.Date;
            label += $" - données depuis le {from:dd/MM/yyyy}";
        }
        return await PeriodAsync(repository, header, machine, from, to, label, filter, status, trsBand, weightBand, now);
    }

    private async Task<MachineSlide> PeriodAsync(ProductionRepository repository, MachineHeader header, Machine machine, DateTime from, DateTime to,
        string label, DashboardFilter filter, MachineStatusSnapshot? status, TrsBand trsBand, WeightBand weightBand, DateTime now)
    {
        var allLots = await repository.GetLotsAsync(machine.MachineId, from, to);
        var lots = filter.Apply(allLots, teams);
        var kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots), trsBand, weightBand);
        // Teams, best TRS first; teams that did not produce go last.
        var teamRows = lots.GroupBy(teams.TeamOf)
            .Select(g =>
            {
                var k = KpiCalculator.Summarize(g.ToList(), KpiCalculator.AvailabilityPercent(g.ToList()), trsBand, weightBand);
                return new TeamRow(g.Key, k, KpiCalculator.TrsSeverity(k.TrsPercent, trsBand),
                    KpiCalculator.WeightDeviationSeverity(k.WeightDeviationPercent, weightBand));
            })
            .OrderByDescending(t => t.Kpis.TrsPercent ?? -1).ThenByDescending(t => t.Kpis.TotalBags)
            .ToList();
        var grain = PeriodRanges.GrainFor(from, to);
        var steps = DeckCharts.Steps(PeriodRanges.Group(lots, from, to, grain, now));
        var figures = new PosteFigures(label, "", "", false, kpis, trsBand.Target, KpiCalculator.TrsSeverity(kpis.TrsPercent, trsBand),
            KpiCalculator.WeightDeviationSeverity(kpis.WeightDeviationPercent, weightBand), null, null, null);
        return new MachineSlide(header, label, SlideText.Production(status?.CurrentProduction, allLots), filter.Describe(), figures,
            Array.Empty<PosteFigures>(),
            new PeriodCharts("par " + PeriodRanges.GrainName(grain), teamRows, DeckCharts.Trs(steps, trsBand.Target), DeckCharts.Speed(steps),
                DeckCharts.ProductionByWeight(steps), DeckCharts.Deviation(steps)));
    }

    // One poste of one shift day (ShiftSummaryViewModel): its KPIs, and for
    // the main one its hourly charts and the up / down arrows of the poste in progress.
    private PosteFigures Poste(int shiftId, DateTime date, bool withCharts, bool running, IReadOnlyCollection<ProductionBucket> dayLots, DateTime now,
        TrsBand trsBand, WeightBand weightBand, IReadOnlyCollection<ProductionBucket>? earlierLots, IReadOnlyList<LotSpan>? spans = null)
    {
        var lots = dayLots.Where(l => l.ShiftId == shiftId && TeamScheduleService.GetShiftDate(l.StartTime, shiftId) == date).ToList();
        var kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots), trsBand, weightBand);
        var team = teams.GetTeamForShift(date, shiftId);
        PosteCharts? charts = null;
        if (withCharts)
        {
            var start = date.AddHours(KpiCalculator.ShiftStartHour(shiftId));
            var byHour = lots.ToLookup(l => l.StartTime);
            var steps = Enumerable.Range(0, 8).Select(i => start.AddHours(i)).Where(h => h <= now)
                .Select(h => new DeckCharts.Step(h.ToString("HH"), byHour[h].ToList())).ToList();
            var end = start.AddHours(8);
            var lotWeight = spans is { Count: > 0 } ? DeckCharts.WeightLots(spans, start, now < end ? now : end, live: now < end) : DeckChart.Empty;
            charts = new PosteCharts(DeckCharts.OnOff(steps), DeckCharts.Speed(steps), lotWeight.HasData ? lotWeight : DeckCharts.Weight(steps));
        }
        KpiTrend? trs = null, speed = null, weight = null;
        if (withCharts && running && earlierLots != null)
        {
            var before = earlierLots.Where(l => l.ShiftId == shiftId && TeamScheduleService.GetShiftDate(l.StartTime, shiftId) == date).ToList();
            var earlier = KpiCalculator.Summarize(before, KpiCalculator.AvailabilityPercent(before));
            trs = KpiTrend.Of(kpis.TrsPercent, earlier.TrsPercent, " pt");
            speed = KpiTrend.Of(kpis.SpeedBagsPerMinute, earlier.SpeedBagsPerMinute, "");
            var main = kpis.Weights.FirstOrDefault();
            var was = main == null ? null : earlier.Weights.FirstOrDefault(w => Math.Round(w.TargetWeight) == Math.Round(main.TargetWeight));
            if (!KpiTrend.WeightOnTarget(kpis) && main?.AvgWeight is { } a && was?.AvgWeight is { } b)
                weight = KpiTrend.Of(a, b, " g", goodDirection: Math.Sign(Math.Abs(b - main.TargetWeight) - Math.Abs(a - main.TargetWeight)));
        }
        return new PosteFigures(KpiCalculator.ShiftName(shiftId), KpiCalculator.ShiftTimeRange(shiftId),
            string.IsNullOrEmpty(team) ? TeamScheduleService.NoTeam : team, running, kpis, trsBand.Target,
            KpiCalculator.TrsSeverity(kpis.TrsPercent, trsBand), KpiCalculator.WeightDeviationSeverity(kpis.WeightDeviationPercent, weightBand),
            trs, speed, weight) { Charts = charts };
    }

    private ProductionRepository Repository(Actor actor) => ScopedData.For(actor, Permission, production, appDb, settings).Production;
}

// The machine's state now, whatever the period (photo, stop bar).
public sealed record MachineHeader(string MachineId, string Name, string Sector, bool Connected, bool Running, SlotKind? Slot,
    OngoingStop? Stop, StopRecovery? Recovery)
{
    public string SectorLabel => Machine.SectorLabel(Sector);
    public string State => Slot is not null ? "is-planned" : !Connected ? "is-off" : Running ? "is-run" : "is-stop";
    public string StateText => Slot == SlotKind.Maintenance ? "Maintenance" : Slot == SlotKind.PlannedStop ? "Arrêt planifié"
        : !Connected ? "Pas de données" : Running ? "En marche" : "À l'arrêt";
}

public sealed record PosteFigures(string Name, string Hours, string Team, bool Running, KpiSummary Kpis, double TrsTarget,
    KpiSeverity TrsSeverity, KpiSeverity WeightSeverity, KpiTrend? TrsTrend, KpiTrend? SpeedTrend, KpiTrend? WeightTrend)
{
    public PosteCharts? Charts { get; init; }
    public bool WeightOnTarget => KpiTrend.WeightOnTarget(Kpis);
}

public sealed record PosteCharts(DeckChart OnOff, DeckChart Speed, DeckChart Weight);

public sealed record TeamRow(string Team, KpiSummary Kpis, KpiSeverity TrsSeverity, KpiSeverity WeightSeverity);

public sealed record PeriodCharts(string Grain, IReadOnlyList<TeamRow> Teams, DeckChart Trs, DeckChart Speed, DeckChart Production, DeckChart Deviation);

// A machine slide: the poste layout (Main + Previous, Period null) or the period layout (Period set).
public sealed record MachineSlide(MachineHeader Header, string PeriodLabel, string ProductionLabel, string FilterLabel, PosteFigures Main,
    IReadOnlyList<PosteFigures> Previous, PeriodCharts? Period)
{
    // Its production line (badge in the title), if it is in one.
    public LineTag? Line { get; init; }
}
