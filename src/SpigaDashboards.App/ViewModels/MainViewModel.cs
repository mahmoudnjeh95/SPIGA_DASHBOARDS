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
    // Sector display order. CC = couscous, PC = pate courte, PL = pate longue.
    // Machines within a sector are auto-paired (2 per slide, odd one gets its own).
    private static readonly string[] SectorOrder = { "Couscous", "Pate courte", "Pate longue" };

    private static readonly TimeSpan NoteRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly ProductionRepository _repository;
    private readonly AppSettings _settings;
    private readonly TeamScheduleService _teamSchedule;
    private readonly DispatcherTimer _rotationTimer;
    private readonly DispatcherTimer _noteTimer;
    private readonly DispatcherTimer _refreshTimer;

    private List<SlideDescriptor> _slides = new();
    private Dictionary<string, Machine> _machinesById = new();
    private int _currentIndex;

    [ObservableProperty] private object? currentSlide;
    [ObservableProperty] private string noteText = "";
    [ObservableProperty] private DashboardPeriod selectedPeriod = DashboardPeriod.Day;
    [ObservableProperty] private bool isConnectionHealthy = true;
    [ObservableProperty] private string connectionErrorMessage = "";
    [ObservableProperty] private bool isEditingNote;
    [ObservableProperty] private string noteSaveStatus = "";
    [ObservableProperty] private string currentTeamLabel = "";
    [ObservableProperty] private bool isRotationPaused;

    public MainViewModel(ProductionRepository repository, AppSettings settings, TeamScheduleService teamSchedule)
    {
        _repository = repository;
        _settings = settings;
        _teamSchedule = teamSchedule;

        _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.SlideDurationSeconds) };
        _rotationTimer.Tick += async (_, _) => await AdvanceAsync();

        // Also doubles as the "current team" refresh: cheap (no DB call) and a
        // 1-minute cadence is more than enough precision for a poste changeover.
        _noteTimer = new DispatcherTimer { Interval = NoteRefreshInterval };
        _noteTimer.Tick += async (_, _) => { RefreshCurrentTeam(); await RefreshNoteAsync(); };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_settings.DataRefreshIntervalMinutes) };
        _refreshTimer.Tick += async (_, _) => await RebuildSlidesAsync();
    }

    public async Task InitializeAsync()
    {
        _rotationTimer.Stop();
        _noteTimer.Stop();
        _refreshTimer.Stop();

        // Settings (slide duration / refresh interval) may have changed since construction.
        // Set before resetting IsRotationPaused below, since that setter can restart
        // _rotationTimer immediately (if slides from a prior run are still loaded).
        _rotationTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.SlideDurationSeconds));
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.DataRefreshIntervalMinutes));

        // A resync should always resume playback, even if the rotation was paused before.
        IsRotationPaused = false;

        try
        {
            await BuildSlidesAsync();
            await RefreshNoteAsync();
            RefreshCurrentTeam();

            _currentIndex = 0;
            if (_slides.Count > 0)
                await ShowSlideAsync(0);
            else
                CurrentSlide = null;

            SetConnectionState(healthy: true);
        }
        catch (Exception ex)
        {
            SetConnectionState(healthy: false, ex.Message);
            return;
        }

        _rotationTimer.Start();
        _noteTimer.Start();
        _refreshTimer.Start();
    }

    [RelayCommand]
    private async Task ResyncAsync()
    {
        AppLogger.Log("Resynchronisation demandee...");
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        try
        {
            await _repository.SetActiveNoteAsync(NoteText);
            NoteSaveStatus = "Enregistre.";
        }
        catch (Exception ex)
        {
            NoteSaveStatus = $"Echec de l'enregistrement : {ex.Message}";
        }
    }

    partial void OnSelectedPeriodChanged(DashboardPeriod value)
    {
        _ = ShowSlideAsync(_currentIndex);
    }

    // Manual pause/play: doesn't touch the note/data-refresh timers, only the
    // slide auto-advance. Kiosk operators use this to hold a slide still (e.g.
    // to walk someone through a number) without losing the live data feed.
    partial void OnIsRotationPausedChanged(bool value)
    {
        if (value)
            _rotationTimer.Stop();
        else if (_slides.Count > 0)
            _rotationTimer.Start();

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
        if (IsRotationPaused)
            return;

        _rotationTimer.Stop();
        _rotationTimer.Start();
    }

    private async Task AdvanceAsync()
    {
        if (_slides.Count == 0)
            return;

        _currentIndex = (_currentIndex + 1) % _slides.Count;
        await ShowSlideAsync(_currentIndex);
    }

    // Rebuilds the slide deck in place (machines dis/reconnecting, admin
    // hiding a machine, etc.) without disrupting the rotation timers. Keeps
    // showing the current slide's content if it's still valid; otherwise
    // jumps back to the first slide.
    private async Task RebuildSlidesAsync()
    {
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

            await ShowSlideAsync(_currentIndex);
            SetConnectionState(healthy: true);
        }
        catch (Exception ex)
        {
            SetConnectionState(healthy: false, ex.Message);
        }
    }

    private async Task BuildSlidesAsync()
    {
        var allMachines = await _repository.GetMachinesAsync();
        _machinesById = allMachines.ToDictionary(m => m.MachineId);

        var visible = allMachines
            .Where(m => m.IsActive && !_settings.HiddenMachineIds.Contains(m.MachineId))
            .ToList();

        // Spec: machines that aren't currently connected drop out of the rotation entirely.
        var statuses = await Task.WhenAll(visible.Select(m => _repository.GetLatestStatusAsync(m.MachineId)));
        var connected = visible
            .Zip(statuses, (machine, status) => (machine, status))
            .Where(x => x.status?.IsConnected == true)
            .Select(x => x.machine)
            .ToList();

        var slides = new List<SlideDescriptor>();
        foreach (var sector in SectorOrder)
        {
            var sectorMachines = connected
                .Where(m => m.Sector == sector)
                .OrderBy(m => m.MachineId)
                .ToList();

            for (var i = 0; i < sectorMachines.Count; i += 2)
            {
                var pairIds = sectorMachines.Skip(i).Take(2).Select(m => m.MachineId).ToList();
                slides.Add(new MachinePairSlideDescriptor(sector, pairIds));
            }
        }

        // Slide 8 per the spec's table: admin-selected external document. Skipped
        // entirely (not shown as a broken slide) when no path is configured yet.
        if (!string.IsNullOrWhiteSpace(_settings.PdfDocumentPath))
            slides.Add(new PdfSlideDescriptor(_settings.PdfDocumentPath));

        // Slides 9-13: annual synthesis, one per sector, same order as above.
        foreach (var sector in SectorOrder)
            slides.Add(new AnnualSummarySlideDescriptor(sector));

        _slides = slides;
    }

    private async Task ShowSlideAsync(int index)
    {
        if (_slides.Count == 0)
            return;

        try
        {
            var slide = _slides[index];
            CurrentSlide = slide switch
            {
                MachinePairSlideDescriptor mp => await MachinePairSlideViewModel.LoadAsync(
                    _repository, mp.Sector, mp.MachineIds.Select(id => _machinesById[id]).ToList(), SelectedPeriod),
                PdfSlideDescriptor pdf => await PdfSlideViewModel.LoadAsync(pdf.PdfPath),
                AnnualSummarySlideDescriptor summary => await AnnualSummarySlideViewModel.LoadAsync(_repository, summary.Sector),
                _ => null,
            };
            SetConnectionState(healthy: true);
        }
        catch (Exception ex)
        {
            _rotationTimer.Stop();
            _noteTimer.Stop();
            _refreshTimer.Stop();
            SetConnectionState(healthy: false, ex.Message);
        }
    }

    private void SetConnectionState(bool healthy, string errorMessage = "")
    {
        if (healthy != IsConnectionHealthy)
            AppLogger.Log(healthy ? "Connexion a la base de donnees retablie." : $"Connexion perdue : {errorMessage}");

        IsConnectionHealthy = healthy;
        ConnectionErrorMessage = healthy ? "" : errorMessage;
    }

    private void RefreshCurrentTeam()
    {
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

        var note = await _repository.GetActiveNoteAsync();
        NoteText = note?.Content ?? "";
    }
}
