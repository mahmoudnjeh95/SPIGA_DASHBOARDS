using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels.Slides;

namespace SpigaDashboards.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan NoteRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(30);

    private readonly ProductionRepository _repository;
    private readonly AppSettings _settings;
    private readonly TeamScheduleService _teamSchedule;
    private readonly SharedSettingsSync _sharedSettings;
    // Shared settings version this deck was built with: another PC's save
    // (or this deck's twin pulling it first) rebuilds it from scratch.
    private int _sharedVersionBuilt;
    private readonly DispatcherTimer _rotationTimer;
    private readonly DispatcherTimer _noteTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _healthCheckTimer;

    private List<SlideDescriptor> _slides = new();
    private Dictionary<string, Machine> _machinesById = new();
    private int _currentIndex;

    // True while the deck is stopped after a failure (timers off, reconnect
    // timer running): only a successful InitializeAsync resumes it, so a slide
    // that happens to load in between can't flag the base healthy and stop
    // the reconnect timer while rotation stays frozen.
    private bool _suspended = true;

    // Set by Stop(): a load still in flight must not restart any timer.
    private bool _stopped;

    [ObservableProperty] private object? currentSlide;
    [ObservableProperty] private string noteText = "";
    [ObservableProperty] private DashboardPeriod selectedPeriod = DashboardPeriod.Day;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    private bool isConnectionHealthy = true;

    // Data being read (deck, slide): the loading motion shows if it lasts
    // (Views/PastaLoader), never over the disconnected overlay. Counted, as
    // loads nest (InitializeAsync shows the first slide).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    private bool isLoading;
    private int _loads;
    public bool ShowLoading => IsLoading && IsConnectionHealthy;

    private void StartLoad()
    {
        _loads++;
        IsLoading = true;
    }

    private void EndLoad()
    {
        _loads = Math.Max(0, _loads - 1);
        IsLoading = _loads > 0;
    }
    [ObservableProperty] private string connectionErrorMessage = "";
    [ObservableProperty] private bool isEditingNote;
    [ObservableProperty] private string noteSaveStatus = "";
    [ObservableProperty] private string currentTeamLabel = "";
    [ObservableProperty] private bool isRotationPaused;
    [ObservableProperty] private bool isReconnecting;
    [ObservableProperty] private string reconnectStatus = "";

    // The consignes PDF gets the screen to itself: no team/poste badge over it.
    [ObservableProperty] private bool isPdfSlideShown;

    // Motion only (Views/SlideMotion): SlideChangeCount moves when another
    // slide comes up (not on a data refresh of the same one), RotationCycle
    // whenever the auto-advance countdown starts over.
    [ObservableProperty] private int slideChangeCount;
    [ObservableProperty] private int rotationCycle;
    // What the slide on screen is about: slide, period, filters, poste. The
    // tiles' change flash (Views/TileFx) only compares figures of the same
    // scope, and the charts (ChartCard) only move their bars within it.
    [ObservableProperty] private string slideScope = "";
    private string? _shownKey;
    // The slide shown's duration (Parametres > Affichage, or the default one).
    public double SlideSeconds => _rotationTimer.Interval.TotalSeconds;

    // Stop pop-up (both windows, the TV's above all): the lines in a stop
    // nobody has given a cause yet. It comes up when a new one appears, then
    // as a reminder every StopAlertReminder while any is left unqualified,
    // and goes away after StopAlertDuration (or a click): never stays over
    // the slides.
    private static readonly TimeSpan StopAlertDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StopAlertReminder = TimeSpan.FromMinutes(30);
    private readonly DispatcherTimer _stopAlertTimer;
    private readonly HashSet<string> _alertedStops = new();
    private DateTime _lastStopAlert = DateTime.MinValue;
    public ObservableCollection<OngoingStop> StopAlerts { get; } = new();
    [ObservableProperty] private bool isStopAlertShown;
    [ObservableProperty] private string stopAlertTitle = "";
    // The card lists the oldest few; the others are counted ("+ 4 autres lignes").
    private const int StopAlertMaxLines = 5;
    [ObservableProperty] private string stopAlertMore = "";

    // Synthesis slides: current month, last 3 months or the year (buttons on the slide).
    [ObservableProperty] private SynthesisPeriod synthesisPeriod = SynthesisPeriod.Month;

    // Filter bar. Every change applies on its own (no "Appliquer"); a short
    // debounce turns a burst of clicks into one deck rebuild. _filter is what
    // the slides currently use.
    public const string AllOption = "Tous";
    private DashboardFilter _filter = DashboardFilter.None;
    private readonly DispatcherTimer _filterTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    private string activeFilterLabel = "";

    public bool HasActiveFilter => ActiveFilterLabel.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomRangeLabel))]
    private DateTime? customFrom = DateTime.Now.Date.AddDays(-7);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomRangeLabel))]
    private DateTime? customTo = DateTime.Now.Date;

    [ObservableProperty] private string filterSector = AllOption;
    [ObservableProperty] private string filterShift = AllOption;
    [ObservableProperty] private string filterTeam = AllOption;
    [ObservableProperty] private string filterWeight = AllOption;
    [ObservableProperty] private bool isDatePickerOpen;
    [ObservableProperty] private string machineFilterLabel = "Toutes";

    // Team-bound account (SPIGA Suivi Production): the figures count its
    // team's postes only, the team filter is fixed and the postes worked by
    // another team are greyed out. null = every team (kiosk, responsables).
    private string? _lockedTeam;
    [ObservableProperty] private bool isTeamFree = true;
    [ObservableProperty] private bool matinOwn = true;
    [ObservableProperty] private bool apresMidiOwn = true;
    [ObservableProperty] private bool nuitOwn = true;

    public string? LockedTeam => _lockedTeam;

    public void LockTeam(string? team)
    {
        if (_lockedTeam == team)
            return;
        _lockedTeam = team;
        IsTeamFree = team == null;
        RefreshFilterOptions();
        FilterTeam = team ?? AllOption;
        RefreshOwnShifts();
    }

    public bool IsShiftOwn(int shiftId) => shiftId switch { 1 => MatinOwn, 2 => ApresMidiOwn, 3 => NuitOwn, _ => true };

    // "Le poste Nuit (Equipe A) ne concerne pas votre équipe (Equipe B)."
    public string ForeignShiftMessage(int shiftId)
    {
        var name = KpiCalculator.ShiftName(shiftId);
        if (SelectedPeriod is DashboardPeriod.Day or DashboardPeriod.PreviousShift)
        {
            var now = DateTime.Now;
            var date = TeamScheduleService.GetShiftDate(now, KpiCalculator.CurrentShiftId(now));
            return $"Ce poste ne concerne pas votre équipe : le poste {name} du {date:dd/MM} est tenu par "
                   + $"{_teamSchedule.GetTeamForShift(date, shiftId)}. Vous ne voyez que les postes de {_lockedTeam}.";
        }
        return $"Ce poste ne concerne pas votre équipe : {_lockedTeam} n'a pas tenu le poste {name} sur cette période.";
    }

    // Today (and the previous poste): the postes of the team today. Longer
    // periods: the postes it worked on at least one day (the rotation).
    private void RefreshOwnShifts()
    {
        bool Own(int shiftId)
        {
            if (_lockedTeam == null)
                return true;
            var now = DateTime.Now;
            var (from, to) = SelectedPeriod is DashboardPeriod.Day or DashboardPeriod.PreviousShift
                ? (TeamScheduleService.GetShiftDate(now, KpiCalculator.CurrentShiftId(now)), DateTime.MinValue)
                : CurrentRange;
            if (to == DateTime.MinValue)
                to = from.AddDays(1);
            for (var d = from.Date; d < to && d < from.Date.AddDays(28); d = d.AddDays(1))
            {
                var team = _teamSchedule.GetTeamForShift(d, shiftId);
                if (string.IsNullOrEmpty(team) || string.Equals(team, _lockedTeam, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        MatinOwn = Own(1);
        ApresMidiOwn = Own(2);
        NuitOwn = Own(3);
        // A poste chosen before, now another team's: back to all (= its own).
        var chosen = FilterShift switch { "Matin" => 1, "Après-midi" => 2, "Nuit" => 3, _ => 0 };
        if (chosen != 0 && !IsShiftOwn(chosen))
            FilterShift = AllOption;
    }

    // "Dates..." button: shows the chosen range once that period is selected.
    public string CustomRangeLabel => SelectedPeriod == DashboardPeriod.Custom
        ? $"{CustomFrom:dd/MM} - {CustomTo:dd/MM}"
        : "Dates…";

    public ObservableCollection<string> TeamOptions { get; } = new();
    public ObservableCollection<string> WeightOptions { get; } = new();

    // Every machine of the rotation; the picker only lists the chosen sector's.
    private readonly List<MachineFilterOption> _allMachineOptions = new();
    public ObservableCollection<MachineFilterOption> MachineOptions { get; } = new();

    public DashboardFilter CurrentFilter => _filter;

    public (DateTime From, DateTime To) CurrentRange
        => SelectedPeriod is DashboardPeriod.Day or DashboardPeriod.PreviousShift
            ? PeriodRanges.Get(DashboardPeriod.Day, DateTime.Now)
            : PeriodRanges.Get(SelectedPeriod, DateTime.Now, CustomFrom, CustomTo);

    public MainViewModel(ProductionRepository repository, AppSettings settings, TeamScheduleService teamSchedule,
        SharedSettingsSync sharedSettings)
    {
        _repository = repository;
        _settings = settings;
        _teamSchedule = teamSchedule;
        _sharedSettings = sharedSettings;
        _repository.Access.Changed += OnAccessChanged;

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            await ApplyFiltersAsync();
        };
        RefreshFilterOptions();

        // Timer handlers are async void: RunGuardedAsync makes sure no
        // exception can escape them and crash the kiosk.
        _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.SlideDurationSeconds) };
        _rotationTimer.Tick += async (_, _) => await RunGuardedAsync(AdvanceAsync);

        // Also doubles as the "current team" refresh: cheap (no DB call) and a
        // 1-minute cadence is more than enough precision for a poste changeover.
        _noteTimer = new DispatcherTimer { Interval = NoteRefreshInterval };
        _noteTimer.Tick += async (_, _) => await RunGuardedAsync(async () =>
        {
            RefreshCurrentTeam();
            await RefreshNoteAsync();
            await RefreshStopAlertsAsync();
        });

        _stopAlertTimer = new DispatcherTimer { Interval = StopAlertDuration };
        _stopAlertTimer.Tick += (_, _) => DismissStopAlert();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_settings.DataRefreshIntervalMinutes) };
        _refreshTimer.Tick += async (_, _) => await RunGuardedAsync(RefreshAsync);

        // Only runs while the DB is unreachable (see SetConnectionState).
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.ReconnectIntervalSeconds)) };
        _reconnectTimer.Tick += async (_, _) => await RunGuardedAsync(ReconnectAsync);

        // Independent of slide rotation / pause: always probes the DB, so a
        // dropped connection is noticed even on a paused or static slide.
        _healthCheckTimer = new DispatcherTimer { Interval = HealthCheckInterval };
        _healthCheckTimer.Tick += async (_, _) => await RunGuardedAsync(CheckConnectionAsync);
        _healthCheckTimer.Start();
    }

    private async Task RefreshStopAlertsAsync()
    {
        if (!_settings.ShowStopAlerts)
        {
            DismissStopAlert();
            return;
        }
        var stops = await OngoingStopService.GetAsync(_repository, _settings);
        var open = stops.Values
            .Where(s => s.IsUnqualified && !_settings.HiddenMachineIds.Contains(s.MachineId))
            .OrderBy(s => s.Start)
            .ToList();
        var keys = open.Select(s => $"{s.MachineId}|{s.Start:O}").ToHashSet();
        var isNew = keys.Any(k => !_alertedStops.Contains(k));
        _alertedStops.Clear();
        _alertedStops.UnionWith(keys);

        if (open.Count == 0)
        {
            DismissStopAlert();
            return;
        }
        if (!isNew && DateTime.Now - _lastStopAlert < StopAlertReminder)
            return;

        StopAlerts.Clear();
        foreach (var stop in open.Take(StopAlertMaxLines))
            StopAlerts.Add(stop);
        var more = open.Count - StopAlertMaxLines;
        StopAlertMore = more <= 0 ? "" : more == 1 ? "+ 1 autre ligne" : $"+ {more} autres lignes";
        StopAlertTitle = open.Count == 1 ? "ARRÊT EN COURS — cause à définir" : $"{open.Count} ARRÊTS EN COURS — cause à définir";
        _lastStopAlert = DateTime.Now;
        IsStopAlertShown = true;
        _stopAlertTimer.Stop();
        _stopAlertTimer.Start();
    }

    [RelayCommand]
    private void DismissStopAlert()
    {
        _stopAlertTimer.Stop();
        IsStopAlertShown = false;
    }

    private async Task CheckConnectionAsync()
    {
        // While suspended / reconnecting, the reconnect timer already owns recovery.
        if (_suspended || IsReconnecting)
            return;

        AppLogger.Log("Vérification périodique de la connexion à la base de données…");
        try
        {
            await _repository.GetMachinesAsync();
            SetConnectionState(healthy: true);
        }
        catch (Exception ex)
        {
            Suspend(ex.Message);
        }
    }

    private void Suspend(string errorMessage)
    {
        _suspended = true;
        _rotationTimer.Stop();
        _noteTimer.Stop();
        _refreshTimer.Stop();
        SetConnectionState(healthy: false, errorMessage);
    }

    // An exception escaping an async void timer handler (DB dropped) would
    // crash the kiosk: route it to the disconnected state instead.
    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Erreur non gérée : {ex.Message}");
            SetConnectionState(healthy: false, ex.Message);
        }
    }

    // Another account, or its team / platforms changed (SPIGA Suivi
    // Production): the repository now serves other machines. What is on
    // screen goes at once; the deck is rebuilt from what is allowed now.
    private bool _started;

    private void OnAccessChanged()
    {
        DismissStopAlert();
        _alertedStops.Clear();
        if (!_started)
            return;
        CurrentSlide = null;
        _ = RunGuardedAsync(async () =>
        {
            await RebuildSlidesAsync();
            await RefreshStopAlertsAsync();
        });
    }

    public async Task InitializeAsync()
    {
        _started = true;
        _suspended = true;
        _rotationTimer.Stop();
        _noteTimer.Stop();
        _refreshTimer.Stop();

        // Another PC may have changed the cadences, pauses, teams... meanwhile.
        await _sharedSettings.PullAsync();
        _sharedVersionBuilt = _settings.SharedSettingsVersion;

        // Settings (slide duration / refresh interval) may have changed since construction.
        // Set before resetting IsRotationPaused below, since that setter can restart
        // _rotationTimer immediately (if slides from a prior run are still loaded).
        _rotationTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.SlideDurationSeconds));
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.DataRefreshIntervalMinutes));

        // A resync should always resume playback, even if the rotation was paused before.
        IsRotationPaused = false;

        // Settings may now point at another server/database, or have new
        // cadences / empty bag weights / team names.
        SynthesisSlideViewModel.ClearCache();
        MachinePeriodSlideViewModel.ClearCache();
        PackagingSlideViewModel.ClearCache();
        RefreshFilterOptions();

        StartLoad();
        try
        {
            await BuildSlidesAsync();
            await RefreshNoteAsync();
            RefreshCurrentTeam();
            await RefreshStopAlertsAsync();

            _currentIndex = 0;
            if (_slides.Count > 0)
            {
                // Failure already reported (disconnected state + retry timer).
                if (!await ShowSlideAsync(0))
                    return;
            }
            else
            {
                CurrentSlide = null;
            }

            _suspended = false;
            SetConnectionState(healthy: true);
        }
        catch (Exception ex)
        {
            SetConnectionState(healthy: false, ex.Message);
            return;
        }
        finally
        {
            EndLoad();
        }

        if (_stopped)
            return;
        _rotationTimer.Start();
        _noteTimer.Start();
        _refreshTimer.Start();
        OnPropertyChanged(nameof(SlideSeconds));
        RotationCycle++;
    }

    // For a deck whose window has closed (TV screen): DispatcherTimers keep
    // ticking, and querying the DB, until stopped.
    public void Stop()
    {
        _stopped = true;
        _suspended = true;
        _rotationTimer.Stop();
        _noteTimer.Stop();
        _refreshTimer.Stop();
        _reconnectTimer.Stop();
        _healthCheckTimer.Stop();
        _filterTimer.Stop();
    }

    // "Reconnecter" button (top bar + disconnected overlay), the automatic
    // retry timer, and Settings > Enregistrer all come through here.
    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (IsReconnecting)
            return;

        IsReconnecting = true;
        ReconnectStatus = "Reconnexion en cours…";
        AppLogger.Log("Tentative de reconnexion à la base de données…");
        try
        {
            _repository.ResetConnections();
            await InitializeAsync();
        }
        finally
        {
            IsReconnecting = false;
            ReconnectStatus = IsConnectionHealthy
                ? ""
                : $"Échec à {DateTime.Now:HH:mm:ss} - nouvelle tentative automatique dans {(int)_reconnectTimer.Interval.TotalSeconds} s.";
        }
    }

    // Who writes the note (DashboardView.Author).
    public ChangeAuthor Author { get; set; } = ChangeAuthor.Kiosk;

    // « Modifier la note de l'écran » over the whole plant (signed-in
    // application). Kiosk: true, NoteGate asks for an account at save.
    private bool _canEditNote = true;

    public bool CanEditNote
    {
        get => _canEditNote;
        set
        {
            if (SetProperty(ref _canEditNote, value))
                SaveNoteCommand.NotifyCanExecuteChanged();
        }
    }

    // Kiosk: the account that signs in to save the note, or null (refused).
    public Func<Task<ChangeAuthor?>>? NoteGate { get; set; }

    [RelayCommand(CanExecute = nameof(CanEditNote))]
    private async Task SaveNoteAsync()
    {
        if (!CanEditNote)
            return;
        var author = Author;
        if (NoteGate != null)
        {
            if (await NoteGate() is not { } signedIn)
            {
                NoteSaveStatus = "Non enregistré.";
                return;
            }
            author = signedIn;
        }
        try
        {
            await _repository.SetActiveNoteAsync(NoteText, author);
            NoteSaveStatus = "Enregistré.";
        }
        catch (Exception ex)
        {
            NoteSaveStatus = $"Échec de l'enregistrement : {ex.Message}";
        }
    }

    partial void OnSelectedPeriodChanged(DashboardPeriod value)
    {
        OnPropertyChanged(nameof(CustomRangeLabel));
        // "Dates..." opens the date pickers right away.
        if (value == DashboardPeriod.Custom)
            IsDatePickerOpen = true;
        RefreshOwnShifts();
        _ = ShowSlideAsync(_currentIndex);
    }

    partial void OnCustomFromChanged(DateTime? value) => OnCustomDatesChanged();
    partial void OnCustomToChanged(DateTime? value) => OnCustomDatesChanged();

    private void OnCustomDatesChanged()
    {
        if (SelectedPeriod == DashboardPeriod.Custom)
        {
            RefreshOwnShifts();
            QueueFilterApply();
        }
    }

    // A new sector lists its own machines, all selected.
    partial void OnFilterSectorChanged(string value)
    {
        ShowSectorMachines();
        QueueFilterApply();
    }

    partial void OnFilterShiftChanged(string value) => QueueFilterApply();
    partial void OnFilterTeamChanged(string value) => QueueFilterApply();
    partial void OnFilterWeightChanged(string value) => QueueFilterApply();

    private void QueueFilterApply()
    {
        // Called from property initializers / RefreshFilterOptions before the timer exists.
        if (_filterTimer == null)
            return;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async Task ApplyFiltersAsync()
    {
        var selected = MachineOptions.Where(m => m.IsSelected).ToList();
        // Every machine of the sector ticked = no machine filter.
        var allTicked = selected.Count == MachineOptions.Count;
        _filter = new DashboardFilter
        {
            Sector = FilterSector == AllOption ? null : FilterSector,
            MachineIds = allTicked ? Array.Empty<string>() : selected.Select(m => m.MachineId).ToList(),
            MachineNames = allTicked ? Array.Empty<string>() : selected.Select(m => m.MachineName).ToList(),
            ShiftId = FilterShift switch { "Matin" => 1, "Après-midi" => 2, "Nuit" => 3, _ => null },
            Team = FilterTeam == AllOption ? null : FilterTeam,
            TargetWeight = FilterWeight == AllOption ? null : double.Parse(FilterWeight.Replace(" g", ""), CultureInfo.InvariantCulture),
        };
        ActiveFilterLabel = _filter.Describe();
        AppLogger.Log($"Filtres appliqués : {(ActiveFilterLabel.Length == 0 ? "aucun" : ActiveFilterLabel)}");

        // Before the first load (or while disconnected) the deck is built later.
        if (_suspended)
            return;

        // The deck's shape depends on the sector / machine filters.
        await RunGuardedAsync(async () =>
        {
            StartLoad();
            try
            {
                await BuildSlidesAsync();
                _currentIndex = 0;
                if (_slides.Count == 0)
                    CurrentSlide = null;
                else
                    await ShowSlideAsync(0);
            }
            finally
            {
                EndLoad();
            }
            RestartRotationTimer();
        });
    }

    [RelayCommand]
    private void ResetFilters()
    {
        FilterShift = FilterWeight = AllOption;
        FilterTeam = _lockedTeam ?? AllOption;
        if (FilterSector == AllOption)
            SelectAllMachines();
        FilterSector = AllOption;
        SelectedPeriod = DashboardPeriod.Day;
        QueueFilterApply();
    }

    // "Dates..." button: reopens the pickers even when that period is already selected.
    [RelayCommand]
    private void OpenDatePicker() => IsDatePickerOpen = true;

    [RelayCommand]
    private void CloseDatePicker() => IsDatePickerOpen = false;

    [RelayCommand]
    private void SelectAllMachines()
    {
        foreach (var machine in MachineOptions)
            machine.IsSelected = true;
    }

    // Lists the chosen sector's machines in the picker, all selected.
    private void ShowSectorMachines()
    {
        MachineOptions.Clear();
        foreach (var machine in _allMachineOptions.Where(m => FilterSector == AllOption || m.Sector == FilterSector))
        {
            machine.IsSelected = true;
            MachineOptions.Add(machine);
        }
        UpdateMachineFilterLabel();
    }

    private void OnMachineOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MachineFilterOption.IsSelected) || !MachineOptions.Contains(sender))
            return;
        UpdateMachineFilterLabel();
        QueueFilterApply();
    }

    private void UpdateMachineFilterLabel()
    {
        var selected = MachineOptions.Where(m => m.IsSelected).Select(m => m.MachineName).ToList();
        MachineFilterLabel = selected.Count == MachineOptions.Count ? "Toutes"
            : selected.Count == 0 ? "Aucune"
            : selected.Count <= 3 ? string.Join(", ", selected)
            : $"{selected.Count} machines";
    }

    [RelayCommand]
    private async Task SetSynthesisPeriodAsync(string period)
    {
        SynthesisPeriod = Enum.Parse<SynthesisPeriod>(period);
        await ShowSlideAsync(_currentIndex);
        RestartRotationTimer();
    }

    // Team names / weights come from Parametres: re-read after a save.
    private void RefreshFilterOptions()
    {
        TeamOptions.Clear();
        if (_lockedTeam != null)
        {
            TeamOptions.Add(_lockedTeam);
        }
        else
        {
            TeamOptions.Add(AllOption);
            foreach (var team in _teamSchedule.TeamNames)
                TeamOptions.Add(team);
        }
        if (!TeamOptions.Contains(FilterTeam))
            FilterTeam = _lockedTeam ?? AllOption;

        WeightOptions.Clear();
        WeightOptions.Add(AllOption);
        foreach (var weight in _settings.NominalRates.Where(r => r.WeightGrams > 0).Select(r => r.WeightGrams).Distinct().Order())
            WeightOptions.Add(weight.ToString("0.##", CultureInfo.InvariantCulture) + " g");
        if (!WeightOptions.Contains(FilterWeight))
            FilterWeight = AllOption;
    }

    // Manual pause/play: doesn't touch the note/data-refresh timers, only the
    // slide auto-advance. Kiosk operators use this to hold a slide still (e.g.
    // to walk someone through a number) without losing the live data feed.
    partial void OnIsRotationPausedChanged(bool value)
    {
        if (value)
            _rotationTimer.Stop();
        else if (_slides.Count > 0 && !_suspended)
        {
            _rotationTimer.Start();
            RotationCycle++;
        }

        AppLogger.Log(value ? "Rotation automatique mise en pause." : "Rotation automatique reprise.");
    }

    [RelayCommand]
    private void ToggleRotation()
    {
        IsRotationPaused = !IsRotationPaused;
    }

    [RelayCommand]
    private async Task NextSlideAsync()
    {
        if (_slides.Count == 0)
            return;

        _currentIndex = (_currentIndex + 1) % _slides.Count;
        await ShowSlideAsync(_currentIndex);
        RestartRotationTimer();
    }

    [RelayCommand]
    private async Task PreviousSlideAsync()
    {
        if (_slides.Count == 0)
            return;

        _currentIndex = (_currentIndex - 1 + _slides.Count) % _slides.Count;
        await ShowSlideAsync(_currentIndex);
        RestartRotationTimer();
    }

    // Manual navigation gives the operator a full slide duration before the
    // next auto-advance, instead of firing right after their click.
    private void RestartRotationTimer()
    {
        if (IsRotationPaused || _suspended)
            return;

        _rotationTimer.Stop();
        _rotationTimer.Start();
        RotationCycle++;
    }

    private async Task AdvanceAsync()
    {
        if (_slides.Count == 0)
            return;

        _currentIndex = (_currentIndex + 1) % _slides.Count;
        await ShowSlideAsync(_currentIndex);
    }

    // Data refresh tick. New shared settings change every number (and the
    // slide caches), so they rebuild the deck from scratch, as Enregistrer does.
    private async Task RefreshAsync()
    {
        await _sharedSettings.PullAsync();
        if (_settings.SharedSettingsVersion != _sharedVersionBuilt)
            await InitializeAsync();
        else
            await RebuildSlidesAsync();
    }

    // Rebuilds the slide deck in place (machines dis/reconnecting, admin
    // hiding a machine, etc.) without disrupting the rotation timers. Keeps
    // showing the current slide's content if it's still valid; otherwise
    // jumps back to the first slide.
    private async Task RebuildSlidesAsync()
    {
        StartLoad();
        try
        {
            await BuildSlidesAsync();

            if (_slides.Count == 0)
            {
                CurrentSlide = null;
                _currentIndex = 0;
                return;
            }

            if (_currentIndex >= _slides.Count)
                _currentIndex = 0;

            // A failed slide has already stopped the deck and started the
            // reconnect timer: don't flag the base healthy over it.
            if (await ShowSlideAsync(_currentIndex) && !_suspended)
                SetConnectionState(healthy: true);
        }
        catch (Exception ex)
        {
            SetConnectionState(healthy: false, ex.Message);
        }
        finally
        {
            EndLoad();
        }
    }

    private async Task BuildSlidesAsync()
    {
        var allMachines = await _repository.GetMachinesAsync();
        _machinesById = allMachines.ToDictionary(m => m.MachineId);

        var shown = allMachines
            .Where(m => m.IsActive && !_settings.HiddenMachineIds.Contains(m.MachineId))
            .ToList();
        RefreshMachineOptions(shown);
        var visible = shown.Where(_filter.IncludesMachine).ToList();

        // Every machine ticked in Parametres stays in the rotation, connected or
        // not: a line with no recent snapshot is shown greyed out with a
        // "Pas de donnees" badge
        // (MachinePanel) so a stopped line is visible on the TV instead of
        // silently vanishing (deliberate change from the spec's "drop out").
        // Then the order and the slides switched off in Parametres > Affichage.
        var off = _settings.Slides.Where(s => !s.Enabled).Select(s => s.Key).ToHashSet();
        _slides = SlideDeck.Arrange(SlideDeck.Default(visible, _settings.PdfDocumentPath), _settings.Slides)
            .Where(s => !off.Contains(s.Key))
            .ToList();
    }

    // Machine checkboxes of the filter bar: the machines shown in the
    // rotation (Parametres), selection kept across refreshes. Rebuilt when a
    // machine is renamed too, and the active filter then shows the new name.
    private void RefreshMachineOptions(List<Machine> machines)
    {
        var ordered = machines.OrderBy(m => Array.IndexOf(SlideDeck.SectorOrder, m.Sector)).ThenBy(m => m.MachineId).ToList();
        if (ordered.Select(m => (m.MachineId, m.MachineName)).SequenceEqual(_allMachineOptions.Select(o => (o.MachineId, o.MachineName))))
            return;
        foreach (var option in _allMachineOptions)
            option.PropertyChanged -= OnMachineOptionChanged;
        _allMachineOptions.Clear();
        foreach (var machine in ordered)
        {
            var option = new MachineFilterOption { MachineId = machine.MachineId, MachineName = machine.MachineName, Sector = machine.Sector };
            option.PropertyChanged += OnMachineOptionChanged;
            _allMachineOptions.Add(option);
        }

        MachineOptions.Clear();
        foreach (var option in _allMachineOptions.Where(m => FilterSector == AllOption || m.Sector == FilterSector))
        {
            option.IsSelected = _filter.MachineIds.Count == 0 || _filter.MachineIds.Contains(option.MachineId);
            MachineOptions.Add(option);
        }
        UpdateMachineFilterLabel();

        var names = _allMachineOptions.ToDictionary(o => o.MachineId, o => o.MachineName);
        _filter = _filter with { MachineNames = _filter.MachineIds.Select(id => names.GetValueOrDefault(id, id)).ToList() };
        ActiveFilterLabel = _filter.Describe();
    }

    // Returns false when the slide couldn't load: the deck is then stopped
    // (_suspended) and the reconnect timer takes over. Never throws.
    private async Task<bool> ShowSlideAsync(int index)
    {
        if (_slides.Count == 0)
            return true;

        StartLoad();
        try
        {
            var slide = _slides[index];
            var previousType = CurrentSlide?.GetType();
            object? next = slide switch
            {
                MachineSlideDescriptor ms when SelectedPeriod is DashboardPeriod.Day or DashboardPeriod.PreviousShift
                    => await MachineDaySlideViewModel.LoadAsync(_repository, _teamSchedule, _settings, ms.Sector,
                        _machinesById[ms.MachineId], _filter, previousShift: SelectedPeriod == DashboardPeriod.PreviousShift),
                MachineSlideDescriptor ms => await LoadPeriodSlideAsync(ms),
                PdfSlideDescriptor pdf => await PdfSlideViewModel.LoadAsync(pdf.PdfPath),
                SynthesisSlideDescriptor synthesis => await SynthesisSlideViewModel.LoadAsync(_repository, _teamSchedule, _settings,
                    synthesis.Sector, synthesis.MachineIds.Select(id => _machinesById[id]).ToList(), SynthesisPeriod, _filter),
                PackagingSlideDescriptor packaging => await LoadPackagingSlideAsync(packaging),
                RunStateSlideDescriptor runState => await RunStateSlideViewModel.LoadAsync(_repository, _settings, runState.Title,
                    runState.MachineIds.Select(id => _machinesById[id]).ToList()),
                _ => null,
            };
            // Before the slide: its figures read the scope they belong to.
            var now = DateTime.Now;
            var shiftId = KpiCalculator.CurrentShiftId(now);
            SlideScope = string.Join("|", slide.Key, SelectedPeriod, CustomFrom?.ToString("yyyyMMdd"), CustomTo?.ToString("yyyyMMdd"),
                SynthesisPeriod, _filter.Describe(), TeamScheduleService.GetShiftDate(now, shiftId).ToString("yyyyMMdd"), shiftId);
            CurrentSlide = next;
            IsPdfSlideShown = CurrentSlide is PdfSlideViewModel;
            // Before RotationCycle moves: the timer bar reads SlideSeconds then.
            // Only on a change, as setting it restarts the countdown.
            var seconds = TimeSpan.FromSeconds(_settings.SlideSecondsFor(slide.Key));
            if (_rotationTimer.Interval != seconds)
            {
                _rotationTimer.Interval = seconds;
                OnPropertyChanged(nameof(SlideSeconds));
            }
            if (slide.Key != _shownKey || previousType != CurrentSlide?.GetType())
            {
                _shownKey = slide.Key;
                SlideChangeCount++;
                if (!IsRotationPaused)
                    RotationCycle++;
            }
            // While suspended, recovery is InitializeAsync's job (it restarts the timers).
            if (!_suspended)
                SetConnectionState(healthy: true);
            return true;
        }
        catch (Exception ex)
        {
            Suspend(ex.Message);
            return false;
        }
        finally
        {
            EndLoad();
        }
    }

    private Task<MachinePeriodSlideViewModel> LoadPeriodSlideAsync(MachineSlideDescriptor slide)
    {
        var (from, to) = PeriodRanges.Get(SelectedPeriod, DateTime.Now, CustomFrom, CustomTo);
        var label = PeriodRanges.Label(SelectedPeriod, from, to);
        // Nothing before DataStartDate is read: start the charts there, and say so.
        if (from < _settings.DataStartDate && to > _settings.DataStartDate)
        {
            from = _settings.DataStartDate.Date;
            label += $" - données depuis le {from:dd/MM/yyyy}";
        }
        return MachinePeriodSlideViewModel.LoadAsync(_repository, _teamSchedule, _settings, slide.Sector,
            _machinesById[slide.MachineId], from, to, label, _filter);
    }

    // Top-bar period; "Aujourd'hui" and "Poste precedent" both show the whole
    // day. Cumul is always the current month, named ("octobre 2026").
    private Task<PackagingSlideViewModel> LoadPackagingSlideAsync(PackagingSlideDescriptor slide)
    {
        DateTime from, to;
        string label;
        if (slide.Cumul)
        {
            (from, to, label) = PeriodRanges.Synthesis(SynthesisPeriod.Month, DateTime.Now);
        }
        else
        {
            (from, to) = CurrentRange;
            label = PeriodRanges.Label(SelectedPeriod == DashboardPeriod.PreviousShift ? DashboardPeriod.Day : SelectedPeriod, from, to);
        }
        // Nothing before DataStartDate is read: start there, and say so.
        if (from < _settings.DataStartDate && to > _settings.DataStartDate)
        {
            from = _settings.DataStartDate.Date;
            label += $" - données depuis le {from:dd/MM/yyyy}";
        }
        return PackagingSlideViewModel.LoadAsync(_repository, _teamSchedule, _settings, slide.DisplayTitle, slide.Trs, slide.Cumul,
            slide.MachineIds.Select(id => _machinesById[id]).ToList(), from, to, label, _filter);
    }

    private void SetConnectionState(bool healthy, string errorMessage = "")
    {
        if (healthy != IsConnectionHealthy)
            AppLogger.Log(healthy ? "Connexion à la base de données rétablie." : $"Connexion perdue : {errorMessage}");

        IsConnectionHealthy = healthy;
        ConnectionErrorMessage = healthy ? "" : errorMessage;

        if (healthy)
        {
            _reconnectTimer.Stop();
            ReconnectStatus = "";
        }
        else if (!_stopped && !_reconnectTimer.IsEnabled)
        {
            _reconnectTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _settings.ReconnectIntervalSeconds));
            _reconnectTimer.Start();
        }
    }

    private void RefreshCurrentTeam()
    {
        RefreshOwnShifts();
        var now = DateTime.Now;
        var shiftId = KpiCalculator.CurrentShiftId(now);
        var team = _teamSchedule.GetCurrentTeam(now);
        CurrentTeamLabel = string.IsNullOrEmpty(team)
            ? KpiCalculator.ShiftName(shiftId)
            : $"{team} - Poste {KpiCalculator.ShiftName(shiftId)}";
    }

    private async Task RefreshNoteAsync()
    {
        // Don't clobber text someone is actively typing (e.g. a manager mid-edit
        // when the periodic refresh timer ticks).
        if (IsEditingNote)
            return;

        try
        {
            var note = await _repository.GetActiveNoteAsync();
            NoteText = note?.Content ?? "";
        }
        catch (Exception ex)
        {
            // Application database unreachable: the note shown stays, the
            // production keeps showing (it is in another database).
            AppLogger.Log("Note du tableau de bord illisible (base applicative) : " + ex.Message);
        }
    }
}
