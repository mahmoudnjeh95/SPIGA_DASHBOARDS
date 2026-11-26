using System.Collections.Concurrent;
using Spiga.Core.Security;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Slides;

namespace Spiga.UI.Services;

// The group slides of the web dashboard, one production line at a time
// (Paramètres > Lignes, see WebDeck): the line's overview (its figures, the
// live state of each machine, the machine x team table), its synthesis over
// the month / 3 months / year, and the plant view (every line side by
// side). A line's figures pool its machines' buckets (KpiCalculator: useful
// time over required time), so its TRS is never an average of theirs.
public sealed partial class DashboardDeckService
{
    // A year of sub-minute snapshots is heavy to aggregate again and these
    // figures barely move: the lots read are kept a while (the kiosk does
    // the same). Keyed by the machines read, so an account only ever gets
    // lots of machines it was allowed to read.
    private static readonly TimeSpan SynthesisCache = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan GroupCache = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTime LoadedAt, List<ProductionBucket> Lots)> Cache = new();

    private static async Task<List<ProductionBucket>> CachedAsync(string key, TimeSpan keep, DateTime now, Func<Task<List<ProductionBucket>>> read)
    {
        if (Cache.TryGetValue(key, out var hit) && now - hit.LoadedAt <= keep)
            return hit.Lots;
        var lots = await read();
        Cache[key] = (now, lots);
        return lots;
    }

    private (DateTime From, string Label) FromDataStart(DateTime from, DateTime to, string label)
    {
        if (from < settings.DataStartDate && to > settings.DataStartDate)
            return (settings.DataStartDate.Date, label + $" - données depuis le {settings.DataStartDate:dd/MM/yyyy}");
        return (from, label);
    }

    public IReadOnlyList<DashboardLine> Lines => settings.DashboardLines;

    public DashboardLine? Line(string name) => Lines.FirstOrDefault(l => l.Name == name);

    // The badge of a machine slide: its line, sector and colour.
    public LineTag? TagOf(string machineId)
        => WebDeck.LineOf(Lines, machineId) is { } line ? new LineTag(line.Label, Machine.SectorLabel(line.Sector), WebDeck.ColorOf(Lines, line.Name)) : null;

    // ---- Synthèse of a line ----

    public async Task<SynthesisSlide> SynthesisAsync(Actor actor, DashboardLine line, IReadOnlyList<Machine> machines, IReadOnlyList<string> planned,
        SynthesisPeriod period, DashboardFilter filter, CancellationToken cancel = default)
    {
        filter = ForActor(actor, filter);
        var repository = Repository(actor);
        var now = Now;
        var (from, to, label) = PeriodRanges.Synthesis(period, now);
        (from, label) = FromDataStart(from, to, label);
        // Day buckets are enough unless postes / teams are filtered.
        var size = filter.FiltersByPosteOrTeam ? BucketSize.Hour : BucketSize.Day;
        var ids = machines.Select(m => m.MachineId).ToList();
        var all = await CachedAsync($"syn|{period}|{from:yyyyMMdd}|{size}|{string.Join(",", ids)}", SynthesisCache, now,
            () => repository.GetLotsForMachinesAsync(ids, from, to, size));
        cancel.ThrowIfCancellationRequested();
        var lots = filter.Apply(all, teams);
        var byMachine = lots.ToLookup(l => l.MachineId);

        var lines = machines.Select(m =>
        {
            var machineLots = byMachine[m.MachineId].ToList();
            var band = settings.TrsBandFor(m.MachineId);
            var weightBand = settings.WeightBandFor(m.MachineId);
            var k = KpiCalculator.Summarize(machineLots, KpiCalculator.AvailabilityPercent(machineLots), band, weightBand);
            return new SynthesisLine(m.MachineName, k, band.Target, KpiCalculator.TrsSeverity(k.TrsPercent, band),
                KpiCalculator.WeightDeviationSeverity(k.WeightDeviationPercent, weightBand));
        }).ToList();

        var grain = period switch
        {
            SynthesisPeriod.Month => TimeGrain.Day,
            SynthesisPeriod.ThreeMonths => TimeGrain.Week,
            _ => TimeGrain.Month,
        };
        var labels = new List<string>();
        var trsLines = new List<(string, double?[])>();
        var deviationLines = new List<(string, double?[])>();
        foreach (var m in machines)
        {
            var groups = PeriodRanges.Group(byMachine[m.MachineId], from, to, grain, now);
            if (labels.Count == 0)
                labels = groups.Select(g => g.Label).ToList();
            var perStep = groups.Select(g => KpiCalculator.Summarize(g.Lots, KpiCalculator.AvailabilityPercent(g.Lots))).ToList();
            trsLines.Add((m.MachineName, perStep.Select(k => k.TrsPercent is { } v ? (double?)Math.Round(v, 1) : null).ToArray()));
            deviationLines.Add((m.MachineName, perStep.Select(k => k.WeightDeviationPercent is { } v ? (double?)Math.Round(v, 2) : null).ToArray()));
        }
        // Reference line only when every machine shares the same TRS target.
        var targets = machines.Select(m => settings.TrsTarget(m.MachineId)).Distinct().ToList();
        var groupBand = TrsBand.Average(machines.Select(m => settings.TrsBandFor(m.MachineId)).ToList());
        var kpis = KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots));

        return new SynthesisSlide(line.Label, Machine.SectorLabel(line.Sector), WebDeck.ColorOf(Lines, line.Name), period, label, filter.Describe(),
            kpis, KpiCalculator.TrsSeverity(kpis.TrsPercent, groupBand), lines, planned, "par " + PeriodRanges.GrainName(grain),
            DeckCharts.Lines(labels, trsLines, v => $"{v:0} %", targets.Count == 1 ? targets[0] : null),
            DeckCharts.Lines(labels, deviationLines, v => v.ToString("+0.00;-0.00;0", Fmt.French) + " %", 0));
    }

    // ---- line overview, plant view ----

    // The top-bar period ("Aujourd'hui" and "Poste précédent": the whole
    // day), the machines' buckets (hour ones: teams are worked out from
    // each bucket's poste), filtered.
    private async Task<(List<ProductionBucket> Lots, string Label)> GroupLotsAsync(ProductionRepository repository, IReadOnlyList<Machine> machines,
        DashboardPeriod period, DateTime? customFrom, DateTime? customTo, DashboardFilter filter, CancellationToken cancel)
    {
        var now = Now;
        var p = period == DashboardPeriod.PreviousShift ? DashboardPeriod.Day : period;
        var (from, to) = PeriodRanges.Get(p, now, customFrom, customTo);
        var label = PeriodRanges.Label(p, from, to);
        (from, label) = FromDataStart(from, to, label);
        var ids = machines.Select(m => m.MachineId).ToList();
        var all = (to - from).TotalDays <= 8
            ? await repository.GetLotsForMachinesAsync(ids, from, to)
            : await CachedAsync($"group|{from:yyyyMMddHH}|{to:yyyyMMddHH}|{string.Join(",", ids)}", GroupCache, now,
                () => repository.GetLotsForMachinesAsync(ids, from, to));
        cancel.ThrowIfCancellationRequested();
        return (filter.Apply(all, teams), label);
    }

    // Each machine's state now, whatever the period: a line with a stop in
    // progress is not running, even if a few bags moved its counter.
    private async Task<Dictionary<string, MachineHeader>> LiveAsync(ProductionRepository repository, IEnumerable<Machine> machines, CancellationToken cancel)
    {
        var slots = await repository.GetActiveSlotKindsAsync();
        var stops = await OngoingStopService.GetAsync(repository, settings);
        var headers = new Dictionary<string, MachineHeader>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in machines)
        {
            cancel.ThrowIfCancellationRequested();
            var status = await repository.GetLatestStatusAsync(m.MachineId);
            var stop = stops.GetValueOrDefault(m.MachineId);
            headers[m.MachineId] = new MachineHeader(m.MachineId, m.MachineName, m.Sector, status?.IsConnected ?? false,
                (status?.BeltRunning ?? false) && stop == null, slots.TryGetValue(m.MachineId, out var slot) ? slot : null, stop, null);
        }
        return headers;
    }

    // Every configured team, even at 0, so the columns compare like for
    // like; "Sans équipe" only when such hours produced something.
    private List<string> TeamColumns(IReadOnlyCollection<ProductionBucket> lots, DashboardFilter filter)
    {
        var columns = filter.Team != null ? new List<string> { filter.Team } : teams.TeamNames.ToList();
        if (!columns.Contains(TeamScheduleService.NoTeam) && lots.Any(l => teams.TeamOf(l) == TeamScheduleService.NoTeam && l.TotalBags > 0))
            columns.Add(TeamScheduleService.NoTeam);
        return columns;
    }

    public async Task<LineSlide> LineAsync(Actor actor, DashboardLine line, IReadOnlyList<Machine> machines, IReadOnlyList<string> planned,
        DashboardPeriod period, DateTime? customFrom, DateTime? customTo, DashboardFilter filter, CancellationToken cancel = default)
    {
        filter = ForActor(actor, filter);
        var repository = Repository(actor);
        var (lots, label) = machines.Count == 0
            ? (new List<ProductionBucket>(), PeriodRanges.Label(period == DashboardPeriod.PreviousShift ? DashboardPeriod.Day : period, Now, Now))
            : await GroupLotsAsync(repository, machines, period, customFrom, customTo, filter, cancel);
        var live = await LiveAsync(repository, machines, cancel);

        var columns = TeamColumns(lots, filter);
        var bands = machines.Select(m => settings.TrsBandFor(m.MachineId)).ToList();
        var groupBand = TrsBand.Average(bands);
        var rows = new List<PackRow>();
        var tiles = new List<LineTile>();
        foreach (var m in machines)
        {
            var machineLots = lots.Where(l => l.MachineId == m.MachineId).ToList();
            var band = settings.TrsBandFor(m.MachineId);
            var total = PackCell.Of(machineLots, band);
            rows.Add(new PackRow(m.MachineName, false, columns.Select(t => PackCell.Of(machineLots.Where(l => teams.TeamOf(l) == t), band)).ToList(), total));
            var h = live[m.MachineId];
            tiles.Add(new LineTile(m.MachineName, h.State, h.StateText, h.Running, h.Stop, total, band.Target, false));
        }
        tiles.AddRange(planned.Select(name => new LineTile(name, "is-off", "Non connectée", false, null, null, null, true)));
        var byTeam = lots.ToLookup(teams.TeamOf);
        var teamTotals = columns.Select(t => PackCell.Of(byTeam[t], groupBand)).ToList();
        var overall = PackCell.Of(lots, groupBand);
        if (machines.Count > 1)
            rows.Add(new PackRow("Total ligne", true, teamTotals, overall));

        // Target line only when every machine shares the same target.
        var targets = bands.Select(b => b.Target).Distinct().ToList();
        double? targetLine = targets.Count == 1 ? targets[0] : null;
        var names = machines.Select(m => m.MachineName).ToList();
        var teamChart = TrsBars(columns, teamTotals.Select(c => c.Kpis.TrsPercent).ToList(), targetLine);
        // Each machine's production, piled up by team.
        var machineChart = DeckCharts.Stacked(names, columns.Select((t, i) =>
            (t, (IReadOnlyList<double?>)rows.Take(machines.Count).Select(r => (double?)Math.Round(r.Cells[i].ProductionKg)).ToList())).ToList(),
            v => KpiCalculator.FormatMass(v));

        return new LineSlide(line.Label, Machine.SectorLabel(line.Sector), WebDeck.ColorOf(Lines, line.Name), label, filter.Describe(),
            overall, groupBand.Target, KpiCalculator.WeightDeviationSeverity(overall.Kpis.WeightDeviationPercent, WeightBand.Default),
            live.Values.Count(h => h.Running), live.Values.Count(h => h.Connected && !h.Running && h.Slot == null),
            tiles, columns, rows, teamChart, machineChart);
    }

    public async Task<PlantSlide> PlantAsync(Actor actor, IReadOnlyList<(DashboardLine Line, IReadOnlyList<Machine> Machines)> lines,
        DashboardPeriod period, DateTime? customFrom, DateTime? customTo, DashboardFilter filter, CancellationToken cancel = default)
    {
        filter = ForActor(actor, filter);
        var repository = Repository(actor);
        var machines = lines.SelectMany(l => l.Machines).ToList();
        var (lots, label) = await GroupLotsAsync(repository, machines, period, customFrom, customTo, filter, cancel);
        var live = await LiveAsync(repository, machines, cancel);
        var byMachine = lots.ToLookup(l => l.MachineId);

        var cards = lines.Select(x =>
        {
            var band = TrsBand.Average(x.Machines.Select(m => settings.TrsBandFor(m.MachineId)).ToList());
            var lineLots = x.Machines.SelectMany(m => byMachine[m.MachineId]).ToList();
            var states = x.Machines.Select(m => live[m.MachineId]).ToList();
            return new PlantLine(x.Line.Label, Machine.SectorLabel(x.Line.Sector), WebDeck.ColorOf(Lines, x.Line.Name), PackCell.Of(lineLots, band), band.Target,
                states.Select(h => new PlantMachineState(h.Name, h.State, h.StateText)).ToList(), x.Line.PlannedMachines.Count);
        }).ToList();
        var plantBand = TrsBand.Average(machines.Select(m => settings.TrsBandFor(m.MachineId)).ToList());
        var overall = PackCell.Of(lots, plantBand);

        var names = cards.Select(c => c.Name).ToList();
        var colors = cards.Select(c => c.Color).ToList();
        var targets = machines.Select(m => settings.TrsTarget(m.MachineId)).Distinct().ToList();
        var trsChart = DeckCharts.ColoredBars(names, cards.Select(c => c.Figures.Kpis.TrsPercent is { } v ? (double?)Math.Round(v, 1) : null).ToList(),
            colors, v => $"{v:0} %", targets.Count == 1 ? targets[0] : null);
        var productionChart = DeckCharts.ColoredBars(names, cards.Select(c => (double?)Math.Round(c.Figures.ProductionKg)).ToList(), colors,
            v => KpiCalculator.FormatMass(v));
        return new PlantSlide(label, filter.Describe(), overall, plantBand.Target,
            live.Values.Count(h => h.Running), live.Values.Count(h => h.Connected && !h.Running && h.Slot == null), cards, trsChart, productionChart);
    }

    // État des machines: every machine shown, live, grouped by line in line
    // order (a line's machines not connected yet greyed, with the whole line
    // only), then the machines in no line.
    // all = every machine of the account, shownIds = those the filters keep.
    public async Task<StatusSlide> StatusAsync(Actor actor, IReadOnlyList<Machine> all, IReadOnlyCollection<string> shownIds,
        CancellationToken cancel = default)
    {
        var shown = shownIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var machines = all.Where(m => shown.Contains(m.MachineId)).ToList();
        var allIds = all.Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repository = Repository(actor);
        var live = await LiveAsync(repository, machines, cancel);
        StatusTile Tile(Machine m)
        {
            var h = live[m.MachineId];
            return new StatusTile(m.MachineName, h.State, h.StateText, h.Running, h.Stop, false);
        }

        var byId = machines.ToDictionary(m => m.MachineId, StringComparer.OrdinalIgnoreCase);
        var groups = new List<StatusGroup>();
        foreach (var line in Lines)
        {
            var mine = line.MachineIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            // Planned machines only when the whole line is shown (a line of
            // planned machines alone: only with no filter at all).
            var whole = mine.Count > 0 ? line.MachineIds.Where(allIds.Contains).All(byId.ContainsKey) : machines.Count == all.Count;
            if (mine.Count == 0 && (!whole || line.PlannedMachines.Count == 0))
                continue;
            var tiles = mine.Select(Tile).ToList();
            if (whole)
                tiles.AddRange(line.PlannedMachines.Select(name => new StatusTile(name, "is-off", "En préparation", false, null, true)));
            groups.Add(new StatusGroup(line.Label, Machine.SectorLabel(line.Sector), WebDeck.ColorOf(Lines, line.Name), tiles));
        }
        var inLines = Lines.SelectMany(l => l.MachineIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = machines.Where(m => !inLines.Contains(m.MachineId)).ToList();
        if (others.Count > 0)
            groups.Add(new StatusGroup("Autres machines", "", "#8A93A0", others.Select(Tile).ToList()));

        var headers = live.Values.ToList();
        return new StatusSlide(groups, headers.Count(h => h.Running), headers.Count(h => h.Connected && !h.Running && h.Slot == null),
            headers.Count(h => h.Slot != null), headers.Count(h => !h.Connected && h.Slot == null));
    }

    private static DeckChart TrsBars(IReadOnlyList<string> labels, IReadOnlyList<double?> values, double? target)
        => values.Any(v => v.HasValue)
            ? DeckCharts.Bars(labels, values.Select(v => v is { } x ? (double?)Math.Round(x, 1) : null).ToList(), "TRS", v => $"{v:0} %", target)
            : DeckChart.Empty;

    // ---- stop alert, sticky note ----

    // Lines of the account stopped with no cause yet, oldest first
    // (Paramètres: alerts on, machine not hidden). Empty when alerts are off.
    public async Task<List<OngoingStop>> UnqualifiedStopsAsync(Actor actor)
    {
        if (!settings.ShowStopAlerts)
            return new List<OngoingStop>();
        var allowed = (await MachinesAsync(actor)).Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stops = await OngoingStopService.GetAsync(Repository(actor), settings);
        return stops.Values.Where(s => s.IsUnqualified && allowed.Contains(s.MachineId)).OrderBy(s => s.Start).ToList();
    }

    // The note of the screens (null: the application database can't be read;
    // the page keeps what it shows).
    public async Task<string?> NoteAsync(Actor actor)
    {
        try
        {
            return (await Repository(actor).GetActiveNoteAsync())?.Content ?? "";
        }
        catch (Exception ex)
        {
            AppLogger.Log("Note du tableau de bord illisible (base applicative) : " + ex.Message);
            return null;
        }
    }

    public static bool CanEditNote(Actor actor) => actor.CanEverywhere(Perm.NoteEcran);

    // Replaces the note on every screen; who wrote it goes to the audit trail.
    public async Task SaveNoteAsync(Actor actor, ChangeAuthor author, string text)
    {
        if (!CanEditNote(actor))
            throw new AccessDeniedException("Votre compte ne peut pas modifier la note des écrans.");
        if (text.Length > 1000)
            throw new RuleViolationException("La note est limitée à 1000 caractères.");
        await Repository(actor).SetActiveNoteAsync(text.Trim(), author);
    }
}

// État des machines: one tile per machine (Planned = not connected yet),
// grouped by line, and the counts of the header.
public sealed record StatusTile(string Name, string State, string StateText, bool Running, OngoingStop? Stop, bool Planned);

public sealed record StatusGroup(string Name, string Sector, string Color, IReadOnlyList<StatusTile> Tiles);

// Scheduled = in a planned stop or maintenance slot; Offline = no data.
public sealed record StatusSlide(IReadOnlyList<StatusGroup> Groups, int Running, int Stopped, int Scheduled, int Offline);

// The line a machine slide belongs to (badge in its title).
public sealed record LineTag(string Label, string SectorLabel, string Color);

public sealed record SynthesisLine(string Name, KpiSummary Kpis, double TrsTarget, KpiSeverity TrsSeverity, KpiSeverity WeightSeverity);

public sealed record SynthesisSlide(string Title, string Sector, string Color, SynthesisPeriod Period, string PeriodLabel, string FilterLabel,
    KpiSummary Kpis, KpiSeverity TrsSeverity, IReadOnlyList<SynthesisLine> Lines, IReadOnlyList<string> Planned, string Grain,
    DeckChart TrsChart, DeckChart DeviationChart);

// One cell of the machine x team table: net weight produced, bags and TRS
// (with its D / P / Q), banded against the machine's (or the line's) target.
public sealed record PackCell(double ProductionKg, long TotalBags, KpiSummary Kpis, KpiSeverity TrsSeverity)
{
    public bool HasProduction => TotalBags > 0 || ProductionKg > 0;

    public static PackCell Of(IEnumerable<ProductionBucket> lots, TrsBand band)
    {
        var list = lots.ToList();
        var kpis = KpiCalculator.Summarize(list, KpiCalculator.AvailabilityPercent(list), band);
        return new PackCell(kpis.ProductionKg, list.Sum(l => (long)l.TotalBags), kpis, KpiCalculator.TrsSeverity(kpis.TrsPercent, band));
    }
}

public sealed record PackRow(string Label, bool IsTotal, IReadOnlyList<PackCell> Cells, PackCell Total);

// A machine of a line overview: its state now and its figures over the
// period; Planned = not connected yet (no figures).
public sealed record LineTile(string Name, string State, string StateText, bool Running, OngoingStop? Stop, PackCell? Figures, double? TrsTarget,
    bool Planned);

public sealed record LineSlide(string Title, string Sector, string Color, string PeriodLabel, string FilterLabel, PackCell Overall, double TrsTarget,
    KpiSeverity WeightSeverity, int Running, int Stopped, IReadOnlyList<LineTile> Tiles, IReadOnlyList<string> Teams, IReadOnlyList<PackRow> Rows,
    DeckChart TeamChart, DeckChart MachineChart);

public sealed record PlantMachineState(string Name, string State, string StateText);

public sealed record PlantLine(string Name, string Sector, string Color, PackCell Figures, double TrsTarget, IReadOnlyList<PlantMachineState> Machines,
    int Planned);

public sealed record PlantSlide(string PeriodLabel, string FilterLabel, PackCell Overall, double TrsTarget, int Running, int Stopped,
    IReadOnlyList<PlantLine> Lines, DeckChart TrsChart, DeckChart ProductionChart);
