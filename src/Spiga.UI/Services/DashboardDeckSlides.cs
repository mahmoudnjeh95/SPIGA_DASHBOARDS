using System.Collections.Concurrent;
using Spiga.Core.Security;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Slides;

namespace Spiga.UI.Services;

// The group slides of the web dashboard: Synthèse (per sector), Conditionnement
// (production and TRS of a group of lines per machine and team) and État des
// machines (run state photos). Same figures as the kiosk's
// SynthesisSlideViewModel, PackagingSlideViewModel and RunStateSlideViewModel.
public sealed partial class DashboardDeckService
{
    // A year of sub-minute snapshots is heavy to aggregate again and these
    // figures barely move: the lots read are kept a while (the kiosk does
    // the same). Keyed by the machines read, so an account only ever gets
    // lots of machines it was allowed to read.
    private static readonly TimeSpan SynthesisCache = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PackagingCache = TimeSpan.FromMinutes(10);
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

    // ---- Synthèse ----

    public async Task<SynthesisSlide> SynthesisAsync(Actor actor, string sector, IReadOnlyList<Machine> machines, SynthesisPeriod period,
        DashboardFilter filter, CancellationToken cancel = default)
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
        // Reference line only when every line shares the same TRS target.
        var targets = machines.Select(m => settings.TrsTarget(m.MachineId)).Distinct().ToList();

        return new SynthesisSlide(Machine.SectorLabel(sector), period, label, filter.Describe(),
            KpiCalculator.Summarize(lots, KpiCalculator.AvailabilityPercent(lots)), lines, "par " + PeriodRanges.GrainName(grain),
            DeckCharts.Lines(labels, trsLines, v => $"{v:0} %", targets.Count == 1 ? targets[0] : null),
            DeckCharts.Lines(labels, deviationLines, v => v.ToString("+0.00;-0.00;0", Fmt.French) + " %", 0));
    }

    // ---- Conditionnement ----

    // Top-bar period ("Aujourd'hui" and "Poste précédent": the whole day);
    // Cumul is always the current month.
    public async Task<PackagingSlide> PackagingAsync(Actor actor, PackagingSlideDescriptor slide, IReadOnlyList<Machine> machines,
        DashboardPeriod period, DateTime? customFrom, DateTime? customTo, DashboardFilter filter, CancellationToken cancel = default)
    {
        filter = ForActor(actor, filter);
        var repository = Repository(actor);
        var now = Now;
        DateTime from, to;
        string label;
        if (slide.Cumul)
            (from, to, label) = PeriodRanges.Synthesis(SynthesisPeriod.Month, now);
        else
        {
            var p = period == DashboardPeriod.PreviousShift ? DashboardPeriod.Day : period;
            (from, to) = PeriodRanges.Get(p, now, customFrom, customTo);
            label = PeriodRanges.Label(p, from, to);
        }
        (from, label) = FromDataStart(from, to, label);

        var ids = machines.Select(m => m.MachineId).ToList();
        // Hour buckets: teams are worked out from each bucket's poste.
        var all = (to - from).TotalDays <= 8
            ? await repository.GetLotsForMachinesAsync(ids, from, to)
            : await CachedAsync($"pack|{from:yyyyMMddHH}|{to:yyyyMMddHH}|{string.Join(",", ids)}", PackagingCache, now,
                () => repository.GetLotsForMachinesAsync(ids, from, to));
        cancel.ThrowIfCancellationRequested();
        var lots = filter.Apply(all, teams);

        // Every configured team, even at 0, so the columns compare like for
        // like; "Sans équipe" only when such hours produced something.
        var columns = filter.Team != null ? new List<string> { filter.Team } : teams.TeamNames.ToList();
        var byTeam = lots.ToLookup(teams.TeamOf);
        if (!columns.Contains(TeamScheduleService.NoTeam) && byTeam[TeamScheduleService.NoTeam].Any(l => l.TotalBags > 0))
            columns.Add(TeamScheduleService.NoTeam);

        var bands = machines.Select(m => settings.TrsBandFor(m.MachineId)).ToList();
        var groupBand = TrsBand.Average(bands);
        var rows = machines.Select(m =>
        {
            var machineLots = lots.Where(l => l.MachineId == m.MachineId).ToList();
            var band = settings.TrsBandFor(m.MachineId);
            return new PackRow(m.MachineName, false, columns.Select(t => PackCell.Of(machineLots.Where(l => teams.TeamOf(l) == t), band)).ToList(),
                PackCell.Of(machineLots, band));
        }).ToList();
        var teamTotals = columns.Select(t => PackCell.Of(byTeam[t], groupBand)).ToList();
        var total = PackCell.Of(lots, groupBand);
        var machineRows = rows.ToList();
        rows.Add(new PackRow("Total", true, teamTotals, total));

        // Target line only when every machine shares the same target.
        var targets = bands.Select(b => b.Target).Distinct().ToList();
        double? targetLine = targets.Count == 1 ? targets[0] : null;
        var names = machines.Select(m => m.MachineName).ToList();

        DeckChart teamChart, machineChart;
        if (slide.Trs)
        {
            teamChart = TrsBars(columns, teamTotals.Select(c => c.Kpis.TrsPercent).ToList(), targetLine);
            machineChart = TrsBars(names, machineRows.Select(r => r.Total.Kpis.TrsPercent).ToList(), targetLine);
        }
        else
        {
            teamChart = DeckCharts.Bars(columns, teamTotals.Select(c => (double?)Math.Round(c.ProductionKg)).ToList(), "Production",
                v => KpiCalculator.FormatMass(v));
            // Each machine's production, piled up by team.
            machineChart = DeckCharts.Stacked(names, columns.Select((t, i) =>
                (t, (IReadOnlyList<double?>)machineRows.Select(r => (double?)Math.Round(r.Cells[i].ProductionKg)).ToList())).ToList(), v => KpiCalculator.FormatMass(v));
        }

        return new PackagingSlide(slide.DisplayTitle, slide.Trs, slide.Cumul ? $" · {ids.Count} machines" : "", label, filter.Describe(),
            total, groupBand.Target, columns, rows, teamChart, machineChart);
    }

    private static DeckChart TrsBars(IReadOnlyList<string> labels, IReadOnlyList<double?> values, double? target)
        => values.Any(v => v.HasValue)
            ? DeckCharts.Bars(labels, values.Select(v => v is { } x ? (double?)Math.Round(x, 1) : null).ToList(), "TRS", v => $"{v:0} %", target)
            : DeckChart.Empty;

    // ---- État des machines ----

    // Live state whatever the period: a line with a stop in progress is not
    // running, even if a few bags moved its counter.
    public async Task<RunStateSlide> RunStateAsync(Actor actor, string title, IReadOnlyList<Machine> machines, CancellationToken cancel = default)
    {
        var repository = Repository(actor);
        var slots = await repository.GetActiveSlotKindsAsync();
        var stops = await OngoingStopService.GetAsync(repository, settings);
        var tiles = new List<RunTile>();
        foreach (var m in machines)
        {
            cancel.ThrowIfCancellationRequested();
            var status = await repository.GetLatestStatusAsync(m.MachineId);
            var stop = stops.GetValueOrDefault(m.MachineId);
            var header = new MachineHeader(m.MachineId, m.MachineName, m.Sector, status?.IsConnected ?? false,
                (status?.BeltRunning ?? false) && stop == null, slots.TryGetValue(m.MachineId, out var slot) ? slot : null, stop, null);
            tiles.Add(new RunTile(m.MachineName, header.State, header.StateText, header.Running, stop));
        }
        return new RunStateSlide(title, tiles);
    }
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

public sealed record SynthesisLine(string Name, KpiSummary Kpis, double TrsTarget, KpiSeverity TrsSeverity, KpiSeverity WeightSeverity);

public sealed record SynthesisSlide(string Sector, SynthesisPeriod Period, string PeriodLabel, string FilterLabel, KpiSummary Kpis,
    IReadOnlyList<SynthesisLine> Lines, string Grain, DeckChart TrsChart, DeckChart DeviationChart);

// One cell of the machine x team table: net weight produced, bags and TRS
// (with its D / P / Q), banded against the machine's (or the group's) target.
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

public sealed record PackagingSlide(string Title, bool Trs, string MachinesLabel, string PeriodLabel, string FilterLabel, PackCell Overall,
    double TrsTarget, IReadOnlyList<string> Teams, IReadOnlyList<PackRow> Rows, DeckChart TeamChart, DeckChart MachineChart);

public sealed record RunTile(string Name, string State, string StateText, bool Running, OngoingStop? Stop);

public sealed record RunStateSlide(string Title, IReadOnlyList<RunTile> Tiles);
