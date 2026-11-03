using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Planning;

// One line of the "A qualifier" list: a stop detected from the counter
// (StopDetector), or a panne declared by hand, with the cause given to it.
public partial class StopRow : ObservableObject
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public DateTime ShiftDate { get; init; }
    public int ShiftId { get; init; }
    public string Team { get; init; } = "";
    public double Minutes { get; init; }
    public bool IsNoData { get; init; }
    public bool IsLive { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCause), nameof(CauseLabel), nameof(CauseBrush), nameof(IsManual))]
    private StopQualification? qualification;

    [ObservableProperty] private bool isSelected;

    // Ticked to be qualified together with others.
    [ObservableProperty] private bool isChecked;

    public bool HasCause => Qualification != null;
    public bool IsManual => Qualification?.IsManual == true;
    // Qualified by itself from a slot of the calendar (PannesViewModel.AutoComment).
    public bool IsAuto => Qualification?.Comment?.StartsWith(PannesViewModel.AutoComment, StringComparison.Ordinal) == true;
    public string CauseLabel => Qualification == null ? (IsNoData ? "Sans données" : "Sans cause")
        : StopCauses.Label(Qualification.CauseCode) + (string.IsNullOrEmpty(Qualification.Component) ? "" : " · " + Qualification.Component)
          + (IsAuto ? " · auto" : "");
    public Brush CauseBrush => Qualification?.Cause is { } cause
        ? PlanningContext.Brush(PlanningContext.FamilyColor(cause.Family))
        : PlanningContext.Brush(Color.FromRgb(0x6B, 0x74, 0x80));

    public string DayText => PlanningContext.Day(Start);
    public string FromText => Start.ToString("HH:mm");
    public string ToText => IsLive ? "maintenant" : End.ToString("HH:mm");
    public string PosteText => KpiCalculator.ShiftName(ShiftId) + " · " + Team;
    public string DurationText => PlanningContext.Duration(Minutes);
}

// Note: shown under the family name ("hors TRS" for the planned causes).
public sealed record CauseOptionGroup(string Name, Brush Color, IReadOnlyList<CauseOption> Causes, string Note = "")
{
    public bool HasNote => Note.Length > 0;
}

public partial class CauseOption : ObservableObject
{
    public string Code { get; init; } = "";
    public string Label { get; init; } = "";
    [ObservableProperty] private bool isSelected;
    public IRelayCommand? Pick { get; set; }
}

// "Pannes" tab: the stops of the period that still need a cause, the form
// that gives one (to one stop, or to all the ticked ones), and hand-declared
// pannes (the counter missed it). Stops inside a planned stop / maintenance
// of the calendar are qualified by themselves.
public partial class PannesViewModel : ObservableObject
{
    private const string AllMachines = "Toutes les lignes";
    private const string AllTeams = "Toutes les équipes";

    // Start of the comment of a cause given from the calendar.
    public const string AutoComment = "Auto (calendrier) : ";
    private readonly PlanningContext _ctx;
    private bool _refreshAgain;
    private List<StopQualification> _recent30 = new();

    public event Action<string, string>? PreventiveRequested;

    public PannesViewModel(PlanningContext ctx)
    {
        _ctx = ctx;
        CauseGroups = Enum.GetValues<CauseFamily>().Select(f => new CauseOptionGroup(
            StopCauses.FamilyLabel(f),
            PlanningContext.Brush(PlanningContext.FamilyColor(f)),
            StopCauses.All.Where(c => c.Family == f).Select(c =>
            {
                var option = new CauseOption { Code = c.Code, Label = c.Label };
                option.Pick = new RelayCommand(() => SelectedCauseCode = c.Code);
                return option;
            }).ToList(),
            // Planned stops are left out of the TRS (ProductionRepository).
            f == CauseFamily.Planifie ? "hors TRS" : "")).ToList();
    }

    public List<string> PeriodOptions { get; } = new() { "Aujourd'hui", "7 jours", "30 jours" };
    public ObservableCollection<string> MachineOptions { get; } = new() { AllMachines };
    public ObservableCollection<string> TeamOptions { get; } = new() { AllTeams };
    public ObservableCollection<StopRow> Rows { get; } = new();
    public ObservableCollection<StopRow> Recent { get; } = new();
    public IReadOnlyList<CauseOptionGroup> CauseGroups { get; }
    public ObservableCollection<string> ComponentSuggestions { get; } = new();
    public ObservableCollection<string> TechnicianSuggestions { get; } = new();

    [ObservableProperty] private int periodIndex = 1;
    [ObservableProperty] private string selectedMachine = AllMachines;
    // Team that worked the poste of the stop (Parametres > Équipes).
    [ObservableProperty] private string selectedTeam = AllTeams;
    [ObservableProperty] private bool onlyUnqualified = true;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "";

    [ObservableProperty] private string bannerTitle = "";
    [ObservableProperty] private string bannerDetail = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBannerMicro))]
    private string bannerMicro = "";
    public bool HasBannerMicro => BannerMicro.Length > 0;

    // What is left to qualify, for the Statistiques tab ("" when nothing).
    [ObservableProperty] private string openSummary = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualificationRatio))]
    private double qualificationRate;
    public double QualificationRatio => QualificationRate / 100;

    // Form
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(FormTitle), nameof(FormSubtitle), nameof(CanDelete))]
    private StopRow? selectedRow;
    [ObservableProperty] private bool isManualMode;
    [ObservableProperty] private string? selectedCauseCode;
    [ObservableProperty] private string component = "";
    [ObservableProperty] private string technician = "";
    [ObservableProperty] private string comment = "";
    [ObservableProperty] private string repeatWarning = "";
    [ObservableProperty] private string manualMachine = "";
    [ObservableProperty] private DateTime? manualDate = DateTime.Today;
    [ObservableProperty] private string manualFrom = "";
    [ObservableProperty] private string manualTo = "";

    // Where the stop sits in its poste (0..1), for the little timeline.
    [ObservableProperty] private double posteStartFrac;
    [ObservableProperty] private double posteEndFrac;
    [ObservableProperty] private string posteLabels = "";

    // Ticked stops: the form then gives its cause to all of them.
    public List<StopRow> CheckedRows => Rows.Where(r => r.IsChecked).ToList();
    public int CheckedCount => Rows.Count(r => r.IsChecked);
    public bool IsBulk => CheckedCount > 0;
    public bool ShowTimeline => !IsManualMode && !IsBulk;
    public bool HasSelection => SelectedRow != null || IsManualMode || IsBulk;
    public bool CanDelete => !IsBulk && SelectedRow?.Qualification != null;
    public string FormTitle => IsBulk ? $"{CheckedCount} arrêt{(CheckedCount > 1 ? "s" : "")} coché{(CheckedCount > 1 ? "s" : "")} · {PlanningContext.Duration(CheckedRows.Sum(r => r.Minutes))}"
        : IsManualMode ? "Déclarer une panne"
        : SelectedRow == null ? "" : $"{SelectedRow.MachineName} · arrêt de {SelectedRow.DurationText}";
    public string FormSubtitle => IsBulk ? "La cause choisie sera donnée à chacun des arrêts cochés."
        : IsManualMode ? "Pour un arrêt que le compteur n'a pas vu (machine en marche à vide, etc.)"
        : SelectedRow == null ? "" : $"{SelectedRow.DayText} · {SelectedRow.FromText} → {SelectedRow.ToText} · {SelectedRow.PosteText}";
    public bool HasRepeatWarning => RepeatWarning.Length > 0;
    public string SaveText => (IsBulk ? $"Enregistrer les {CheckedCount}" : "Enregistrer")
        + (StopCauses.Find(SelectedCauseCode) is { } c ? " · " + c.Label : "");

    partial void OnPeriodIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PeriodKey));
        _ = RefreshAsync();
    }

    // The period buttons bind to this (two-way), whatever checks them: mouse, keyboard, UI Automation.
    public string PeriodKey
    {
        get => PeriodIndex.ToString(CultureInfo.InvariantCulture);
        set => PeriodIndex = int.Parse(value, CultureInfo.InvariantCulture);
    }
    partial void OnSelectedMachineChanged(string value) => _ = RefreshAsync();
    partial void OnSelectedTeamChanged(string value) => _ = RefreshAsync();
    partial void OnOnlyUnqualifiedChanged(bool value) => _ = RefreshAsync();
    partial void OnRepeatWarningChanged(string value) => OnPropertyChanged(nameof(HasRepeatWarning));
    partial void OnComponentChanged(string value) => UpdateRepeatWarning();
    partial void OnIsManualModeChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSubtitle));
        OnPropertyChanged(nameof(ShowTimeline));
    }

    // A box ticked / unticked: the form switches between one stop and the ticked ones.
    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StopRow.IsChecked))
            return;
        var count = CheckedCount;
        if (count == 1 && sender is StopRow { IsChecked: true })
        {
            IsManualMode = false;
            ClearForm();
        }
        else if (count == 0 && !IsManualMode)
        {
            LoadRow(SelectedRow);
        }
        NotifyChecks();
    }

    private void NotifyChecks()
    {
        foreach (var name in new[] { nameof(CheckedCount), nameof(IsBulk), nameof(ShowTimeline), nameof(HasSelection),
                     nameof(CanDelete), nameof(FormTitle), nameof(FormSubtitle), nameof(SaveText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void CheckAll()
    {
        foreach (var row in Rows.Where(r => !r.HasCause && !r.IsLive))
            row.IsChecked = true;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var row in Rows.Where(r => r.IsChecked))
            row.IsChecked = false;
    }

    partial void OnSelectedCauseCodeChanged(string? value)
    {
        foreach (var option in CauseGroups.SelectMany(g => g.Causes))
            option.IsSelected = option.Code == value;
        OnPropertyChanged(nameof(SaveText));
        UpdateRepeatWarning();
    }

    partial void OnSelectedRowChanged(StopRow? oldValue, StopRow? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue == null)
            return;
        newValue.IsSelected = true;
        IsManualMode = false;
        if (!IsBulk)
            LoadRow(newValue);
    }

    // The form shows the stop's cause (none yet: empty, or "Pas de données").
    private void LoadRow(StopRow? newValue)
    {
        if (newValue == null)
        {
            ClearForm();
            return;
        }
        var q = newValue.Qualification;
        SelectedCauseCode = q?.CauseCode ?? (newValue.IsNoData ? "NODATA" : null);
        Component = q?.Component ?? "";
        Technician = q?.Technician ?? "";
        Comment = q?.Comment ?? "";

        var posteStart = Postes.Of(newValue.Start).Start;
        PosteStartFrac = Math.Clamp((newValue.Start - posteStart).TotalMinutes / 480, 0, 1);
        PosteEndFrac = Math.Clamp((newValue.End - posteStart).TotalMinutes / 480, PosteStartFrac + 0.006, 1);
        PosteLabels = string.Join("|", Enumerable.Range(0, 5).Select(i => posteStart.AddHours(2 * i).ToString("HH:mm")));
        OnPropertyChanged(nameof(PosteTicks));
    }

    public IReadOnlyList<string> PosteTicks => PosteLabels.Length == 0 ? Array.Empty<string>() : PosteLabels.Split('|');

    public async Task InitializeAsync()
    {
        foreach (var m in _ctx.Machines)
            MachineOptions.Add(m.MachineName);
        ManualMachine = _ctx.Machines.FirstOrDefault()?.MachineName ?? "";
        foreach (var team in _ctx.Teams.TeamNames)
            TeamOptions.Add(team);
        await RefreshAsync();
    }

    private (DateTime From, DateTime To) Range()
    {
        var now = DateTime.Now;
        var today = Postes.Of(now).ShiftDate;
        var days = PeriodIndex switch { 0 => 0, 1 => 6, _ => 29 };
        return (today.AddDays(-days).AddHours(6), now);
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
        StatusMessage = "Recherche des arrêts…";
        try
        {
            var (from, to) = Range();
            var machines = SelectedMachine == AllMachines
                ? _ctx.Machines
                : _ctx.Machines.Where(m => m.MachineName == SelectedMachine).ToList();
            var names = _ctx.Machines.ToDictionary(m => m.MachineId, m => m.MachineName);

            var segments = await _ctx.Production.GetLineSegmentsAsync(machines.Select(m => m.MachineId).ToList(), from, to);
            var stops = StopDetector.Detect(segments, from, to, _ctx.Settings, _ctx.Teams, names);
            // Micro-stops: under the threshold, so not listed; counted for the banner.
            var threshold = Math.Max(1, _ctx.Settings.StopThresholdMinutes);
            var micro = threshold <= 1 ? new List<ProductionStop>()
                : StopDetector.Detect(segments, from, to, _ctx.Settings, _ctx.Teams, names, 1).Where(s => s.NetMinutes < threshold).ToList();
            var quals = await _ctx.Planning.GetQualificationsAsync(from.AddHours(-1), to.AddHours(1));
            _recent30 = await _ctx.Planning.GetQualificationsAsync(DateTime.Now.AddDays(-30), DateTime.Now.AddDays(1));
            var ids = machines.Select(m => m.MachineId).ToHashSet();
            quals = quals.Where(q => ids.Contains(q.MachineId)).ToList();

            var used = new HashSet<int>();
            var rows = new List<StopRow>();
            foreach (var s in stops)
            {
                var match = quals.FirstOrDefault(q => !used.Contains(q.Id) && q.MachineId == s.MachineId
                    && Math.Abs((q.StopStart - s.Start).TotalMinutes) <= 3);
                if (match != null)
                    used.Add(match.Id);
                rows.Add(new StopRow
                {
                    MachineId = s.MachineId, MachineName = s.MachineName, Start = s.Start, End = s.End,
                    ShiftDate = s.ShiftDate, ShiftId = s.ShiftId, Team = s.Team, Minutes = s.NetMinutes,
                    IsNoData = s.IsMostlyNoData, IsLive = (DateTime.Now - s.End).TotalMinutes < 3,
                    Qualification = match,
                });
            }
            foreach (var q in quals.Where(q => !used.Contains(q.Id)))
            {
                rows.Add(new StopRow
                {
                    MachineId = q.MachineId, MachineName = _ctx.NameOf(q.MachineId), Start = q.StopStart, End = q.StopEnd,
                    ShiftDate = q.ShiftDate, ShiftId = q.ShiftId, Team = q.Team, Minutes = q.Minutes, Qualification = q,
                });
            }

            // Every team's stops get their calendar cause; the list and the banner show the chosen team's.
            var auto = await QualifyFromCalendarAsync(rows, from, to);
            if (SelectedTeam != AllTeams)
            {
                rows = rows.Where(r => r.Team == SelectedTeam).ToList();
                micro = micro.Where(s => s.Team == SelectedTeam).ToList();
            }

            var detected = rows.Where(r => !r.IsManual && !r.IsLive).ToList();
            var open = rows.Where(r => !r.HasCause).ToList();
            QualificationRate = detected.Count == 0 ? 100 : Math.Round(100.0 * detected.Count(r => r.HasCause) / detected.Count);
            BannerTitle = open.Count == 0
                ? "Tous les arrêts ont une cause"
                : $"{open.Count} arrêt{(open.Count > 1 ? "s" : "")} sans cause · {PlanningContext.Duration(open.Sum(r => r.Minutes))}";
            var autoCount = rows.Count(r => r.IsAuto);
            BannerDetail = $"Seuil de détection : {threshold} min (Paramètres) · pauses planifiées déjà retirées"
                + (autoCount > 0 ? $" · {autoCount} arrêt{(autoCount > 1 ? "s" : "")} qualifié{(autoCount > 1 ? "s" : "")} par le calendrier" : "");
            BannerMicro = micro.Count == 0 ? ""
                : $"+ {micro.Count} micro-arrêt{(micro.Count > 1 ? "s" : "")} de moins de {threshold} min ({PlanningContext.Duration(micro.Sum(s => s.NetMinutes))}), non listés";
            OpenSummary = open.Count == 0 ? ""
                : $"{BannerTitle} ({PeriodOptions[PeriodIndex].ToLowerInvariant()}{(SelectedTeam != AllTeams ? ", " + SelectedTeam : "")}) : les statistiques ne comptent que les arrêts qualifiés.";
            if (auto > 0)
                AppLogger.Log($"Pannes : {auto} arrêt(s) qualifié(s) par le calendrier");

            var previous = SelectedRow;
            foreach (var row in Rows)
                row.PropertyChanged -= OnRowPropertyChanged;
            Rows.Clear();
            foreach (var row in rows.Where(r => !OnlyUnqualified || !r.HasCause).OrderByDescending(r => r.Start))
            {
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }
            NotifyChecks();
            Recent.Clear();
            foreach (var row in rows.Where(r => r.HasCause).OrderByDescending(r => r.Qualification!.CreatedAt).Take(8))
                Recent.Add(row);

            SelectedRow = Rows.FirstOrDefault(r => previous != null && r.MachineId == previous.MachineId && r.Start == previous.Start)
                ?? Rows.FirstOrDefault();
            if (SelectedRow == null && !IsManualMode)
                ClearForm();
            await LoadSuggestionsAsync();
            StatusMessage = rows.Count == 0 ? "Aucun arrêt sur cette période." : "";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - pannes : {ex}");
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

    // A detected stop lying (80 % or more) inside a planned stop or a
    // maintenance of the calendar gets its cause by itself: "Pas de
    // production prévue" / "Maintenance préventive", saved like a typed one so
    // the Arrêts window and the statistics see it. Stops still running wait
    // for their end. Returns how many were qualified.
    private async Task<int> QualifyFromCalendarAsync(List<StopRow> rows, DateTime from, DateTime to)
    {
        var open = rows.Where(r => !r.HasCause && !r.IsLive && !r.IsManual).ToList();
        if (open.Count == 0)
            return 0;
        var slots = await _ctx.Planning.GetEventsAsync(from, to);
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
                MachineId = row.MachineId, StopStart = row.Start, StopEnd = row.End, ShiftDate = row.ShiftDate,
                ShiftId = row.ShiftId, Team = row.Team, CauseCode = slot.IsStop ? "NOPLAN" : "PREV",
                Technician = slot.Technician, Comment = AutoComment + slot.Task,
            };
            await _ctx.Planning.SaveQualificationAsync(q);
            row.Qualification = q;
            count++;
        }
        return count;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private async Task LoadSuggestionsAsync()
    {
        var components = await _ctx.Planning.GetDistinctAsync("Component");
        ComponentSuggestions.Clear();
        foreach (var c in components.Concat(StopCauses.DefaultComponents).Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
            ComponentSuggestions.Add(c);
        var technicians = await _ctx.Planning.GetDistinctAsync("Technician");
        TechnicianSuggestions.Clear();
        foreach (var t in technicians.Concat(new[] { "Maintenance", "Opérateur de ligne", "Prestataire externe" })
                     .Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            TechnicianSuggestions.Add(t);
    }

    [RelayCommand]
    private void PickComponent(string value) => Component = value;

    [RelayCommand]
    private void PickTechnician(string value) => Technician = value;

    [RelayCommand]
    private void SelectRow(StopRow row) => SelectedRow = row;

    [RelayCommand]
    private void StartManual()
    {
        ClearChecks();
        SelectedRow = null;
        ClearForm();
        IsManualMode = true;
        ManualDate = DateTime.Today;
        ManualFrom = DateTime.Now.AddHours(-1).ToString("HH:mm");
        ManualTo = DateTime.Now.ToString("HH:mm");
        SelectedCauseCode = "MECA";
    }

    [RelayCommand]
    private void CancelForm()
    {
        IsManualMode = false;
        SelectedRow = Rows.FirstOrDefault();
        if (SelectedRow == null)
            ClearForm();
    }

    private void ClearForm()
    {
        SelectedCauseCode = null;
        Component = Technician = Comment = "";
        RepeatWarning = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (StopCauses.Find(SelectedCauseCode) == null)
        {
            StatusMessage = "Choisissez une cause.";
            return;
        }

        if (IsBulk)
        {
            await SaveCheckedAsync();
            return;
        }

        StopQualification q;
        if (IsManualMode)
        {
            var machine = _ctx.Machines.FirstOrDefault(m => m.MachineName == ManualMachine);
            if (machine == null || ManualDate is not { } date
                || !TimeSpan.TryParse(ManualFrom, CultureInfo.InvariantCulture, out var fromTime)
                || !TimeSpan.TryParse(ManualTo, CultureInfo.InvariantCulture, out var toTime))
            {
                StatusMessage = "Ligne, date et heures (HH:mm) sont nécessaires.";
                return;
            }
            var start = date.Date + fromTime;
            var end = date.Date + toTime;
            if (end <= start)
                end = end.AddDays(1);
            var poste = Postes.Of(start);
            q = new StopQualification
            {
                MachineId = machine.MachineId, StopStart = start, StopEnd = end, ShiftDate = poste.ShiftDate,
                ShiftId = poste.ShiftId, Team = _ctx.TeamOf(poste.ShiftDate, poste.ShiftId), IsManual = true,
            };
        }
        else if (SelectedRow is { } row)
        {
            q = row.Qualification ?? new StopQualification
            {
                MachineId = row.MachineId, StopStart = row.Start, StopEnd = row.End, ShiftDate = row.ShiftDate,
                ShiftId = row.ShiftId, Team = row.Team,
            };
            // A stop still running when qualified: keep its latest end.
            q.StopEnd = row.End;
        }
        else
        {
            return;
        }

        q.CauseCode = SelectedCauseCode!;
        q.Component = string.IsNullOrWhiteSpace(Component) ? null : Component.Trim();
        q.Technician = string.IsNullOrWhiteSpace(Technician) ? null : Technician.Trim();
        q.Comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();

        try
        {
            await _ctx.Planning.SaveQualificationAsync(q);
            AppLogger.Log($"Panne qualifiée : {q.MachineId} {q.StopStart:g} {q.CauseCode} {q.Component}");
            var next = NextOpenRow();
            IsManualMode = false;
            if (SelectedRow != null)
                SelectedRow.Qualification = q;
            await RefreshAsync();
            if (OnlyUnqualified && next != null)
                SelectedRow = Rows.FirstOrDefault(r => r.MachineId == next.MachineId && r.Start == next.Start) ?? Rows.FirstOrDefault();
            StatusMessage = $"Enregistré : {StopCauses.Label(q.CauseCode)} sur {_ctx.NameOf(q.MachineId)}.";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - enregistrement panne : {ex}");
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // The cause of the form given to every ticked stop.
    private async Task SaveCheckedAsync()
    {
        var rows = CheckedRows;
        try
        {
            foreach (var row in rows)
            {
                var q = row.Qualification ?? new StopQualification
                {
                    MachineId = row.MachineId, StopStart = row.Start, StopEnd = row.End, ShiftDate = row.ShiftDate,
                    ShiftId = row.ShiftId, Team = row.Team,
                };
                q.StopEnd = row.End;
                q.CauseCode = SelectedCauseCode!;
                q.Component = string.IsNullOrWhiteSpace(Component) ? null : Component.Trim();
                q.Technician = string.IsNullOrWhiteSpace(Technician) ? null : Technician.Trim();
                q.Comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();
                await _ctx.Planning.SaveQualificationAsync(q);
            }
            AppLogger.Log($"Pannes qualifiées en lot : {rows.Count} x {SelectedCauseCode}");
            var cause = StopCauses.Label(SelectedCauseCode);
            await RefreshAsync();
            StatusMessage = $"Enregistré : {cause} sur {rows.Count} arrêt{(rows.Count > 1 ? "s" : "")}.";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - enregistrement en lot : {ex}");
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    private StopRow? NextOpenRow()
    {
        if (SelectedRow == null)
            return null;
        var index = Rows.IndexOf(SelectedRow);
        return Rows.Skip(index + 1).FirstOrDefault(r => !r.HasCause) ?? Rows.Take(index).FirstOrDefault(r => !r.HasCause);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedRow?.Qualification is not { } q)
            return;
        try
        {
            await _ctx.Planning.DeleteQualificationAsync(q.Id);
            SelectedRow.Qualification = null;
            await RefreshAsync();
            StatusMessage = "Cause retirée.";
        }
        catch (Exception ex)
        {
            StatusMessage = PlanningErrors.Describe(ex);
        }
    }

    // Same component failing again on the same line: a preventive is due.
    private void UpdateRepeatWarning()
    {
        var machineId = IsManualMode ? _ctx.Machines.FirstOrDefault(m => m.MachineName == ManualMachine)?.MachineId : SelectedRow?.MachineId;
        if (machineId == null || string.IsNullOrWhiteSpace(Component) || StopCauses.Find(SelectedCauseCode)?.Family != CauseFamily.Panne)
        {
            RepeatWarning = "";
            return;
        }
        var currentId = SelectedRow?.Qualification?.Id ?? 0;
        var previous = _recent30.Count(q => q.Id != currentId && q.MachineId == machineId && q.IsPanne
            && string.Equals(q.Component, Component.Trim(), StringComparison.OrdinalIgnoreCase));
        RepeatWarning = previous >= 2
            ? $"« {Component.Trim()} » sur {_ctx.NameOf(machineId)} : {previous + 1}e panne en 30 jours. Un préventif sur ce composant est conseillé."
            : "";
    }

    [RelayCommand]
    private void PlanPreventive()
    {
        var machineId = SelectedRow?.MachineId ?? _ctx.Machines.FirstOrDefault(m => m.MachineName == ManualMachine)?.MachineId;
        if (machineId != null)
            PreventiveRequested?.Invoke(machineId, Component.Trim());
    }
}

public static class PlanningErrors
{
    // The Plan* tables are created by the app: a SQL login without CREATE
    // TABLE rights needs database/planning.sql run once by an administrator.
    public static string Describe(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            return "Les tables de planification n'ont pas pu être créées (droits SQL). Faites exécuter database\\planning.sql une fois sur la base, puis rouvrez cette fenêtre.";
        return "Erreur : " + message;
    }
}
