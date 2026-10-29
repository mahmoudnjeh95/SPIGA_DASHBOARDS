using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Planning;

public enum BlockKind
{
    Order,
    Maintenance,
    MaintenanceDone,
    MaintenanceLate,
    Panne,
}

// One block on a line of the week board, placed by fractions of the week.
public partial class BoardBlock : ObservableObject
{
    public string Key { get; init; } = "";
    public BlockKind Kind { get; init; }
    public double StartFrac { get; init; }
    public double EndFrac { get; init; }
    public string Label { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public Brush Fill { get; init; } = Brushes.Transparent;
    public Brush Stroke { get; init; } = Brushes.Transparent;
    public bool ShowLabel { get; init; }
    public bool HasProgress { get; init; }
    public double Progress { get; init; }
    public IRelayCommand? Select { get; set; }
    [ObservableProperty] private bool isSelected;

    public object? Source { get; init; }
    public double ProducedBags { get; init; }
}

public sealed class MachineRow
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string StateText { get; init; } = "";
    public Brush StateBrush { get; init; } = Brushes.Gray;
    public ObservableCollection<BoardBlock> Blocks { get; } = new();
}

public sealed record SectorGroup(string Name, Brush Color, string Caption, IReadOnlyList<MachineRow> Rows);
public sealed record DayHeader(string Name, bool IsToday);
public sealed record DetailRow(string Key, string Value);
public sealed record AlertRow(string Machine, string Text, Brush Brush);

public sealed record PreventiveRow(string MachineId, string MachineName, string Task, string Rule, string DueText,
    string StatusText, Brush StatusBrush, bool HasGauge, double Gauge,
    IRelayCommand Plan, IRelayCommand Done, IRelayCommand Edit);

public enum EditorMode
{
    None,
    Order,
    Event,
    Plan,
}

public static class MaintenanceText
{
    public static string Rule(MaintenancePlan p) => p.Kind == MaintenanceKind.Calendar
        ? $"Calendaire · tous les {p.IntervalDays} j"
        : $"Usage · tous les {PlanningContext.Bags(p.IntervalBags)} sacs";
}

// "Planification" tab: the week of every line (orders, maintenance, pannes)
// with the real progress of each order from the counter, the preventive
// maintenance rules and what is due, and the alerts a planner acts on.
public partial class BoardViewModel : ObservableObject
{
    public static readonly string[] PosteNames = { "Matin", "Après-midi", "Nuit" };
    private readonly PlanningContext _ctx;
    private bool _refreshAgain;
    private List<ProductionOrder> _orders = new();
    private List<MaintenanceEvent> _events = new();
    private List<MaintenancePlan> _plans = new();
    private ProductionOrder? _editingOrder;
    private MaintenanceEvent? _editingEvent;
    private MaintenancePlan? _editingPlan;

    public BoardViewModel(PlanningContext ctx)
    {
        _ctx = ctx;
        WeekStart = MondayOf(DateTime.Now);
    }

    public ObservableCollection<DayHeader> Days { get; } = new();
    public ObservableCollection<SectorGroup> Sectors { get; } = new();
    public ObservableCollection<PreventiveRow> Preventives { get; } = new();
    public ObservableCollection<AlertRow> Alerts { get; } = new();
    public ObservableCollection<DetailRow> DetailRows { get; } = new();
    public ObservableCollection<string> MachineNames { get; } = new();
    public List<string> PosteOptions { get; } = PosteNames.ToList();
    public List<string> PlanKindOptions { get; } = new() { "Calendaire (jours)", "Usage (sacs produits)" };

    [ObservableProperty] private DateTime weekStart;
    [ObservableProperty] private string weekLabel = "";
    [ObservableProperty] private double nowFrac = -1;
    [ObservableProperty] private bool isNowVisible;
    [ObservableProperty] private double todayStartFrac;
    [ObservableProperty] private double todayEndFrac;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "";

    // KPI strip
    [ObservableProperty] private string chargeText = "—";
    [ObservableProperty] private double chargeRatio;
    [ObservableProperty] private string ordersText = "0";
    [ObservableProperty] private string ordersSub = "";
    [ObservableProperty] private string maintText = "0";
    [ObservableProperty] private string maintSub = "";
    [ObservableProperty] private string pannesText = "0";
    [ObservableProperty] private string pannesSub = "";

    // Detail panel
    [ObservableProperty] private BoardBlock? selectedBlock;
    [ObservableProperty] private string detailKind = "";
    [ObservableProperty] private Brush detailKindBrush = Brushes.Gray;
    [ObservableProperty] private string detailTitle = "";
    [ObservableProperty] private string detailSub = "";
    [ObservableProperty] private string detailStatus = "";
    [ObservableProperty] private Brush detailStatusBrush = Brushes.Gray;
    [ObservableProperty] private bool detailHasProgress;
    [ObservableProperty] private double detailProgress;
    [ObservableProperty] private double detailExpected;
    [ObservableProperty] private string detailProgressText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailNote))]
    private string detailNote = "";
    public bool HasDetailNote => DetailNote.Length > 0;
    [ObservableProperty] private Brush detailNoteBrush = Brushes.Gray;
    [ObservableProperty] private bool canEditSelected;
    [ObservableProperty] private bool canCloseSelected;
    [ObservableProperty] private string closeSelectedText = "";

    // Editors
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingOrder), nameof(IsEditingEvent), nameof(IsEditingPlan), nameof(IsEditing), nameof(ShowDetail))]
    private EditorMode editor;
    public bool IsEditingOrder => Editor == EditorMode.Order;
    public bool IsEditingEvent => Editor == EditorMode.Event;
    public bool IsEditingPlan => Editor == EditorMode.Plan;
    public bool IsEditing => Editor != EditorMode.None;
    public bool ShowDetail => Editor == EditorMode.None && SelectedBlock != null;
    public bool ShowEmptyDetail => Editor == EditorMode.None && SelectedBlock == null;
    public bool HasAlerts => Alerts.Count > 0;
    public bool HasPreventives => Preventives.Count > 0;

    [ObservableProperty] private string formTitle = "";
    [ObservableProperty] private string orderNo = "";
    [ObservableProperty] private string orderMachine = "";
    [ObservableProperty] private string orderProduct = "";
    [ObservableProperty] private string orderWeight = "";
    [ObservableProperty] private string orderQuantity = "";
    [ObservableProperty] private string orderHours = "";
    [ObservableProperty] private DateTime? orderDate = DateTime.Today;
    [ObservableProperty] private string orderPoste = PosteNames[0];
    [ObservableProperty] private string orderEstimate = "";

    [ObservableProperty] private string eventMachine = "";
    [ObservableProperty] private string eventTask = "";
    [ObservableProperty] private DateTime? eventDate = DateTime.Today;
    [ObservableProperty] private string eventPoste = PosteNames[0];
    [ObservableProperty] private string eventDuration = "60";
    [ObservableProperty] private string eventTechnician = "";

    [ObservableProperty] private string planMachine = "";
    [ObservableProperty] private string planTask = "";
    [ObservableProperty] private int planKindIndex;
    [ObservableProperty] private string planInterval = "14";
    [ObservableProperty] private string planDuration = "60";
    [ObservableProperty] private DateTime? planLastDone = DateTime.Today;
    public string PlanIntervalLabel => PlanKindIndex == 0 ? "Tous les (jours)" : "Tous les (sacs)";

    partial void OnPlanKindIndexChanged(int value) => OnPropertyChanged(nameof(PlanIntervalLabel));
    partial void OnOrderMachineChanged(string value) => UpdateEstimate();
    partial void OnOrderWeightChanged(string value) => UpdateEstimate();
    partial void OnOrderQuantityChanged(string value) => UpdateEstimate();
    partial void OnOrderHoursChanged(string value) => UpdateEstimate();
    partial void OnOrderDateChanged(DateTime? value) => UpdateEstimate();
    partial void OnOrderPosteChanged(string value) => UpdateEstimate();
    partial void OnSelectedBlockChanged(BoardBlock? oldValue, BoardBlock? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowEmptyDetail));
        ShowDetails(newValue);
    }
    partial void OnEditorChanged(EditorMode value) => OnPropertyChanged(nameof(ShowEmptyDetail));

    private static DateTime MondayOf(DateTime t)
    {
        var day = Postes.Of(t).ShiftDate;
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).AddHours(6);
    }

    private DateTime WeekEnd => WeekStart.AddDays(7);
    private double Frac(DateTime t) => Math.Clamp((t - WeekStart).TotalHours / 168, 0, 1);

    public async Task InitializeAsync()
    {
        MachineNames.Clear();
        foreach (var m in _ctx.Machines)
            MachineNames.Add(m.MachineName);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task PreviousWeekAsync()
    {
        WeekStart = WeekStart.AddDays(-7);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NextWeekAsync()
    {
        WeekStart = WeekStart.AddDays(7);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ThisWeekAsync()
    {
        WeekStart = MondayOf(DateTime.Now);
        await RefreshAsync();
    }

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
        StatusMessage = "Chargement de la semaine…";
        try
        {
            await BuildAsync();
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - semaine : {ex}");
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
        var week = ISOWeek.GetWeekOfYear(WeekStart);
        WeekLabel = $"Semaine {week} · {WeekStart.ToString("d MMM", PlanningContext.French)} → {WeekEnd.AddDays(-1).ToString("d MMM yyyy", PlanningContext.French)}";
        NowFrac = (now - WeekStart).TotalHours / 168;
        IsNowVisible = NowFrac is >= 0 and <= 1;
        var today = Postes.Of(now).ShiftDate.AddHours(6);
        TodayStartFrac = Frac(today);
        TodayEndFrac = Frac(today.AddDays(1));

        Days.Clear();
        for (var i = 0; i < 7; i++)
        {
            var d = WeekStart.Date.AddDays(i);
            Days.Add(new DayHeader(PlanningContext.Day(d), d == today.Date));
        }

        _orders = await _ctx.Planning.GetOrdersAsync(WeekStart, WeekEnd);
        _events = await _ctx.Planning.GetEventsAsync(WeekStart, WeekEnd);
        _plans = await _ctx.Planning.GetPlansAsync();
        var openEvents = await _ctx.Planning.GetOpenEventsAsync();
        var pannes = (await _ctx.Planning.GetQualificationsAsync(WeekStart, WeekEnd)).Where(q => q.IsPanne).ToList();

        // Real production of the orders already started (hour buckets).
        var started = _orders.Where(o => o.PlannedStart < now).ToList();
        var produced = new Dictionary<int, double>();
        if (started.Count > 0)
        {
            var buckets = await _ctx.Production.GetLotsForMachinesAsync(
                started.Select(o => o.MachineId).Distinct().ToList(), started.Min(o => o.PlannedStart), now);
            foreach (var o in started)
            {
                var next = _orders.Where(x => x.MachineId == o.MachineId && x.PlannedStart > o.PlannedStart)
                    .Select(x => (DateTime?)x.PlannedStart).Min();
                var until = new[] { now, next ?? DateTime.MaxValue, o.PlannedEnd.AddHours(8) }.Min();
                var mine = buckets.Where(b => b.MachineId == o.MachineId && b.StartTime >= o.PlannedStart.AddMinutes(-30) && b.StartTime < until).ToList();
                if (o.TargetWeightGrams > 0 && mine.Any(b => Math.Abs(b.TargetNetWeight - o.TargetWeightGrams) < 1))
                    mine = mine.Where(b => Math.Abs(b.TargetNetWeight - o.TargetWeightGrams) < 1).ToList();
                produced[o.Id] = mine.Sum(b => b.TotalBags);
            }
        }

        // Live state of each line.
        var states = new Dictionary<string, MachineStatusSnapshot?>();
        foreach (var m in _ctx.Machines)
        {
            try { states[m.MachineId] = await _ctx.Production.GetLatestStatusAsync(m.MachineId); }
            catch { states[m.MachineId] = null; }
        }

        var selectedKey = SelectedBlock?.Key;
        Sectors.Clear();
        var allBlocks = new List<BoardBlock>();
        double orderHours = 0;
        foreach (var sector in _ctx.Machines.GroupBy(m => m.Sector))
        {
            var color = PlanningContext.SectorColor(sector.Key);
            var rows = new List<MachineRow>();
            double sectorHours = 0;
            foreach (var m in sector)
            {
                var inMaintenance = _events.Any(e => e.MachineId == m.MachineId && e.Status == PlanStatus.Planned && e.PlannedStart <= now && e.PlannedEnd > now);
                var st = states.GetValueOrDefault(m.MachineId);
                var (stateText, stateColor) = inMaintenance ? ("En maintenance", Color.FromRgb(0xFA, 0xB2, 0x19))
                    : st == null || !st.IsConnected ? ("Pas de données", Color.FromRgb(0x9A, 0xA4, 0xB2))
                    : st.BeltRunning ? ("En production", Color.FromRgb(0x2E, 0xCC, 0x71))
                    : ("À l'arrêt", Color.FromRgb(0xE7, 0x4C, 0x3C));
                var row = new MachineRow
                {
                    MachineId = m.MachineId, MachineName = m.MachineName, StateText = stateText,
                    StateBrush = PlanningContext.Brush(stateColor),
                };

                foreach (var o in _orders.Where(o => o.MachineId == m.MachineId))
                {
                    var hours = (Clamp(o.PlannedEnd) - Clamp(o.PlannedStart)).TotalHours;
                    sectorHours += hours;
                    var done = produced.GetValueOrDefault(o.Id);
                    var progress = o.QuantityBags > 0 ? Math.Min(1, done / o.QuantityBags) : 0;
                    if (o.Status == PlanStatus.Done)
                        progress = Math.Max(progress, 1);
                    row.Blocks.Add(new BoardBlock
                    {
                        Key = "O" + o.Id, Kind = BlockKind.Order, StartFrac = Frac(o.PlannedStart), EndFrac = Frac(o.PlannedEnd),
                        Label = o.OrderNo, Sub = o.Product, ShowLabel = (o.PlannedEnd - o.PlannedStart).TotalHours >= 10,
                        Tooltip = $"{o.OrderNo} · {o.Product} · {When(o.PlannedStart)} → {When(o.PlannedEnd)}",
                        Fill = PlanningContext.Brush(color, 0x40), Stroke = PlanningContext.Brush(color),
                        HasProgress = o.PlannedStart < now || o.Status == PlanStatus.Done, Progress = progress,
                        Source = o, ProducedBags = done,
                    });
                }
                foreach (var e in _events.Where(e => e.MachineId == m.MachineId))
                {
                    var kind = e.Status == PlanStatus.Done ? BlockKind.MaintenanceDone
                        : e.PlannedEnd < now ? BlockKind.MaintenanceLate : BlockKind.Maintenance;
                    row.Blocks.Add(new BoardBlock
                    {
                        Key = "E" + e.Id, Kind = kind, StartFrac = Frac(e.PlannedStart), EndFrac = Frac(e.PlannedEnd),
                        Label = kind == BlockKind.MaintenanceLate ? "Non validée" : e.Task, Sub = e.Task,
                        ShowLabel = e.DurationMinutes >= 360,
                        Tooltip = $"Maintenance · {e.Task} · {When(e.PlannedStart)} · {PlanningContext.Duration(e.DurationMinutes)}",
                        Source = e,
                    });
                }
                foreach (var q in pannes.Where(q => q.MachineId == m.MachineId))
                {
                    row.Blocks.Add(new BoardBlock
                    {
                        Key = "Q" + q.Id, Kind = BlockKind.Panne, StartFrac = Frac(q.StopStart),
                        EndFrac = Math.Max(Frac(q.StopEnd), Frac(q.StopStart) + 0.004),
                        Tooltip = $"Panne · {StopCauses.Label(q.CauseCode)}{(q.Component == null ? "" : " · " + q.Component)} · {PlanningContext.Duration(q.Minutes)}",
                        Source = q,
                    });
                }
                foreach (var b in row.Blocks)
                {
                    var block = b;
                    block.Select = new RelayCommand(() => { Editor = EditorMode.None; SelectedBlock = block; });
                }
                allBlocks.AddRange(row.Blocks);
                rows.Add(row);
            }
            orderHours += sectorHours;
            var charge = rows.Count == 0 ? 0 : sectorHours / (rows.Count * 168) * 100;
            Sectors.Add(new SectorGroup(SectorLabel(sector.Key), PlanningContext.Brush(color),
                $"{rows.Count} ligne{(rows.Count > 1 ? "s" : "")} · charge {charge:0} %", rows));
        }

        // KPIs
        var chargeAll = _ctx.Machines.Count == 0 ? 0 : orderHours / (_ctx.Machines.Count * 168) * 100;
        ChargeText = $"{chargeAll:0} %";
        ChargeRatio = Math.Clamp(chargeAll / 100, 0, 1);
        var running = _orders.Where(o => o.Status == PlanStatus.Planned && o.PlannedStart <= now && o.PlannedEnd > now).ToList();
        var lateOrders = running.Where(o => Behind(o, produced.GetValueOrDefault(o.Id), now) > 0.25).ToList();
        OrdersText = _orders.Count.ToString(CultureInfo.InvariantCulture);
        OrdersSub = $"{running.Count} en cours · {lateOrders.Count} en retard sur le plan";
        var overduePlans = _plans.Select(p => (p, s: PlanState(p, now, null))).Where(x => x.s.Overdue && !openEvents.Any(e => e.PlanId == x.p.Id)).ToList();
        MaintText = _events.Count.ToString(CultureInfo.InvariantCulture);
        MaintSub = overduePlans.Count == 0 ? "aucun préventif en retard" : $"{overduePlans.Count} préventif{(overduePlans.Count > 1 ? "s" : "")} en retard";
        PannesText = pannes.Count.ToString(CultureInfo.InvariantCulture);
        PannesSub = $"{PlanningContext.Duration(pannes.Sum(q => q.Minutes))} d'arrêt";

        // Preventive rules (usage ones need the bags made since last done)
        var usage = _plans.Where(p => p.Kind == MaintenanceKind.Usage && p.LastDoneAt != null).ToList();
        var bagsSince = new Dictionary<int, double>();
        if (usage.Count > 0)
        {
            var buckets = await _ctx.Production.GetLotsForMachinesAsync(usage.Select(p => p.MachineId).Distinct().ToList(),
                usage.Min(p => p.LastDoneAt!.Value).Date, now, BucketSize.Day);
            foreach (var p in usage)
                bagsSince[p.Id] = buckets.Where(b => b.MachineId == p.MachineId && b.StartTime.Date >= p.LastDoneAt!.Value.Date).Sum(b => b.TotalBags);
        }
        Preventives.Clear();
        foreach (var p in _plans.OrderBy(p => PlanState(p, now, bagsSince.GetValueOrDefault(p.Id)).Ratio * -1))
        {
            var plan = p;
            var state = PlanState(p, now, bagsSince.GetValueOrDefault(p.Id));
            var open = openEvents.Where(e => e.PlanId == p.Id).OrderBy(e => e.PlannedStart).FirstOrDefault();
            var (statusText, statusColor) = open != null ? ($"Planifiée · {When(open.PlannedStart)}", Color.FromRgb(0x8A, 0xB8, 0xF7))
                : state.Overdue ? (state.DueText, Color.FromRgb(0xD0, 0x3B, 0x3B))
                : state.Soon ? (state.DueText, Color.FromRgb(0xFA, 0xB2, 0x19))
                : (state.DueText, Color.FromRgb(0x2E, 0xCC, 0x71));
            Preventives.Add(new PreventiveRow(p.MachineId, _ctx.NameOf(p.MachineId), p.Task, MaintenanceText.Rule(p) + state.Detail,
                state.DueText, statusText, PlanningContext.Brush(statusColor), p.LastDoneAt != null, Math.Clamp(state.Ratio, 0, 1),
                new AsyncRelayCommand(() => PlanFromRuleAsync(plan, state.Due)),
                new AsyncRelayCommand(() => MarkPlanDoneAsync(plan)),
                new RelayCommand(() => EditPlan(plan))));
        }
        OnPropertyChanged(nameof(HasPreventives));

        // Alerts
        Alerts.Clear();
        var red = PlanningContext.Brush(Color.FromRgb(0xD0, 0x3B, 0x3B));
        var orange = PlanningContext.Brush(Color.FromRgb(0xEC, 0x83, 0x5A));
        var amber = PlanningContext.Brush(Color.FromRgb(0xFA, 0xB2, 0x19));
        foreach (var (p, s) in overduePlans)
            Alerts.Add(new AlertRow(p.MachineId, $"Préventif « {p.Task} » : {s.DueText.ToLowerInvariant()}, pas encore planifié.", red));
        foreach (var o in lateOrders)
        {
            var late = Behind(o, produced.GetValueOrDefault(o.Id), now);
            Alerts.Add(new AlertRow(o.MachineId, $"{o.OrderNo} en retard de {PlanningContext.Duration(late * 60)} au rythme actuel.", orange));
        }
        foreach (var e in _events.Where(e => e.Status == PlanStatus.Planned && e.PlannedEnd < now))
            Alerts.Add(new AlertRow(e.MachineId, $"Maintenance « {e.Task} » du {When(e.PlannedStart)} non validée.", orange));
        foreach (var e in _events.Where(e => e.Status == PlanStatus.Planned))
            foreach (var o in _orders.Where(o => o.MachineId == e.MachineId && o.PlannedStart < e.PlannedEnd && o.PlannedEnd > e.PlannedStart))
                Alerts.Add(new AlertRow(e.MachineId, $"« {e.Task} » tombe pendant {o.OrderNo} ({When(e.PlannedStart)}).", amber));
        var planned = _orders.Where(o => o.Status == PlanStatus.Planned).OrderBy(o => o.PlannedStart).ToList();
        for (var i = 0; i < planned.Count; i++)
            foreach (var other in planned.Skip(i + 1).Where(x => x.MachineId == planned[i].MachineId && x.PlannedStart < planned[i].PlannedEnd))
                Alerts.Add(new AlertRow(other.MachineId, $"{planned[i].OrderNo} et {other.OrderNo} se chevauchent ({When(other.PlannedStart)}).", orange));
        OnPropertyChanged(nameof(HasAlerts));

        SelectedBlock = allBlocks.FirstOrDefault(b => b.Key == selectedKey);
    }

    private DateTime Clamp(DateTime t) => t < WeekStart ? WeekStart : t > WeekEnd ? WeekEnd : t;

    private static string SectorLabel(string sector) => sector switch
    {
        "Pate courte" => "Pâte courte",
        "Pate longue" => "Pâte longue",
        _ => sector,
    };

    public static string When(DateTime t) => PlanningContext.Day(t) + " · " + t.ToString("HH:mm");

    // Hours an order will finish after its planned end, at the pace so far.
    private static double Behind(ProductionOrder o, double produced, DateTime now)
    {
        if (o.QuantityBags <= 0 || now <= o.PlannedStart)
            return 0;
        var elapsed = (now - o.PlannedStart).TotalHours;
        if (produced <= 0)
            return Math.Max(0, (now - o.PlannedStart).TotalHours) > 1 ? (o.PlannedEnd - o.PlannedStart).TotalHours : 0;
        var estimate = now.AddHours(elapsed * (o.QuantityBags - produced) / produced);
        return Math.Max(0, (estimate - o.PlannedEnd).TotalHours);
    }

    private sealed record RuleState(bool Overdue, bool Soon, double Ratio, string DueText, string Detail, DateTime Due);

    private static RuleState PlanState(MaintenancePlan p, DateTime now, double? bagsSince)
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
        var label = remaining <= 0 ? $"Dépassée de {PlanningContext.Bags(-remaining)} sacs" : $"Reste {PlanningContext.Bags(remaining)} sacs";
        return new RuleState(remaining <= 0, r >= 0.85, r, label, $" · {PlanningContext.Bags(used)} faits", now);
    }

    // ---- Detail panel ----

    private void ShowDetails(BoardBlock? block)
    {
        DetailRows.Clear();
        DetailHasProgress = false;
        DetailNote = "";
        CanEditSelected = CanCloseSelected = false;
        if (block == null)
            return;
        var now = DateTime.Now;
        var orange = PlanningContext.Brush(Color.FromRgb(0xEC, 0x83, 0x5A));
        var green = PlanningContext.Brush(Color.FromRgb(0x2E, 0xCC, 0x71));
        var blue = PlanningContext.Brush(Color.FromRgb(0x8A, 0xB8, 0xF7));
        var red = PlanningContext.Brush(Color.FromRgb(0xD0, 0x3B, 0x3B));
        var amber = PlanningContext.Brush(Color.FromRgb(0xFA, 0xB2, 0x19));

        switch (block.Source)
        {
            case ProductionOrder o:
                DetailKind = "ORDRE DE FABRICATION";
                DetailKindBrush = blue;
                DetailTitle = o.OrderNo;
                DetailSub = o.Product + (o.TargetWeightGrams > 0 ? $" · {o.TargetWeightGrams:0.##} g" : "");
                var running = o.Status == PlanStatus.Planned && o.PlannedStart <= now;
                (DetailStatus, DetailStatusBrush) = o.Status == PlanStatus.Done ? ("Terminé", PlanningContext.Brush(Color.FromRgb(0x9A, 0xA4, 0xB2)))
                    : running ? ("En cours", green) : ("Planifié", blue);
                DetailRows.Add(new DetailRow("Ligne", _ctx.NameOf(o.MachineId)));
                DetailRows.Add(new DetailRow("Début prévu", When(o.PlannedStart)));
                DetailRows.Add(new DetailRow("Fin prévue", When(o.PlannedEnd)));
                DetailRows.Add(new DetailRow("Quantité", PlanningContext.Bags(o.QuantityBags) + " sacs"));
                var rate = _ctx.Settings.FindNominalRate(o.MachineId, o.TargetWeightGrams);
                if (rate > 0)
                    DetailRows.Add(new DetailRow("Cadence nominale", $"{rate:0.#} sacs/min"));
                if (o.Comment is { Length: > 0 })
                    DetailRows.Add(new DetailRow("Commentaire", o.Comment));
                if (o.PlannedStart < now || o.Status == PlanStatus.Done)
                {
                    DetailHasProgress = true;
                    DetailProgress = block.Progress;
                    DetailExpected = Math.Clamp((now - o.PlannedStart).TotalHours / Math.Max(0.01, (o.PlannedEnd - o.PlannedStart).TotalHours), 0, 1);
                    DetailProgressText = $"{PlanningContext.Bags(block.ProducedBags)} / {PlanningContext.Bags(o.QuantityBags)} sacs · trait = attendu maintenant ({DetailExpected * 100:0} %)";
                }
                if (running && block.ProducedBags > 0)
                {
                    var behind = Behind(o, block.ProducedBags, now);
                    var elapsed = (now - o.PlannedStart).TotalHours;
                    var estimate = now.AddHours(elapsed * Math.Max(0, o.QuantityBags - block.ProducedBags) / block.ProducedBags);
                    DetailRows.Add(new DetailRow("Fin estimée", When(estimate)));
                    DetailNote = behind > 0.25
                        ? $"Retard estimé de {PlanningContext.Duration(behind * 60)} au rythme actuel : l'ordre suivant sur cette ligne sera décalé."
                        : "Dans les temps au rythme actuel.";
                    DetailNoteBrush = behind > 0.25 ? orange : green;
                }
                else if (running && (now - o.PlannedStart).TotalHours > 1)
                {
                    DetailNote = "Aucun sac compté depuis le début prévu : l'ordre a-t-il démarré ?";
                    DetailNoteBrush = orange;
                }
                CanEditSelected = o.Status == PlanStatus.Planned;
                CanCloseSelected = o.Status == PlanStatus.Planned;
                CloseSelectedText = "Terminer";
                break;

            case MaintenanceEvent e:
                DetailKind = "MAINTENANCE";
                DetailKindBrush = amber;
                DetailTitle = e.Task;
                DetailSub = _ctx.NameOf(e.MachineId);
                (DetailStatus, DetailStatusBrush) = e.Status == PlanStatus.Done ? ("Réalisée", green)
                    : e.PlannedEnd < now ? ("Non validée", red)
                    : e.PlannedStart <= now ? ("En cours", amber) : ("Planifiée", blue);
                DetailRows.Add(new DetailRow("Prévue", When(e.PlannedStart)));
                DetailRows.Add(new DetailRow("Durée", PlanningContext.Duration(e.DurationMinutes)));
                if (_plans.FirstOrDefault(p => p.Id == e.PlanId) is { } plan)
                    DetailRows.Add(new DetailRow("Règle", MaintenanceText.Rule(plan)));
                if (e.Technician is { Length: > 0 })
                    DetailRows.Add(new DetailRow("Intervenant", e.Technician));
                if (e.DoneAt is { } doneAt)
                    DetailRows.Add(new DetailRow("Réalisée le", When(doneAt)));
                if (e.Status == PlanStatus.Planned && e.PlannedEnd < now)
                {
                    DetailNote = "Le créneau est passé : validez-la si elle a été faite, sinon replanifiez-la.";
                    DetailNoteBrush = red;
                }
                CanEditSelected = e.Status == PlanStatus.Planned;
                CanCloseSelected = e.Status == PlanStatus.Planned;
                CloseSelectedText = "Valider";
                break;

            case StopQualification q:
                DetailKind = "PANNE";
                DetailKindBrush = red;
                DetailTitle = q.Component ?? StopCauses.Label(q.CauseCode);
                DetailSub = $"{_ctx.NameOf(q.MachineId)} · {StopCauses.Label(q.CauseCode)}";
                (DetailStatus, DetailStatusBrush) = ("Qualifiée", green);
                DetailRows.Add(new DetailRow("Début", When(q.StopStart)));
                DetailRows.Add(new DetailRow("Fin", When(q.StopEnd)));
                DetailRows.Add(new DetailRow("Durée", PlanningContext.Duration(q.Minutes)));
                DetailRows.Add(new DetailRow("Poste", $"{KpiCalculator.ShiftName(q.ShiftId)} · {q.Team}"));
                if (q.Technician is { Length: > 0 })
                    DetailRows.Add(new DetailRow("Intervenant", q.Technician));
                if (q.Comment is { Length: > 0 })
                    DetailRows.Add(new DetailRow("Commentaire", q.Comment));
                break;
        }
    }

    [RelayCommand]
    private void EditSelected()
    {
        switch (SelectedBlock?.Source)
        {
            case ProductionOrder o: EditOrder(o); break;
            case MaintenanceEvent e: EditEvent(e); break;
        }
    }

    [RelayCommand]
    private async Task CloseSelectedAsync()
    {
        var now = DateTime.Now;
        try
        {
            switch (SelectedBlock?.Source)
            {
                case ProductionOrder o:
                    o.Status = PlanStatus.Done;
                    if (o.PlannedEnd > now && o.PlannedStart < now)
                        o.PlannedEnd = now;
                    await _ctx.Planning.SaveOrderAsync(o);
                    break;
                case MaintenanceEvent e:
                    e.Status = PlanStatus.Done;
                    e.DoneAt = now;
                    await _ctx.Planning.SaveEventAsync(e);
                    if (_plans.FirstOrDefault(p => p.Id == e.PlanId) is { } plan)
                    {
                        plan.LastDoneAt = now;
                        await _ctx.Planning.SavePlanAsync(plan);
                    }
                    break;
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    [RelayCommand]
    private async Task CancelSelectedAsync()
    {
        try
        {
            switch (SelectedBlock?.Source)
            {
                case ProductionOrder o:
                    o.Status = PlanStatus.Cancelled;
                    await _ctx.Planning.SaveOrderAsync(o);
                    break;
                case MaintenanceEvent e:
                    e.Status = PlanStatus.Cancelled;
                    await _ctx.Planning.SaveEventAsync(e);
                    break;
            }
            SelectedBlock = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // ---- Order editor ----

    [RelayCommand]
    private async Task NewOrderAsync()
    {
        _editingOrder = null;
        FormTitle = "Nouvel ordre de fabrication";
        try
        {
            OrderNo = $"OF-{DateTime.Now:yyyy}-{await _ctx.Planning.CountOrdersAsync() + 1:0000}";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
            return;
        }
        OrderMachine = SelectedMachineName() ?? MachineNames.FirstOrDefault() ?? "";
        OrderProduct = "";
        OrderWeight = "";
        OrderQuantity = "";
        OrderHours = "";
        var next = Postes.NextStart(DateTime.Now);
        OrderDate = Postes.Of(next).ShiftDate;
        OrderPoste = PosteNames[Postes.Of(next).ShiftId - 1];
        Editor = EditorMode.Order;
        UpdateEstimate();
    }

    private void EditOrder(ProductionOrder o)
    {
        _editingOrder = o;
        FormTitle = "Modifier " + o.OrderNo;
        OrderNo = o.OrderNo;
        OrderMachine = _ctx.NameOf(o.MachineId);
        OrderProduct = o.Product;
        OrderWeight = o.TargetWeightGrams > 0 ? o.TargetWeightGrams.ToString("0.##", CultureInfo.InvariantCulture) : "";
        OrderQuantity = o.QuantityBags.ToString(CultureInfo.InvariantCulture);
        var poste = Postes.Of(o.PlannedStart);
        OrderDate = poste.ShiftDate;
        OrderPoste = PosteNames[poste.ShiftId - 1];
        OrderHours = (o.PlannedEnd - o.PlannedStart).TotalHours.ToString("0.#", CultureInfo.InvariantCulture);
        Editor = EditorMode.Order;
        UpdateEstimate();
    }

    private string? SelectedMachineName() => SelectedBlock?.Source switch
    {
        ProductionOrder o => _ctx.NameOf(o.MachineId),
        MaintenanceEvent e => _ctx.NameOf(e.MachineId),
        StopQualification q => _ctx.NameOf(q.MachineId),
        _ => null,
    };

    private static DateTime PosteStart(DateTime? date, string poste)
        => (date ?? DateTime.Today).Date.AddHours(Array.IndexOf(PosteNames, poste) switch { 1 => 14, 2 => 22, _ => 6 });

    private static double ParseNumber(string text)
        => double.TryParse(text.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Planned duration: hours typed, else quantity at the nominal rate (Parametres).
    private double? OrderDurationHours(out string how)
    {
        how = "";
        var hours = ParseNumber(OrderHours);
        if (hours > 0)
        {
            how = "durée saisie";
            return hours;
        }
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == OrderMachine);
        var qty = ParseNumber(OrderQuantity);
        if (machine == null || qty <= 0)
            return null;
        var rate = _ctx.Settings.FindNominalRate(machine.MachineId, ParseNumber(OrderWeight));
        if (rate <= 0)
            return null;
        how = $"cadence nominale {rate:0.#} sacs/min";
        return qty / rate / 60;
    }

    private void UpdateEstimate()
    {
        if (Editor != EditorMode.Order)
            return;
        var hours = OrderDurationHours(out var how);
        var start = PosteStart(OrderDate, OrderPoste);
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == OrderMachine);
        OrderEstimate = hours is { } h
            ? $"Durée {PlanningContext.Duration(h * 60)} ({how}) · fin prévue {When(start.AddHours(h))}"
            : machine != null && ParseNumber(OrderQuantity) > 0
                ? $"Pas de cadence nominale pour {machine.MachineId} à {ParseNumber(OrderWeight):0.##} g (Paramètres) : indiquez une durée en heures."
                : "Indiquez la quantité (et le poids pour la cadence nominale), ou une durée en heures.";
    }

    [RelayCommand]
    private async Task SaveOrderAsync()
    {
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == OrderMachine);
        var hours = OrderDurationHours(out _);
        if (machine == null || string.IsNullOrWhiteSpace(OrderProduct) || hours is not { } h)
        {
            StatusMessage = "Ligne, produit et quantité (ou durée) sont nécessaires.";
            return;
        }
        var o = _editingOrder ?? new ProductionOrder();
        o.OrderNo = OrderNo.Trim();
        o.MachineId = machine.MachineId;
        o.Product = OrderProduct.Trim();
        o.TargetWeightGrams = ParseNumber(OrderWeight);
        o.QuantityBags = (long)ParseNumber(OrderQuantity);
        o.PlannedStart = PosteStart(OrderDate, OrderPoste);
        o.PlannedEnd = o.PlannedStart.AddHours(h);
        try
        {
            await _ctx.Planning.SaveOrderAsync(o);
            AppLogger.Log($"OF enregistre : {o.OrderNo} {o.MachineId} {o.PlannedStart:g}");
            Editor = EditorMode.None;
            await GoToAsync(o.PlannedStart, "O" + o.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // ---- Maintenance event editor ----

    [RelayCommand]
    private void NewEvent()
    {
        _editingEvent = null;
        FormTitle = "Planifier une maintenance";
        EventMachine = SelectedMachineName() ?? MachineNames.FirstOrDefault() ?? "";
        EventTask = "";
        EventDuration = "60";
        EventTechnician = "";
        var next = Postes.NextStart(DateTime.Now);
        EventDate = Postes.Of(next).ShiftDate;
        EventPoste = PosteNames[Postes.Of(next).ShiftId - 1];
        Editor = EditorMode.Event;
    }

    private void EditEvent(MaintenanceEvent e)
    {
        _editingEvent = e;
        FormTitle = "Modifier la maintenance";
        EventMachine = _ctx.NameOf(e.MachineId);
        EventTask = e.Task;
        EventDuration = e.DurationMinutes.ToString(CultureInfo.InvariantCulture);
        EventTechnician = e.Technician ?? "";
        var poste = Postes.Of(e.PlannedStart);
        EventDate = poste.ShiftDate;
        EventPoste = PosteNames[poste.ShiftId - 1];
        Editor = EditorMode.Event;
    }

    [RelayCommand]
    private async Task SaveEventAsync()
    {
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == EventMachine);
        var minutes = (int)ParseNumber(EventDuration);
        if (machine == null || string.IsNullOrWhiteSpace(EventTask) || minutes <= 0)
        {
            StatusMessage = "Ligne, tâche et durée (minutes) sont nécessaires.";
            return;
        }
        var e = _editingEvent ?? new MaintenanceEvent();
        e.MachineId = machine.MachineId;
        e.Task = EventTask.Trim();
        e.DurationMinutes = minutes;
        e.PlannedStart = PosteStart(EventDate, EventPoste);
        e.Technician = string.IsNullOrWhiteSpace(EventTechnician) ? null : EventTechnician.Trim();
        try
        {
            await _ctx.Planning.SaveEventAsync(e);
            Editor = EditorMode.None;
            await GoToAsync(e.PlannedStart, "E" + e.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // ---- Preventive rules ----

    [RelayCommand]
    private void NewPlan() => StartPlanForm(null, "");

    // Also from the Pannes / Statistiques tabs ("Planifier un préventif").
    public void StartPlanForm(string? machineId, string task)
    {
        _editingPlan = null;
        FormTitle = "Nouvelle règle de maintenance préventive";
        PlanMachine = machineId != null ? _ctx.NameOf(machineId) : SelectedMachineName() ?? MachineNames.FirstOrDefault() ?? "";
        PlanTask = task.Length > 0 ? "Contrôle " + task.ToLowerInvariant() : "";
        PlanKindIndex = 0;
        PlanInterval = "14";
        PlanDuration = "60";
        PlanLastDone = DateTime.Today;
        Editor = EditorMode.Plan;
    }

    private void EditPlan(MaintenancePlan p)
    {
        _editingPlan = p;
        FormTitle = "Modifier la règle";
        PlanMachine = _ctx.NameOf(p.MachineId);
        PlanTask = p.Task;
        PlanKindIndex = (int)p.Kind;
        PlanInterval = (p.Kind == MaintenanceKind.Calendar ? p.IntervalDays : p.IntervalBags).ToString(CultureInfo.InvariantCulture);
        PlanDuration = p.DurationMinutes.ToString(CultureInfo.InvariantCulture);
        PlanLastDone = p.LastDoneAt?.Date;
        Editor = EditorMode.Plan;
    }

    [RelayCommand]
    private async Task SavePlanAsync()
    {
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == PlanMachine);
        var interval = ParseNumber(PlanInterval);
        var minutes = (int)ParseNumber(PlanDuration);
        if (machine == null || string.IsNullOrWhiteSpace(PlanTask) || interval <= 0 || minutes <= 0)
        {
            StatusMessage = "Ligne, tâche, intervalle et durée sont nécessaires.";
            return;
        }
        var p = _editingPlan ?? new MaintenancePlan();
        p.MachineId = machine.MachineId;
        p.Task = PlanTask.Trim();
        p.Kind = (MaintenanceKind)PlanKindIndex;
        p.IntervalDays = p.Kind == MaintenanceKind.Calendar ? (int)interval : 0;
        p.IntervalBags = p.Kind == MaintenanceKind.Usage ? (long)interval : 0;
        p.DurationMinutes = minutes;
        p.LastDoneAt = PlanLastDone?.Date.AddHours(6);
        try
        {
            await _ctx.Planning.SavePlanAsync(p);
            Editor = EditorMode.None;
            await RefreshAsync();
            StatusMessage = $"Règle enregistrée : {p.Task} sur {machine.MachineName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    [RelayCommand]
    private async Task DeletePlanAsync()
    {
        if (_editingPlan == null)
        {
            Editor = EditorMode.None;
            return;
        }
        _editingPlan.IsActive = false;
        try
        {
            await _ctx.Planning.SavePlanAsync(_editingPlan);
            Editor = EditorMode.None;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // Puts the rule's next slot on the board: the next poste if it is due
    // (or overdue), else the poste of its due date.
    private async Task PlanFromRuleAsync(MaintenancePlan p, DateTime due)
    {
        var now = DateTime.Now;
        var start = Postes.NextStart(due > now ? due : now);
        var e = new MaintenanceEvent
        {
            PlanId = p.Id, MachineId = p.MachineId, Task = p.Task, PlannedStart = start, DurationMinutes = p.DurationMinutes,
        };
        try
        {
            await _ctx.Planning.SaveEventAsync(e);
            await GoToAsync(start, "E" + e.Id);
            StatusMessage = $"« {p.Task} » planifiée le {When(start)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    private async Task MarkPlanDoneAsync(MaintenancePlan p)
    {
        try
        {
            var now = DateTime.Now;
            p.LastDoneAt = now;
            await _ctx.Planning.SavePlanAsync(p);
            foreach (var e in (await _ctx.Planning.GetOpenEventsAsync()).Where(e => e.PlanId == p.Id))
            {
                e.Status = PlanStatus.Done;
                e.DoneAt = now;
                await _ctx.Planning.SaveEventAsync(e);
            }
            await RefreshAsync();
            StatusMessage = $"« {p.Task} » réalisée : prochaine échéance recalculée.";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    [RelayCommand]
    private void CancelEditor() => Editor = EditorMode.None;

    private async Task GoToAsync(DateTime t, string key)
    {
        WeekStart = MondayOf(t);
        await RefreshAsync();
        SelectedBlock = Sectors.SelectMany(s => s.Rows).SelectMany(r => r.Blocks).FirstOrDefault(b => b.Key == key);
    }
}
