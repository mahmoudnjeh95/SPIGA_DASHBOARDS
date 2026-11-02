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
    PlannedStop,
}

// What the board shows: a day (postes cut in hours), a week (days cut in
// postes) or a month (days). Every period starts at 06:00, with the Matin.
public enum BoardRange
{
    Day,
    Week,
    Month,
}

// One block on a line of the board, placed by fractions of the period shown.
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

    // Shown instead of the text when the block is too narrow for it.
    public string Icon { get; init; } = "";
    public bool ShowIcon => !ShowLabel && Icon.Length > 0;
}

public sealed partial class MachineRow : ObservableObject
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string StateText { get; init; } = "";
    public Brush StateBrush { get; init; } = Brushes.Gray;
    public ObservableCollection<BoardBlock> Blocks { get; } = new();

    // The slot picked on this line (click / drag), or where the open form will put its block.
    [ObservableProperty] private bool hasSlot;
    [ObservableProperty] private double slotStartFrac;
    [ObservableProperty] private double slotEndFrac;
    [ObservableProperty] private string slotText = "";
}

public sealed record SectorGroup(string Name, Brush Color, string Caption, IReadOnlyList<MachineRow> Rows);
// A column of the board (a poste, or a day) and its ticks (hours, postes, or the weekday).
public sealed record ColumnTick(string Label, bool IsFirst);
public sealed record ColumnHeader(string Name, IReadOnlyList<ColumnTick> Ticks, bool IsToday);
public sealed record DetailRow(string Key, string Value);
public sealed record AlertRow(string Machine, string Text, Brush Brush);

public sealed record PreventiveRow(string MachineId, string MachineName, string Task, string Rule, string DueText,
    string StatusText, Brush StatusBrush, bool HasGauge, double Gauge,
    IRelayCommand Plan, IRelayCommand Done, IRelayCommand Edit);

// What a mouse drag on a slot does: move it, or move its start / end edge.
public enum BlockDrag
{
    Move,
    Start,
    End,
}

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

// "Planification" tab: the day, week or month of every line (orders,
// maintenance, planned stops, pannes) with the real progress of each order
// from the counter, the preventive maintenance rules and what is due, and the
// alerts a planner acts on. A click or drag on an empty part of a line picks a
// slot to plan a maintenance or a stop in, as in a calendar.
public partial class BoardViewModel : ObservableObject
{
    public static readonly string[] PosteNames = { "Matin", "Après-midi", "Nuit" };

    // Production orders are not part of this version: their buttons only say so.
    public static readonly bool OrdersEnabled = false;
    public const string OrdersUnavailableText =
        "Cette fonctionnalité n'est pas disponible dans cette version. Contactez le service client pour l'activer.";
    private readonly PlanningContext _ctx;
    private bool _refreshAgain;
    private List<ProductionOrder> _orders = new();
    private List<MaintenanceEvent> _events = new();
    private List<MaintenancePlan> _plans = new();
    private ProductionOrder? _editingOrder;
    private int? _rulePlanId;
    private MaintenanceEvent? _editingEvent;
    private MaintenancePlan? _editingPlan;

    public BoardViewModel(PlanningContext ctx)
    {
        _ctx = ctx;
        RangeStart = StartOf(DateTime.Now, Range);
    }

    public ObservableCollection<ColumnHeader> Columns { get; } = new();
    public ObservableCollection<SectorGroup> Sectors { get; } = new();
    public ObservableCollection<PreventiveRow> Preventives { get; } = new();
    public ObservableCollection<AlertRow> Alerts { get; } = new();
    public ObservableCollection<DetailRow> DetailRows { get; } = new();
    public ObservableCollection<string> MachineNames { get; } = new();
    public List<string> PlanKindOptions { get; } = new() { "Calendaire (jours)", "Usage (sacs produits)" };

    [ObservableProperty] private BoardRange range = BoardRange.Week;
    [ObservableProperty] private DateTime rangeStart;
    [ObservableProperty] private string rangeLabel = "";
    [ObservableProperty] private string periodSuffix = "de la semaine";
    [ObservableProperty] private double nowFrac = -1;
    [ObservableProperty] private bool isNowVisible;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "";

    [ObservableProperty] private bool isUnavailableOpen;

    // KPI strip
    [ObservableProperty] private string stopsText = "0";
    [ObservableProperty] private string stopsSub = "";
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
    [ObservableProperty] private DateTime? orderDate = DateTime.Today;
    [ObservableProperty] private string orderTime = "06:00";
    [ObservableProperty] private DateTime? orderEndDate = DateTime.Today;
    [ObservableProperty] private string orderEndTime = "14:00";
    [ObservableProperty] private string orderEstimate = "";
    [ObservableProperty] private bool canOrderEndFromRate;

    [ObservableProperty] private string eventMachine = "";
    [ObservableProperty] private string eventTask = "";
    [ObservableProperty] private DateTime? eventDate = DateTime.Today;
    [ObservableProperty] private string eventTime = "06:00";
    [ObservableProperty] private DateTime? eventEndDate = DateTime.Today;
    [ObservableProperty] private string eventEndTime = "07:00";
    [ObservableProperty] private string eventLength = "";
    [ObservableProperty] private string eventTechnician = "";

    // The slot form plans a maintenance or a planned stop (Task = its reason).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopForm), nameof(EventTaskLabel))]
    private SlotKind eventKind;
    public bool IsStopForm => EventKind == SlotKind.PlannedStop;
    public string EventTaskLabel => IsStopForm ? "Motif" : "Tâche";
    public IReadOnlyList<string> StopReasons => StopCauses.PlannedStopReasons;

    // Start and end of the order / maintenance forms. The end moves with the
    // start (same length), as in a calendar. An order's end follows the
    // quantity at the nominal rate until one is typed (_orderEndManual).
    // _filling: fields set by code, not typed.
    private bool _filling;
    private bool _orderEndManual;
    private DateTime? _orderStart;
    private DateTime? _eventStart;

    [ObservableProperty] private string planMachine = "";
    [ObservableProperty] private string planTask = "";
    [ObservableProperty] private int planKindIndex;
    [ObservableProperty] private string planInterval = "14";
    [ObservableProperty] private string planDuration = "60";
    [ObservableProperty] private bool canDeletePlan;
    [ObservableProperty] private DateTime? planLastDone = DateTime.Today;
    public string PlanIntervalLabel => PlanKindIndex == 0 ? "Tous les (jours)" : "Tous les (sacs)";

    partial void OnPlanKindIndexChanged(int value) => OnPropertyChanged(nameof(PlanIntervalLabel));
    partial void OnOrderMachineChanged(string value) => UpdateEstimate();
    partial void OnOrderWeightChanged(string value) => UpdateEstimate();
    partial void OnOrderQuantityChanged(string value) => UpdateEstimate();
    partial void OnOrderDateChanged(DateTime? value) => OrderStartMoved();
    partial void OnOrderTimeChanged(string value) => OrderStartMoved();
    partial void OnOrderEndDateChanged(DateTime? value) => OrderEndTyped();
    partial void OnOrderEndTimeChanged(string value) => OrderEndTyped();
    partial void OnEventMachineChanged(string value) => PreviewEvent();
    partial void OnEventDateChanged(DateTime? value) => EventStartMoved();
    partial void OnEventTimeChanged(string value) => EventStartMoved();
    partial void OnEventEndDateChanged(DateTime? value) => PreviewEvent();
    partial void OnEventEndTimeChanged(string value) => PreviewEvent();
    partial void OnSelectedBlockChanged(BoardBlock? oldValue, BoardBlock? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
        {
            newValue.IsSelected = true;
            ClearSlot();
        }
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(ShowEmptyDetail));
        ShowDetails(newValue);
    }
    partial void OnEditorChanged(EditorMode value)
    {
        OnPropertyChanged(nameof(ShowEmptyDetail));
        // A form opens: the message of the last action no longer applies.
        if (value != EditorMode.None)
            StatusMessage = "";
        if (value is EditorMode.None or EditorMode.Plan)
            ClearSlot();
    }

    // Jour / Semaine / Mois segments.
    public string RangeKey
    {
        get => ((int)Range).ToString(CultureInfo.InvariantCulture);
        set => Range = (BoardRange)int.Parse(value, CultureInfo.InvariantCulture);
    }

    partial void OnRangeChanged(BoardRange oldValue, BoardRange newValue)
    {
        OnPropertyChanged(nameof(RangeKey));
        // Stays on now if it was shown, else on the start of what was shown.
        var now = DateTime.Now;
        var anchor = now >= RangeStart && now < EndOf(RangeStart, oldValue) ? now : RangeStart;
        RangeStart = StartOf(anchor, newValue);
        _ = RefreshAsync();
    }

    // A day follows the postes (the Nuit after midnight still belongs to the
    // day before); a week or a month follows the calendar date, so
    // "Aujourd'hui" at night opens the week / month of today.
    private static DateTime StartOf(DateTime t, BoardRange range)
    {
        var day = range == BoardRange.Day ? Postes.Of(t).ShiftDate : t.Date;
        return range switch
        {
            BoardRange.Day => day.AddHours(6),
            BoardRange.Month => new DateTime(day.Year, day.Month, 1).AddHours(6),
            _ => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).AddHours(6),
        };
    }

    private static DateTime EndOf(DateTime start, BoardRange range) => range switch
    {
        BoardRange.Day => start.AddDays(1),
        BoardRange.Month => start.AddMonths(1),
        _ => start.AddDays(7),
    };

    private DateTime RangeEnd => EndOf(RangeStart, Range);
    private double RangeHours => (RangeEnd - RangeStart).TotalHours;
    private double Frac(DateTime t) => Math.Clamp((t - RangeStart).TotalHours / RangeHours, 0, 1);

    public async Task InitializeAsync()
    {
        MachineNames.Clear();
        foreach (var m in _ctx.Machines)
            MachineNames.Add(m.MachineName);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        RangeStart = Step(-1);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        RangeStart = Step(1);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task TodayAsync()
    {
        RangeStart = StartOf(DateTime.Now, Range);
        await RefreshAsync();
    }

    private DateTime Step(int count) => Range switch
    {
        BoardRange.Day => RangeStart.AddDays(count),
        BoardRange.Month => RangeStart.AddMonths(count),
        _ => RangeStart.AddDays(7 * count),
    };

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
        StatusMessage = "Chargement du planning…";
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
        var french = PlanningContext.French;
        var week = ISOWeek.GetWeekOfYear(RangeStart);
        // Short enough to stay whole beside the toolbar.
        (RangeLabel, PeriodSuffix) = Range switch
        {
            BoardRange.Day => (Capitalize(RangeStart.ToString("dddd d MMMM", french)) + $" · S{week}", "du jour"),
            BoardRange.Month => (Capitalize(RangeStart.ToString("MMMM yyyy", french)), "du mois"),
            _ => ($"Semaine {week} · {RangeStart.ToString("d MMM", french)} → {RangeEnd.AddDays(-1).ToString("d MMM", french)}", "de la semaine"),
        };
        NowFrac = (now - RangeStart).TotalHours / RangeHours;
        IsNowVisible = NowFrac is >= 0 and <= 1;

        Columns.Clear();
        foreach (var column in BuildColumns(now))
            Columns.Add(column);

        _orders = await _ctx.Planning.GetOrdersAsync(RangeStart, RangeEnd);
        _events = await _ctx.Planning.GetEventsAsync(RangeStart, RangeEnd);
        _plans = await _ctx.Planning.GetPlansAsync();
        var openEvents = await _ctx.Planning.GetOpenEventsAsync();
        var pannes = (await _ctx.Planning.GetQualificationsAsync(RangeStart, RangeEnd)).Where(q => q.IsPanne).ToList();

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
        foreach (var sector in _ctx.Machines.GroupBy(m => m.Sector))
        {
            var color = PlanningContext.SectorColor(sector.Key);
            var rows = new List<MachineRow>();
            foreach (var m in sector)
            {
                var active = _events.Where(e => e.MachineId == m.MachineId && e.IsActiveAt(now)).ToList();
                var st = states.GetValueOrDefault(m.MachineId);
                var (stateText, stateColor) = active.Any(e => e.IsStop) ? ("Arrêt planifié", Color.FromRgb(0x7F, 0x8C, 0x8D))
                    : active.Count > 0 ? ("En maintenance", Color.FromRgb(0xFA, 0xB2, 0x19))
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
                    var done = produced.GetValueOrDefault(o.Id);
                    var progress = o.QuantityBags > 0 ? Math.Min(1, done / o.QuantityBags) : 0;
                    if (o.Status == PlanStatus.Done)
                        progress = Math.Max(progress, 1);
                    // Labels where there is room: 10 h on a week, 1 h 30 on a day, 2 days on a month.
                    row.Blocks.Add(new BoardBlock
                    {
                        Key = "O" + o.Id, Kind = BlockKind.Order, StartFrac = Frac(o.PlannedStart), EndFrac = Frac(o.PlannedEnd),
                        Label = o.OrderNo, Sub = o.Product, ShowLabel = hours / RangeHours >= 0.06,
                        Tooltip = $"{o.OrderNo} · {o.Product} · {When(o.PlannedStart)} → {When(o.PlannedEnd)}",
                        Fill = PlanningContext.Brush(color, 0x40), Stroke = PlanningContext.Brush(color),
                        HasProgress = o.PlannedStart < now || o.Status == PlanStatus.Done, Progress = progress,
                        Source = o, ProducedBags = done,
                    });
                }
                // The colour says what a slot is: its text is the reason / task and
                // its hours. Short slots keep a minimum width and show an icon.
                foreach (var e in _events.Where(e => e.MachineId == m.MachineId && e.IsStop))
                {
                    var (startFrac, endFrac) = SlotFracs(e);
                    row.Blocks.Add(new BoardBlock
                    {
                        Key = "E" + e.Id, Kind = BlockKind.PlannedStop, StartFrac = startFrac, EndFrac = endFrac,
                        Label = e.Task, Sub = ShortSpan(e.PlannedStart, e.PlannedEnd),
                        ShowLabel = endFrac - startFrac >= 0.036, Icon = PauseIcon,
                        Tooltip = $"Arrêt planifié · {e.Task} · {Span(e.PlannedStart, e.PlannedEnd)} · {PlanningContext.Duration(e.DurationMinutes)}",
                        Source = e,
                    });
                }
                foreach (var e in _events.Where(e => e.MachineId == m.MachineId && !e.IsStop))
                {
                    var kind = e.Status == PlanStatus.Done ? BlockKind.MaintenanceDone
                        : e.PlannedEnd < now ? BlockKind.MaintenanceLate : BlockKind.Maintenance;
                    var (startFrac, endFrac) = SlotFracs(e);
                    row.Blocks.Add(new BoardBlock
                    {
                        Key = "E" + e.Id, Kind = kind, StartFrac = startFrac, EndFrac = endFrac,
                        Label = kind == BlockKind.MaintenanceLate ? "Non validée" : e.Task,
                        Sub = kind == BlockKind.MaintenanceLate ? e.Task : ShortSpan(e.PlannedStart, e.PlannedEnd),
                        ShowLabel = endFrac - startFrac >= 0.036, Icon = kind == BlockKind.MaintenanceLate ? WarningIcon : WrenchIcon,
                        Tooltip = $"Maintenance{(kind == BlockKind.MaintenanceLate ? " non validée" : "")} · {e.Task} · {Span(e.PlannedStart, e.PlannedEnd)} · {PlanningContext.Duration(e.DurationMinutes)}",
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
            Sectors.Add(new SectorGroup(SectorLabel(sector.Key), PlanningContext.Brush(color),
                $"{rows.Count} ligne{(rows.Count > 1 ? "s" : "")}", rows));
        }

        // KPIs
        var running = _orders.Where(o => o.Status == PlanStatus.Planned && o.PlannedStart <= now && o.PlannedEnd > now).ToList();
        var lateOrders = running.Where(o => Behind(o, produced.GetValueOrDefault(o.Id), now) > 0.25).ToList();
        var stops = _events.Where(e => e.IsStop).ToList();
        var maintenances = _events.Where(e => !e.IsStop).ToList();
        StopsText = stops.Count.ToString(CultureInfo.InvariantCulture);
        var stopMinutes = stops.Sum(e => (Clamp(e.PlannedEnd) - Clamp(e.PlannedStart)).TotalMinutes);
        var stopsNow = stops.Count(e => e.IsActiveAt(now));
        StopsSub = $"{PlanningContext.Duration(stopMinutes)} d'arrêt prévu" + (stopsNow > 0 ? $" · {stopsNow} en cours" : "");
        var overduePlans = _plans.Select(p => (p, s: PlanState(p, now, null))).Where(x => x.s.Overdue && !openEvents.Any(e => e.PlanId == x.p.Id)).ToList();
        MaintText = maintenances.Count.ToString(CultureInfo.InvariantCulture);
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
                new RelayCommand(() => PlanFromRule(plan, state.Due)),
                new RelayCommand(() => ConfirmPlanDone(plan)),
                new RelayCommand(() => EditPlan(plan))));
        }
        OnPropertyChanged(nameof(HasPreventives));

        // Alerts
        Alerts.Clear();
        var red = PlanningContext.Brush(Color.FromRgb(0xD0, 0x3B, 0x3B));
        var orange = PlanningContext.Brush(Color.FromRgb(0xEC, 0x83, 0x5A));
        var amber = PlanningContext.Brush(Color.FromRgb(0xFA, 0xB2, 0x19));
        foreach (var (p, s) in overduePlans)
            Alerts.Add(new AlertRow(_ctx.NameOf(p.MachineId), $"Préventif « {p.Task} » : {s.DueText.ToLowerInvariant()}, pas encore planifié.", red));
        foreach (var o in lateOrders)
        {
            var late = Behind(o, produced.GetValueOrDefault(o.Id), now);
            Alerts.Add(new AlertRow(_ctx.NameOf(o.MachineId), $"{o.OrderNo} en retard de {PlanningContext.Duration(late * 60)} au rythme actuel.", orange));
        }
        foreach (var e in maintenances.Where(e => e.Status == PlanStatus.Planned && e.PlannedEnd < now))
            Alerts.Add(new AlertRow(_ctx.NameOf(e.MachineId), $"Maintenance « {e.Task} » du {When(e.PlannedStart)} non validée.", orange));
        foreach (var e in _events.Where(e => e.Status == PlanStatus.Planned))
            foreach (var o in _orders.Where(o => o.MachineId == e.MachineId && o.PlannedStart < e.PlannedEnd && o.PlannedEnd > e.PlannedStart))
                Alerts.Add(new AlertRow(_ctx.NameOf(e.MachineId), $"« {e.Task} » tombe pendant {o.OrderNo} ({When(e.PlannedStart)}).", amber));
        var planned = _orders.Where(o => o.Status == PlanStatus.Planned).OrderBy(o => o.PlannedStart).ToList();
        for (var i = 0; i < planned.Count; i++)
            foreach (var other in planned.Skip(i + 1).Where(x => x.MachineId == planned[i].MachineId && x.PlannedStart < planned[i].PlannedEnd))
                Alerts.Add(new AlertRow(_ctx.NameOf(other.MachineId), $"{planned[i].OrderNo} et {other.OrderNo} se chevauchent ({When(other.PlannedStart)}).", orange));
        OnPropertyChanged(nameof(HasAlerts));

        SelectedBlock = allBlocks.FirstOrDefault(b => b.Key == selectedKey);
        ShowSlot();
    }

    // Columns of the header (and lines behind the blocks): the 3 postes cut in
    // hours for a day, the days cut in postes for a week, the days for a month.
    private IEnumerable<ColumnHeader> BuildColumns(DateTime now)
    {
        if (Range == BoardRange.Day)
        {
            for (var p = 0; p < 3; p++)
            {
                var start = RangeStart.AddHours(8 * p);
                var ticks = Enumerable.Range(0, 8).Select(h => new ColumnTick(start.AddHours(h).ToString("HH"), h == 0)).ToList();
                yield return new ColumnHeader($"{PosteNames[p]} · {start:HH} h – {start.AddHours(8):HH} h", ticks,
                    now >= start && now < start.AddHours(8));
            }
            yield break;
        }
        var today = now.Date;
        var postes = new[] { new ColumnTick("M", true), new ColumnTick("AM", false), new ColumnTick("N", false) };
        for (var d = RangeStart.Date; d < RangeEnd.Date; d = d.AddDays(1))
            yield return Range == BoardRange.Week
                ? new ColumnHeader(PlanningContext.Day(d), postes, d == today)
                : new ColumnHeader(d.Day.ToString(CultureInfo.InvariantCulture),
                    new[] { new ColumnTick(PlanningContext.French.DateTimeFormat.GetShortestDayName(d.DayOfWeek), true) }, d == today);
    }

    private const string WrenchIcon = "\uE90F";
    private const string PauseIcon = "\uE769";
    private const string WarningIcon = "\uE7BA";

    // A slot's place on the board; outside the day view a short one keeps a
    // minimum width (about half a day on a week) so it can be seen and clicked.
    private (double Start, double End) SlotFracs(MaintenanceEvent e)
    {
        var start = Frac(e.PlannedStart);
        var end = Frac(e.PlannedEnd);
        var min = Range == BoardRange.Day ? 0 : 0.02;
        return (start, Math.Min(1, Math.Max(end, start + min)));
    }

    // "08:00 → 19:00", or "Mar 3 08:00 → Mer 4 19:00" over several days.
    private static string ShortSpan(DateTime start, DateTime end)
        => start.Date == end.Date || end == end.Date && end.AddDays(-1).Date == start.Date
            ? $"{start:HH:mm} → {end:HH:mm}"
            : $"{PlanningContext.Day(start)} {start:HH:mm} → {PlanningContext.Day(end)} {end:HH:mm}";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    private DateTime Clamp(DateTime t) => t < RangeStart ? RangeStart : t > RangeEnd ? RangeEnd : t;

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

            case MaintenanceEvent { IsStop: true } e:
                DetailKind = "ARRÊT PLANIFIÉ";
                DetailKindBrush = PlanningContext.Brush(Color.FromRgb(0x7F, 0x8C, 0x8D));
                DetailTitle = e.Task;
                DetailSub = _ctx.NameOf(e.MachineId);
                var grey = PlanningContext.Brush(Color.FromRgb(0x9A, 0xA4, 0xB2));
                (DetailStatus, DetailStatusBrush) = e.Status == PlanStatus.Done || e.PlannedEnd <= now ? ("Terminé", grey)
                    : e.PlannedStart <= now ? ("En cours", amber) : ("Planifié", blue);
                DetailRows.Add(new DetailRow("Début", When(e.PlannedStart)));
                DetailRows.Add(new DetailRow("Fin", When(e.PlannedEnd)));
                DetailRows.Add(new DetailRow("Durée", PlanningContext.Duration(e.DurationMinutes)));
                if (e.DoneAt is { } endedAt)
                    DetailRows.Add(new DetailRow("Terminé le", When(endedAt)));
                DetailNote = "Hors TRS : ce temps n'est pas compté dans le temps requis de la ligne.";
                DetailNoteBrush = grey;
                CanEditSelected = e.Status == PlanStatus.Planned && e.PlannedEnd > now;
                // Ends it now (the line restarts early).
                CanCloseSelected = e.IsActiveAt(now);
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
                DetailRows.Add(new DetailRow("Début prévu", When(e.PlannedStart)));
                DetailRows.Add(new DetailRow("Fin prévue", When(e.PlannedEnd)));
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
                case MaintenanceEvent { IsStop: true } e:
                    // Ended early: only the time really stopped stays out of the TRS.
                    e.Status = PlanStatus.Done;
                    e.DoneAt = now;
                    e.DurationMinutes = Math.Max(1, (int)Math.Round((now - e.PlannedStart).TotalMinutes));
                    await _ctx.Planning.SaveEventAsync(e);
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

    // "Supprimer" in the detail panel: asks first.
    [RelayCommand]
    private void DeleteSelected()
    {
        var what = SelectedBlock?.Source switch
        {
            ProductionOrder o => $"l'ordre {o.OrderNo}",
            MaintenanceEvent { IsStop: true } e => $"l'arrêt planifié « {e.Task} » ({_ctx.NameOf(e.MachineId)}, {Span(e.PlannedStart, e.PlannedEnd)})",
            MaintenanceEvent e => $"la maintenance « {e.Task} » ({_ctx.NameOf(e.MachineId)}, {Span(e.PlannedStart, e.PlannedEnd)})",
            _ => null,
        };
        if (what != null)
            Confirm($"Supprimer {what} ?", "Supprimer", DeleteSelectedNowAsync);
    }

    private async Task DeleteSelectedNowAsync()
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
            StatusMessage = "Supprimé.";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // ---- Order editor ----

    [RelayCommand]
    private void CloseUnavailable() => IsUnavailableOpen = false;

    [RelayCommand]
    private async Task NewOrderAsync()
    {
        if (!OrdersEnabled)
        {
            IsUnavailableOpen = true;
            return;
        }
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
        Editor = EditorMode.Order;
        // One poste by default, then the quantity at the nominal rate once known.
        var next = Postes.NextStart(DateTime.Now);
        FillOrderTimes(next, next.AddHours(8), manualEnd: false);
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
        Editor = EditorMode.Order;
        FillOrderTimes(o.PlannedStart, o.PlannedEnd, manualEnd: true);
    }

    private void FillOrderTimes(DateTime start, DateTime end, bool manualEnd)
    {
        _filling = true;
        OrderDate = start.Date;
        OrderTime = start.ToString("HH:mm");
        OrderEndDate = end.Date;
        OrderEndTime = end.ToString("HH:mm");
        _filling = false;
        _orderStart = start;
        _orderEndManual = manualEnd;
        UpdateEstimate();
    }

    private void SetOrderEnd(DateTime end)
    {
        _filling = true;
        OrderEndDate = end.Date;
        OrderEndTime = end.ToString("HH:mm");
        _filling = false;
    }

    // The end keeps its distance to the start.
    private void OrderStartMoved()
    {
        if (_filling)
            return;
        if (StartAt(OrderDate, OrderTime) is { } start)
        {
            if (_orderStart is { } old && start != old && StartAt(OrderEndDate, OrderEndTime) is { } end)
                SetOrderEnd(end + (start - old));
            _orderStart = start;
        }
        UpdateEstimate();
    }

    private void OrderEndTyped()
    {
        if (_filling)
            return;
        _orderEndManual = true;
        UpdateEstimate();
    }

    // "Fin à la cadence nominale" chip: back to the end computed from the quantity.
    [RelayCommand]
    private void OrderEndFromRate()
    {
        _orderEndManual = false;
        UpdateEstimate();
    }

    private string? SelectedMachineName() => SelectedBlock?.Source switch
    {
        ProductionOrder o => _ctx.NameOf(o.MachineId),
        MaintenanceEvent e => _ctx.NameOf(e.MachineId),
        StopQualification q => _ctx.NameOf(q.MachineId),
        _ => null,
    };

    // "6", "06:30", "14h", "14h30" → that time of the day picked; null if not a time.
    private static DateTime? StartAt(DateTime? date, string time)
    {
        var parts = time.Trim().ToLowerInvariant().Replace('h', ':').Split(':');
        if (parts.Length > 2 || !int.TryParse(parts[0], out var h) || h is < 0 or > 23)
            return null;
        var m = 0;
        if (parts.Length == 2 && parts[1].Length > 0 && (!int.TryParse(parts[1], out m) || m is < 0 or > 59))
            return null;
        return (date ?? DateTime.Today).Date.AddHours(h).AddMinutes(m);
    }

    private static string PosteOf(DateTime t) => PosteNames[Postes.Of(t).ShiftId - 1];

    // M / AM / N chips under the hour of the open form.
    [RelayCommand]
    private void PickPoste(string index)
    {
        var time = int.Parse(index, CultureInfo.InvariantCulture) switch { 1 => "14:00", 2 => "22:00", _ => "06:00" };
        if (Editor == EditorMode.Order)
            OrderTime = time;
        else if (Editor == EditorMode.Event)
            EventTime = time;
    }

    // "Fin de poste" chips: the end of the Matin / Après-midi / Nuit that
    // comes after the start.
    [RelayCommand]
    private void PickPosteEnd(string index)
    {
        var hour = int.Parse(index, CultureInfo.InvariantCulture) switch { 1 => 22, 2 => 6, _ => 14 };
        var start = Editor == EditorMode.Order ? StartAt(OrderDate, OrderTime) : StartAt(EventDate, EventTime);
        if (start is not { } s)
            return;
        var end = s.Date.AddHours(hour);
        if (end <= s)
            end = end.AddDays(1);
        if (Editor == EditorMode.Order)
        {
            OrderEndDate = end.Date;
            OrderEndTime = end.ToString("HH:mm");
        }
        else if (Editor == EditorMode.Event)
        {
            EventEndDate = end.Date;
            EventEndTime = end.ToString("HH:mm");
        }
    }

    private static double ParseNumber(string text)
        => double.TryParse(text.Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Hours the quantity takes at the nominal rate (Parametres); null if either is missing.
    private double? RateHours(Machine? machine, out double rate)
    {
        rate = 0;
        var qty = ParseNumber(OrderQuantity);
        if (machine == null || qty <= 0)
            return null;
        rate = _ctx.Settings.FindNominalRate(machine.MachineId, ParseNumber(OrderWeight));
        return rate > 0 ? qty / rate / 60 : null;
    }

    // What is wrong with a form's start / end, or null.
    private static string? SpanError(DateTime? start, DateTime? end)
        => start == null ? "Heure de début invalide (ex. 06:00, 14h30)."
            : end == null ? "Heure de fin invalide (ex. 22:00)."
            : end <= start ? "La fin doit être après le début."
            : null;

    private void UpdateEstimate()
    {
        if (Editor != EditorMode.Order || _filling)
            return;
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == OrderMachine);
        var rateHours = RateHours(machine, out var rate);
        var start = StartAt(OrderDate, OrderTime);
        if (start is { } s && !_orderEndManual && rateHours is { } h)
            SetOrderEnd(s.AddHours(h));
        var end = StartAt(OrderEndDate, OrderEndTime);
        CanOrderEndFromRate = _orderEndManual && rateHours != null;

        var rateText = rateHours is { } rh ? $"cadence nominale {rate:0.#} sacs/min : {PlanningContext.Duration(rh * 60)}"
            : machine != null && ParseNumber(OrderQuantity) > 0 ? $"pas de cadence nominale pour {machine.MachineName} à {ParseNumber(OrderWeight):0.##} g (Paramètres)"
            : "";
        if (SpanError(start, end) is { } error)
        {
            OrderEstimate = error;
            SetSlot(null, default, default);
            return;
        }
        OrderEstimate = $"Durée {PlanningContext.Duration((end!.Value - start!.Value).TotalMinutes)} · poste {PosteOf(start.Value)} → {PosteOf(end.Value)}"
            + (rateText.Length > 0 ? $" · {rateText}" : "");
        // Shows on the board where the order will go.
        SetSlot(machine?.MachineId, start.Value, end.Value);
    }

    [RelayCommand]
    private async Task SaveOrderAsync()
    {
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == OrderMachine);
        if (machine == null || string.IsNullOrWhiteSpace(OrderProduct))
        {
            StatusMessage = "Ligne et produit sont nécessaires.";
            return;
        }
        var (start, end) = (StartAt(OrderDate, OrderTime), StartAt(OrderEndDate, OrderEndTime));
        if (SpanError(start, end) is { } error)
        {
            StatusMessage = error;
            return;
        }
        var o = _editingOrder ?? new ProductionOrder();
        o.OrderNo = OrderNo.Trim();
        o.MachineId = machine.MachineId;
        o.Product = OrderProduct.Trim();
        o.TargetWeightGrams = ParseNumber(OrderWeight);
        o.QuantityBags = (long)ParseNumber(OrderQuantity);
        o.PlannedStart = start!.Value;
        o.PlannedEnd = end!.Value;
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
    private void NewEvent() => StartEventForm(SlotKind.Maintenance);

    [RelayCommand]
    private void NewStop() => StartEventForm(SlotKind.PlannedStop);

    // A maintenance lasts an hour by default, a planned stop a poste.
    private void StartEventForm(SlotKind kind)
    {
        _editingEvent = null;
        _rulePlanId = null;
        EventKind = kind;
        FormTitle = kind == SlotKind.PlannedStop ? "Planifier un arrêt" : "Planifier une maintenance";
        EventMachine = SelectedMachineName() ?? MachineNames.FirstOrDefault() ?? "";
        EventTask = "";
        EventTechnician = "";
        Editor = EditorMode.Event;
        var next = Postes.NextStart(DateTime.Now);
        FillEventTimes(next, next.AddHours(kind == SlotKind.PlannedStop ? 8 : 1));
    }

    // Reason chips of the planned stop form.
    [RelayCommand]
    private void PickStopReason(string reason) => EventTask = reason;

    private void EditEvent(MaintenanceEvent e)
    {
        _editingEvent = e;
        EventKind = e.Kind;
        FormTitle = e.IsStop ? "Modifier l'arrêt planifié" : "Modifier la maintenance";
        EventMachine = _ctx.NameOf(e.MachineId);
        EventTask = e.Task;
        EventTechnician = e.Technician ?? "";
        Editor = EditorMode.Event;
        FillEventTimes(e.PlannedStart, e.PlannedEnd);
    }

    private void FillEventTimes(DateTime start, DateTime end)
    {
        _filling = true;
        EventDate = start.Date;
        EventTime = start.ToString("HH:mm");
        EventEndDate = end.Date;
        EventEndTime = end.ToString("HH:mm");
        _filling = false;
        _eventStart = start;
        PreviewEvent();
    }

    // The end keeps its distance to the start.
    private void EventStartMoved()
    {
        if (_filling)
            return;
        if (StartAt(EventDate, EventTime) is { } start)
        {
            if (_eventStart is { } old && start != old && StartAt(EventEndDate, EventEndTime) is { } end)
            {
                _filling = true;
                EventEndDate = (end + (start - old)).Date;
                EventEndTime = (end + (start - old)).ToString("HH:mm");
                _filling = false;
            }
            _eventStart = start;
        }
        PreviewEvent();
    }

    // Shows the length, and on the board where the maintenance will go.
    private void PreviewEvent()
    {
        if (Editor != EditorMode.Event || _filling)
            return;
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == EventMachine);
        var (start, end) = (StartAt(EventDate, EventTime), StartAt(EventEndDate, EventEndTime));
        if (SpanError(start, end) is { } error)
        {
            EventLength = error;
            SetSlot(null, default, default);
            return;
        }
        EventLength = $"Durée {PlanningContext.Duration((end!.Value - start!.Value).TotalMinutes)} · poste {PosteOf(start.Value)} → {PosteOf(end.Value)}";
        SetSlot(machine?.MachineId, start.Value, end.Value);
    }

    [RelayCommand]
    private async Task SaveEventAsync()
    {
        var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == EventMachine);
        if (machine == null || string.IsNullOrWhiteSpace(EventTask))
        {
            StatusMessage = IsStopForm ? "Ligne et motif sont nécessaires." : "Ligne et tâche sont nécessaires.";
            return;
        }
        var (start, end) = (StartAt(EventDate, EventTime), StartAt(EventEndDate, EventEndTime));
        if (SpanError(start, end) is { } error)
        {
            StatusMessage = error;
            return;
        }
        var e = _editingEvent ?? new MaintenanceEvent { PlanId = _rulePlanId };
        e.Kind = EventKind;
        e.MachineId = machine.MachineId;
        e.Task = EventTask.Trim();
        e.PlannedStart = start!.Value;
        e.DurationMinutes = (int)Math.Round((end!.Value - start.Value).TotalMinutes);
        e.Technician = IsStopForm || string.IsNullOrWhiteSpace(EventTechnician) ? null : EventTechnician.Trim();
        try
        {
            await _ctx.Planning.SaveEventAsync(e);
            Editor = EditorMode.None;
            await GoToAsync(e.PlannedStart, "E" + e.Id);
            StatusMessage = $"{(e.IsStop ? "Arrêt planifié enregistré" : "Maintenance enregistrée")} : {machine.MachineName} · {Span(e.PlannedStart, e.PlannedEnd)}.";
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
        CanDeletePlan = false;
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
        CanDeletePlan = true;
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
    private void DeletePlan()
    {
        if (_editingPlan == null)
        {
            Editor = EditorMode.None;
            return;
        }
        Confirm($"Supprimer la règle « {_editingPlan.Task} » ({_ctx.NameOf(_editingPlan.MachineId)}) ?", "Supprimer", DeletePlanNowAsync);
    }

    private async Task DeletePlanNowAsync()
    {
        if (_editingPlan == null)
            return;
        _editingPlan.IsActive = false;
        try
        {
            await _ctx.Planning.SavePlanAsync(_editingPlan);
            Editor = EditorMode.None;
            await RefreshAsync();
            StatusMessage = "Règle supprimée.";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // Opens the maintenance form filled from the rule, on the next poste if
    // it is due (or overdue), else the poste of its due date: the planner
    // checks or moves it, then saves.
    private void PlanFromRule(MaintenancePlan p, DateTime due)
    {
        var now = DateTime.Now;
        var start = Postes.NextStart(due > now ? due : now);
        SelectedBlock = null;
        StartEventForm(SlotKind.Maintenance);
        _rulePlanId = p.Id;
        FormTitle = "Planifier le préventif";
        EventMachine = _ctx.NameOf(p.MachineId);
        EventTask = p.Task;
        FillEventTimes(start, start.AddMinutes(p.DurationMinutes));
    }

    private void ConfirmPlanDone(MaintenancePlan p)
        => Confirm($"« {p.Task} » ({_ctx.NameOf(p.MachineId)}) réalisée maintenant ? L'échéance repart d'aujourd'hui.", "Réalisée",
            () => MarkPlanDoneAsync(p));

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

    // ---- Slot picked on the board (as in a calendar) ----

    private string? _slotMachineId;
    private DateTime _slotStart;
    private DateTime _slotEnd;
    private double _slotAnchor;
    private bool _slotTaken;

    [ObservableProperty] private bool isSlotMenuOpen;
    [ObservableProperty] private string slotTitle = "";
    [ObservableProperty] private string slotWhen = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlotWarning))]
    private string slotWarning = "";
    public bool HasSlotWarning => SlotWarning.Length > 0;

    // Closed without picking an action: the slot goes away (back to what the open form shows, if any).
    partial void OnIsSlotMenuOpenChanged(bool value)
    {
        if (value || _slotTaken)
            return;
        if (Editor == EditorMode.Order)
            UpdateEstimate();
        else if (Editor == EditorMode.Event)
            PreviewEvent();
        else
            SetSlot(null, default, default);
    }

    // In hours, what a drag snaps to and what a single click picks:
    // 15 min / 1 h on a day, a poste / a poste on a week, a poste / a day on a month.
    private (double Snap, double Click) SlotUnits => Range switch
    {
        BoardRange.Day => (0.25, 1),
        BoardRange.Month => (8, 24),
        _ => (8, 8),
    };

    private DateTime SnapAt(double frac, double unit, bool up)
    {
        var steps = Math.Clamp(frac, 0, 1) * RangeHours / unit;
        return RangeStart.AddHours((up ? Math.Ceiling(steps) : Math.Floor(steps)) * unit);
    }

    // Mouse down on an empty part of a line (frac = where, 0..1 of the period).
    public void BeginSlot(MachineRow row, double frac)
    {
        IsSlotMenuOpen = false;
        _slotAnchor = Math.Clamp(frac, 0, 0.9999);
        var click = SlotUnits.Click;
        var start = SnapAt(_slotAnchor, click, false);
        SetSlot(row.MachineId, start, start.AddHours(click) > RangeEnd ? RangeEnd : start.AddHours(click));
    }

    public void DragSlot(double frac)
    {
        if (_slotMachineId == null)
            return;
        var snap = SlotUnits.Snap;
        var start = SnapAt(Math.Min(_slotAnchor, frac), snap, false);
        var end = SnapAt(Math.Max(_slotAnchor, frac), snap, true);
        if (end <= start)
            end = start.AddHours(snap);
        SetSlot(_slotMachineId, start, end > RangeEnd ? RangeEnd : end);
    }

    // Mouse up: offers what to plan in the slot.
    public void EndSlot()
    {
        if (_slotMachineId is not { } id)
            return;
        SlotTitle = $"{id} · {_ctx.NameOf(id)}";
        SlotWhen = $"{Span(_slotStart, _slotEnd)} · {PlanningContext.Duration((_slotEnd - _slotStart).TotalMinutes)}";
        var busy = _orders.Where(o => o.MachineId == id && o.Status == PlanStatus.Planned && o.PlannedStart < _slotEnd && o.PlannedEnd > _slotStart)
            .Select(o => o.OrderNo)
            .Concat(_events.Where(e => e.MachineId == id && e.Status == PlanStatus.Planned && e.PlannedStart < _slotEnd && e.PlannedEnd > _slotStart)
                .Select(e => $"« {e.Task} »"))
            .ToList();
        SlotWarning = busy.Count == 0 ? "" : "Déjà occupé : " + string.Join(", ", busy);
        _slotTaken = false;
        IsSlotMenuOpen = true;
    }

    [RelayCommand]
    private void CancelSlot() => IsSlotMenuOpen = false;

    [RelayCommand]
    private async Task OrderInSlotAsync()
    {
        if (_slotMachineId is not { } id)
            return;
        if (!OrdersEnabled)
        {
            IsSlotMenuOpen = false;
            IsUnavailableOpen = true;
            return;
        }
        var (start, end) = (_slotStart, _slotEnd);
        _slotTaken = true;
        IsSlotMenuOpen = false;
        SelectedBlock = null;
        await NewOrderAsync();
        if (Editor != EditorMode.Order)
            return;
        OrderMachine = _ctx.NameOf(id);
        FillOrderTimes(start, end, manualEnd: true);
    }

    [RelayCommand]
    private void EventInSlot() => SlotForm(SlotKind.Maintenance);

    [RelayCommand]
    private void StopInSlot() => SlotForm(SlotKind.PlannedStop);

    private void SlotForm(SlotKind kind)
    {
        if (_slotMachineId is not { } id)
            return;
        var (start, end) = (_slotStart, _slotEnd);
        _slotTaken = true;
        IsSlotMenuOpen = false;
        SelectedBlock = null;
        StartEventForm(kind);
        EventMachine = _ctx.NameOf(id);
        FillEventTimes(start, end);
    }

    private void SetSlot(string? machineId, DateTime start, DateTime end)
    {
        _slotMachineId = machineId;
        _slotStart = start;
        _slotEnd = end;
        ShowSlot();
    }

    // Draws the slot on its line (if it falls in the period shown).
    private void ShowSlot()
    {
        foreach (var row in Sectors.SelectMany(s => s.Rows))
        {
            var on = row.MachineId == _slotMachineId && _slotEnd > RangeStart && _slotStart < RangeEnd;
            row.HasSlot = on;
            if (!on)
                continue;
            row.SlotStartFrac = Frac(_slotStart);
            row.SlotEndFrac = Frac(_slotEnd);
            row.SlotText = ShortSpan(_slotStart, _slotEnd);
        }
    }

    private void ClearSlot()
    {
        IsSlotMenuOpen = false;
        SetSlot(null, default, default);
    }

    // "Jeu 29 · 06:00 → 14:00", or "Jeu 29 · 06:00 → Sam 31 · 06:00"
    private static string Span(DateTime start, DateTime end)
        => start.Date == end.Date ? $"{When(start)} → {end:HH:mm}" : $"{When(start)} → {When(end)}";

    [RelayCommand]
    private void CancelEditor() => Editor = EditorMode.None;

    // ---- Confirmation before what can't be undone (Supprimer, Réalisée) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmOpen))]
    private string confirmText = "";
    [ObservableProperty] private string confirmYesText = "";
    public bool IsConfirmOpen => ConfirmText.Length > 0;
    private Func<Task>? _confirmed;

    private void Confirm(string text, string yes, Func<Task> action)
    {
        _confirmed = action;
        ConfirmYesText = yes;
        ConfirmText = text;
    }

    [RelayCommand]
    private async Task ConfirmYesAsync()
    {
        var action = _confirmed;
        _confirmed = null;
        ConfirmText = "";
        if (action != null)
            await action();
    }

    [RelayCommand]
    private void ConfirmNo()
    {
        _confirmed = null;
        ConfirmText = "";
    }

    // ---- Moving / stretching a slot with the mouse (BoardView) ----

    private MaintenanceEvent? _dragged;
    private DateTime _dragStart;
    private DateTime _dragEnd;

    // Planned slots only (done ones are history), and not while a form is open.
    public bool CanDrag(BoardBlock block) => Editor == EditorMode.None && block.Source is MaintenanceEvent { Status: PlanStatus.Planned };

    // deltaFrac: how far the mouse went, as a part of the period shown. The
    // new start / end snap like a picked slot (15 min on a day, a poste on a
    // week or a month); the outline shows where it will go.
    public void DragBlock(BoardBlock block, BlockDrag mode, double deltaFrac)
    {
        if (!CanDrag(block) || block.Source is not MaintenanceEvent e)
            return;
        _dragged = e;
        var delta = TimeSpan.FromHours(deltaFrac * RangeHours);
        var snap = SlotUnits.Snap;
        DateTime Snap(DateTime t) => RangeStart.AddHours(Math.Round((t - RangeStart).TotalHours / snap) * snap);
        var length = e.PlannedEnd - e.PlannedStart;
        var (start, end) = mode switch
        {
            BlockDrag.Start => (Snap(e.PlannedStart + delta), e.PlannedEnd),
            BlockDrag.End => (e.PlannedStart, Snap(e.PlannedEnd + delta)),
            _ => (Snap(e.PlannedStart + delta), Snap(e.PlannedStart + delta) + length),
        };
        // Never shorter than one step.
        if (end <= start)
            (start, end) = mode == BlockDrag.Start ? (end.AddHours(-snap), end) : (start, start.AddHours(snap));
        (_dragStart, _dragEnd) = (start, end);
        SetSlot(e.MachineId, start, end);
    }

    public void CancelDrag()
    {
        _dragged = null;
        SetSlot(null, default, default);
    }

    // Mouse up: saves the new place, if it changed.
    public async Task EndDragAsync()
    {
        if (_dragged is not { } e)
            return;
        CancelDrag();
        if (_dragStart == e.PlannedStart && _dragEnd == e.PlannedEnd)
            return;
        var (oldStart, oldMinutes) = (e.PlannedStart, e.DurationMinutes);
        e.PlannedStart = _dragStart;
        e.DurationMinutes = (int)Math.Round((_dragEnd - _dragStart).TotalMinutes);
        try
        {
            await _ctx.Planning.SaveEventAsync(e);
            await RefreshAsync();
            SelectedBlock = Sectors.SelectMany(s => s.Rows).SelectMany(r => r.Blocks).FirstOrDefault(b => b.Key == "E" + e.Id);
            StatusMessage = $"« {e.Task} » replanifié : {Span(e.PlannedStart, e.PlannedEnd)} ({PlanningContext.Duration(e.DurationMinutes)}).";
        }
        catch (Exception ex)
        {
            (e.PlannedStart, e.DurationMinutes) = (oldStart, oldMinutes);
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    private async Task GoToAsync(DateTime t, string key)
    {
        RangeStart = StartOf(t, Range);
        await RefreshAsync();
        SelectedBlock = Sectors.SelectMany(s => s.Rows).SelectMany(r => r.Blocks).FirstOrDefault(b => b.Key == key);
    }
}
