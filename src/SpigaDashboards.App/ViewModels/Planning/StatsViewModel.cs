using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Planning;

public sealed record StatTile(string Label, string Value, string Hint, Brush? DeltaBrush = null, string Delta = "");

// One bar: Ratio (0..1) sizes it against the longest one.
public sealed record StatBar(string Label, string Value, string Detail, double Ratio, Brush Fill, string Tooltip, string Extra = "");

public sealed record HeatCell(Brush Fill, Brush Ink, string Text, string Tooltip);
public sealed record HeatRow(string Day, IReadOnlyList<HeatCell> Cells);

public sealed record RepeatRow(string MachineName, string Component, int Count, IReadOnlyList<int> Dots, string Lost,
    string Mtbf, string PlanText, Brush PlanBrush, string MachineId, IRelayCommand Plan);

// "Statistiques" tab: the qualified stops of the last 7 / 30 / 90 days, by
// cause, component, line, day and hour, poste and team, plus the components
// that keep failing. Only stops given a cause count (Pannes tab).
public partial class StatsViewModel : ObservableObject
{
    private readonly PlanningContext _ctx;
    private bool _refreshAgain;

    public event Action<string, string>? PreventiveRequested;

    public StatsViewModel(PlanningContext ctx) => _ctx = ctx;

    [ObservableProperty] private int periodDays = 30;
    [ObservableProperty] private bool pannesOnly = true;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string paretoCaption = "";
    [ObservableProperty] private string machineCaption = "";
    [ObservableProperty] private string heatCaption = "";
    [ObservableProperty] private string groupCaption = "";

    public ObservableCollection<StatTile> Tiles { get; } = new();
    public ObservableCollection<StatBar> ByCause { get; } = new();
    public ObservableCollection<StatBar> ByComponent { get; } = new();
    public ObservableCollection<StatBar> ByMachine { get; } = new();
    public ObservableCollection<StatBar> ByPoste { get; } = new();
    public ObservableCollection<StatBar> ByTeam { get; } = new();
    public ObservableCollection<StatBar> Weeks { get; } = new();
    public ObservableCollection<HeatRow> Heat { get; } = new();
    public ObservableCollection<string> HeatHours { get; } = new();
    public ObservableCollection<Brush> HeatScale { get; } = new();
    public ObservableCollection<RepeatRow> Repeats { get; } = new();

    public bool HasRepeats => Repeats.Count > 0;

    partial void OnPeriodDaysChanged(int value)
    {
        OnPropertyChanged(nameof(PeriodKey));
        _ = RefreshAsync();
    }

    // The period buttons bind to this (two-way), whatever checks them: mouse, keyboard, UI Automation.
    public string PeriodKey
    {
        get => PeriodDays.ToString(CultureInfo.InvariantCulture);
        set => PeriodDays = int.Parse(value, CultureInfo.InvariantCulture);
    }
    partial void OnPannesOnlyChanged(bool value) => _ = RefreshAsync();

    private static bool Dark => ThemeService.CurrentTheme == AppTheme.Dark;
    private static Brush Blue => PlanningContext.Brush(Color.FromRgb(0x39, 0x87, 0xE5));
    private static Brush BlueDim => PlanningContext.Brush(Dark ? Color.FromRgb(0x25, 0x6A, 0xBF) : Color.FromRgb(0x86, 0xB6, 0xEF));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // A change made while loading (period clicked as the tab opens) runs right after.
        if (IsBusy)
        {
            _refreshAgain = true;
            return;
        }
        IsBusy = true;
        StatusMessage = "Calcul…";
        try
        {
            await BuildAsync();
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - statistiques : {ex}");
            StatusMessage = PlanningErrors.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
        if (_refreshAgain)
        {
            _refreshAgain = false;
            await RefreshAsync();
        }
    }

    private async Task BuildAsync()
    {
        var now = DateTime.Now;
        var from = now.Date.AddDays(-PeriodDays + 1);
        var prevFrom = from.AddDays(-PeriodDays);
        var ids = _ctx.Machines.Select(m => m.MachineId).ToHashSet();

        var all = (await _ctx.Planning.GetQualificationsAsync(prevFrom, now.AddDays(1)))
            .Where(q => ids.Contains(q.MachineId) && (!PannesOnly || q.IsPanne)).ToList();
        var cur = all.Where(q => q.StopStart >= from).ToList();
        var prev = all.Where(q => q.StopStart < from).ToList();

        // Run hours for MTBF / lost availability: day buckets are light.
        var buckets = await _ctx.Production.GetLotsForMachinesAsync(ids.ToList(), from, now, BucketSize.Day);
        var runHours = buckets.Sum(b => b.RunMinutes) / 60;

        var minutes = cur.Sum(q => q.Minutes);
        var prevMinutes = prev.Sum(q => q.Minutes);
        Tiles.Clear();
        Tiles.Add(new StatTile(PannesOnly ? "Pannes" : "Arrêts qualifiés", cur.Count.ToString(CultureInfo.InvariantCulture),
            $"vs {prev.Count} sur les {PeriodDays} j précédents", DeltaBrush(cur.Count, prev.Count), Delta(cur.Count, prev.Count)));
        Tiles.Add(new StatTile("Temps d'arrêt", PlanningContext.Duration(minutes),
            $"vs {PlanningContext.Duration(prevMinutes)} avant", DeltaBrush(minutes, prevMinutes), Delta(minutes, prevMinutes)));
        Tiles.Add(new StatTile("MTBF · temps moyen entre pannes", cur.Count == 0 || runHours <= 0 ? "—" : $"{runHours / cur.Count:0} h",
            $"sur {runHours:N0} h de marche des lignes"));
        Tiles.Add(new StatTile("MTTR · temps moyen de réparation", cur.Count == 0 ? "—" : PlanningContext.Duration(minutes / cur.Count),
            prev.Count == 0 ? "par arrêt" : $"vs {PlanningContext.Duration(prevMinutes / prev.Count)} avant"));
        Tiles.Add(new StatTile("Disponibilité perdue", runHours <= 0 ? "—" : (minutes / 60 / (runHours + minutes / 60) * 100).ToString("0.0", PlanningContext.French) + " %",
            "du temps de marche + arrêts"));

        // Pareto by cause and by component
        Fill(ByCause, Pareto(cur.GroupBy(q => StopCauses.Label(q.CauseCode)), minutes, 8));
        Fill(ByComponent, Pareto(cur.Where(q => !string.IsNullOrEmpty(q.Component))
            .GroupBy(q => q.Component!, StringComparer.OrdinalIgnoreCase), minutes, 8));
        var top3 = cur.GroupBy(q => q.Component ?? StopCauses.Label(q.CauseCode)).Select(g => g.Sum(q => q.Minutes))
            .OrderByDescending(m => m).Take(3).Sum();
        ParetoCaption = minutes <= 0 ? "Aucun arrêt qualifié sur la période"
            : $"Les 3 premiers composants font {top3 / minutes * 100:0} % du temps d'arrêt";

        // By line
        var perMachine = _ctx.Machines.Select(m => (m, list: cur.Where(q => q.MachineId == m.MachineId).ToList()))
            .OrderByDescending(x => x.list.Sum(q => q.Minutes)).ToList();
        var maxMachine = Math.Max(1, perMachine.Max(x => x.list.Sum(q => q.Minutes)));
        ByMachine.Clear();
        for (var i = 0; i < perMachine.Count; i++)
        {
            var (m, list) = perMachine[i];
            var mins = list.Sum(q => q.Minutes);
            ByMachine.Add(new StatBar(m.MachineName, PlanningContext.Duration(mins), $"{list.Count} p.", mins / maxMachine,
                i < 2 && mins > 0 ? Blue : BlueDim,
                $"{m.MachineName} : {PlanningContext.Duration(mins)} · {list.Count} arrêts" + (list.Count > 0 ? $" · MTTR {PlanningContext.Duration(mins / list.Count)}" : ""),
                Extra: m.MachineName));
        }
        var top2 = perMachine.Take(2).Sum(x => x.list.Sum(q => q.Minutes));
        MachineCaption = minutes <= 0 || perMachine.Count < 2 ? "" : $"{perMachine[0].m.MachineName} et {perMachine[1].m.MachineName} : {top2 / minutes * 100:0} % du temps perdu";

        // Heatmap: day of week x hour of the start
        BuildHeat(cur);

        // By poste and team
        Fill(ByPoste, Groups(cur.GroupBy(q => KpiCalculator.ShiftName(q.ShiftId)), new[] { "Matin", "Après-midi", "Nuit" }));
        Fill(ByTeam, Groups(cur.GroupBy(q => q.Team), _ctx.Teams.TeamNames.ToArray()));
        var night = cur.Where(q => q.ShiftId == 3).Sum(q => q.Minutes);
        GroupCaption = minutes <= 0 ? "" : $"Le poste de nuit concentre {night / minutes * 100:0} % du temps d'arrêt";

        // 12-week trend (independent of the period)
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var trend = (await _ctx.Planning.GetQualificationsAsync(weekStart.AddDays(-77), now.AddDays(1)))
            .Where(q => ids.Contains(q.MachineId) && (!PannesOnly || q.IsPanne)).ToList();
        var weekly = Enumerable.Range(0, 12).Select(i =>
        {
            var ws = weekStart.AddDays(-7 * (11 - i));
            return (ws, mins: trend.Where(q => q.StopStart >= ws && q.StopStart < ws.AddDays(7)).Sum(q => q.Minutes));
        }).ToList();
        var maxWeek = Math.Max(1, weekly.Max(w => w.mins));
        Weeks.Clear();
        foreach (var (ws, mins) in weekly)
        {
            var week = ISOWeek.GetWeekOfYear(ws);
            var current = ws == weekStart;
            Weeks.Add(new StatBar("S" + week, PlanningContext.Duration(mins), "", mins / maxWeek,
                current ? BlueDim : Blue, $"Semaine {week} : {PlanningContext.Duration(mins)}" + (current ? " (en cours)" : "")));
        }

        // Components that keep failing on the same line
        var plans = await _ctx.Planning.GetPlansAsync();
        Repeats.Clear();
        foreach (var g in cur.Where(q => q.IsPanne && !string.IsNullOrEmpty(q.Component))
                     .GroupBy(q => (q.MachineId, Component: q.Component!.ToLowerInvariant()))
                     .Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()))
        {
            var component = g.First().Component!;
            var plan = plans.FirstOrDefault(p => p.MachineId == g.Key.MachineId && p.Task.Contains(component, StringComparison.OrdinalIgnoreCase));
            var machineId = g.Key.MachineId;
            Repeats.Add(new RepeatRow(_ctx.NameOf(machineId), component, g.Count(), Enumerable.Range(0, Math.Min(8, g.Count())).ToList(),
                PlanningContext.Duration(g.Sum(q => q.Minutes)), $"{(double)PeriodDays / g.Count():0.#} j",
                plan == null ? "Aucun préventif" : MaintenanceText.Rule(plan),
                plan == null ? PlanningContext.Brush(Color.FromRgb(0x9A, 0xA4, 0xB2)) : PlanningContext.Brush(Color.FromRgb(0xFA, 0xB2, 0x19)),
                machineId, new RelayCommand(() => PreventiveRequested?.Invoke(machineId, component))));
        }
        OnPropertyChanged(nameof(HasRepeats));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static List<StatBar> Pareto(IEnumerable<IGrouping<string, StopQualification>> groups, double total, int take)
    {
        var list = groups.Select(g => (g.Key, mins: g.Sum(q => q.Minutes), n: g.Count()))
            .OrderByDescending(x => x.mins).ToList();
        if (list.Count > take)
        {
            var rest = list.Skip(take - 1).ToList();
            list = list.Take(take - 1).Append(("Autres", rest.Sum(x => x.mins), rest.Sum(x => x.n))).ToList();
        }
        var max = Math.Max(1, list.Select(x => x.mins).DefaultIfEmpty(0).Max());
        double cum = 0;
        return list.Select((x, i) =>
        {
            cum += x.mins;
            var cumPct = total > 0 ? cum / total * 100 : 0;
            return new StatBar(x.Key, PlanningContext.Duration(x.mins), $"{x.n}", x.mins / max, i < 3 ? Blue : BlueDim,
                $"{x.Key} : {PlanningContext.Duration(x.mins)} · {x.n} arrêts · {(total > 0 ? x.mins / total * 100 : 0):0} % du total",
                Extra: $"{cumPct:0} %");
        }).ToList();
    }

    private List<StatBar> Groups(IEnumerable<IGrouping<string, StopQualification>> groups, string[] order)
    {
        var map = groups.ToDictionary(g => g.Key, g => (mins: g.Sum(q => q.Minutes), n: g.Count()));
        var keys = order.Concat(map.Keys.Where(k => !order.Contains(k))).ToList();
        var max = Math.Max(1, map.Values.Select(v => v.mins).DefaultIfEmpty(0).Max());
        return keys.Select(k =>
        {
            var (mins, n) = map.GetValueOrDefault(k);
            return new StatBar(k, PlanningContext.Duration(mins), $"{n} p.", mins / max, Blue, $"{k} : {PlanningContext.Duration(mins)} · {n} arrêts");
        }).ToList();
    }

    private void BuildHeat(List<StopQualification> cur)
    {
        // One blue, light to dark on the light theme and dark to light on the dark one.
        var ramp = Dark
            ? new[] { "#22272F", "#104281", "#1C5CAB", "#2A78D6", "#5598E7", "#9EC5F4" }
            : new[] { "#EEF1F5", "#CDE2FB", "#9EC5F4", "#5598E7", "#2A78D6", "#1C5CAB" };
        var brushes = ramp.Select(h => PlanningContext.Brush((Color)ColorConverter.ConvertFromString(h))).ToArray();
        var darkInk = PlanningContext.Brush(Color.FromRgb(0x0B, 0x12, 0x20));
        var lightInk = PlanningContext.Brush(Colors.White);
        var max = 1;
        var counts = new int[7, 24];
        foreach (var q in cur)
        {
            var d = ((int)q.StopStart.DayOfWeek + 6) % 7;
            max = Math.Max(max, ++counts[d, q.StopStart.Hour]);
        }

        HeatScale.Clear();
        foreach (var b in brushes)
            HeatScale.Add(b);
        HeatHours.Clear();
        for (var h = 0; h < 24; h++)
            HeatHours.Add(h.ToString("00"));

        var days = new[] { "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi", "Dimanche" };
        Heat.Clear();
        for (var d = 0; d < 7; d++)
        {
            var cells = new List<HeatCell>();
            for (var h = 0; h < 24; h++)
            {
                var n = counts[d, h];
                var step = n == 0 ? 0 : Math.Clamp((int)Math.Ceiling(n * 5.0 / max), 1, 5);
                var ink = Dark ? (step >= 4 ? darkInk : lightInk) : (step >= 3 ? lightInk : darkInk);
                cells.Add(new HeatCell(brushes[step], ink, n > 0 ? n.ToString(CultureInfo.InvariantCulture) : "",
                    $"{days[d]} {h:00}:00–{(h + 1) % 24:00}:00 · {n} arrêt{(n > 1 ? "s" : "")}"));
            }
            Heat.Add(new HeatRow(days[d][..3] + ".", cells));
        }

        var peak = Enumerable.Range(0, 24).Select(h => (h, n: Enumerable.Range(0, 7).Sum(d => counts[d, h]))).OrderByDescending(x => x.n).First();
        HeatCaption = cur.Count == 0 ? "Heure de début de chaque arrêt"
            : $"Heure de début de chaque arrêt · heure la plus chargée : {peak.h:00}h ({peak.n} arrêt{(peak.n > 1 ? "s" : "")})";
    }

    private static string Delta(double cur, double prev)
        => prev <= 0 ? "" : (cur >= prev ? "▲ " : "▼ ") + Math.Abs((cur - prev) / prev * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    private static Brush? DeltaBrush(double cur, double prev)
        => prev <= 0 ? null : PlanningContext.Brush(cur > prev ? Color.FromRgb(0xE7, 0x4C, 0x3C) : Color.FromRgb(0x16, 0xA3, 0x4A));
}
