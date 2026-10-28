using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels;

// "Arrets" window (main screen only, not the TV): production stops per
// poste and team over a range of days (StopDetector), with totals per team
// and the printable sheet each team fills in (StopSheetPdf).
public partial class StopsViewModel : ObservableObject
{
    private const string AllOption = "Toutes";
    private const string AllPostes = "Tous";
    private static readonly MachinePick AllMachines = new(AllOption, Array.Empty<string>());

    private readonly ProductionRepository _repository;
    private readonly TeamScheduleService _teamSchedule;
    private readonly AppSettings _settings;
    private List<Machine> _machines = new();

    [ObservableProperty] private DateTime? fromDate = DateTime.Now.Date;
    [ObservableProperty] private DateTime? toDate = DateTime.Now.Date;
    [ObservableProperty] private MachinePick selectedMachine = AllMachines;
    [ObservableProperty] private string selectedShift = AllPostes;
    [ObservableProperty] private string selectedTeam = AllOption;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private bool isBusy;

    public List<string> ShiftOptions { get; } = new() { AllPostes, "Matin", "Après-midi", "Nuit" };
    public ObservableCollection<string> TeamOptions { get; } = new();
    public ObservableCollection<MachinePick> MachineOptions { get; } = new();
    public ObservableCollection<ProductionStop> Results { get; } = new();
    public ObservableCollection<TeamStopTotal> TeamTotals { get; } = new();

    public string SettingsText =>
        $"Seuil : {_settings.StopThresholdMinutes} min  -  Pauses planifiées déduites : {BreakSchedule.Describe(_settings.PlannedBreaks)}  (modifiables dans Paramètres)";

    public StopsViewModel(ProductionRepository repository, TeamScheduleService teamSchedule, AppSettings settings,
        (DateTime From, DateTime To)? initialRange = null)
    {
        _repository = repository;
        _teamSchedule = teamSchedule;
        _settings = settings;

        TeamOptions.Add(AllOption);
        foreach (var name in teamSchedule.TeamNames)
            TeamOptions.Add(name);
        if (!TeamOptions.Contains(TeamScheduleService.NoTeam))
            TeamOptions.Add(TeamScheduleService.NoTeam);
        MachineOptions.Add(AllMachines);

        if (initialRange is { } range)
        {
            FromDate = range.From.Date;
            ToDate = range.To.AddDays(-1).Date;
        }
    }

    // Loads the machine list, then the stops of the opening range.
    public async Task InitializeAsync()
    {
        try
        {
            _machines = await _repository.GetMachinesAsync();
            foreach (var machine in _machines)
                MachineOptions.Add(new MachinePick(machine.MachineName, new[] { machine.MachineId }));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossible de charger les machines : {ex.Message}";
            return;
        }
        await SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        // An unhandled exception here (async command) would take the whole app
        // down, so surface any failure as a status message instead.
        IsBusy = true;
        try
        {
            await RunSearchAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Echec de la recherche des arrets : {ex}");
            StatusMessage = $"Échec de la recherche : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunSearchAsync()
    {
        var fromDay = (FromDate ?? DateTime.Now.Date).Date;
        var toDay = (ToDate ?? DateTime.Now.Date).Date;
        if (toDay < fromDay)
            (fromDay, toDay) = (toDay, fromDay);
        // Postes of these days: Matin of the first day to Nuit of the last
        // (ending the next morning at 06h).
        var from = fromDay.AddHours(6);
        var to = toDay.AddDays(1).AddHours(6);

        if (_machines.Count == 0)
            _machines = await _repository.GetMachinesAsync();
        var machines = SelectedMachine.MachineIds.Count == 0
            ? _machines
            : _machines.Where(m => SelectedMachine.MachineIds.Contains(m.MachineId)).ToList();

        StatusMessage = "Recherche en cours...";
        var segments = await _repository.GetLineSegmentsAsync(machines.Select(m => m.MachineId).ToList(), from, to);
        var names = _machines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var shiftId = ShiftOptions.IndexOf(SelectedShift); // 0 = Tous, 1 Matin, 2 Après-midi, 3 Nuit
        var stops = StopDetector.Detect(segments, from, to, _settings, _teamSchedule, names)
            .Where(s => shiftId <= 0 || s.ShiftId == shiftId)
            .Where(s => SelectedTeam == AllOption || s.Team == SelectedTeam)
            .ToList();

        Results.Clear();
        foreach (var stop in stops)
            Results.Add(stop);

        TeamTotals.Clear();
        foreach (var team in stops.GroupBy(s => s.Team).OrderBy(g => g.Key))
            TeamTotals.Add(new TeamStopTotal(team.Key, team.Count(), team.Sum(s => s.NetMinutes),
                team.Where(s => !s.IsMostlyNoData).Sum(s => s.NetMinutes)));

        SummaryText = stops.Count == 0
            ? ""
            : $"{stops.Count} arrêt(s), durée totale hors pauses : {ProductionStop.FormatDuration(stops.Sum(s => s.NetMinutes))}";
        StatusMessage = stops.Count == 0 ? "Aucun arrêt sur cette période." : "";
        OnPropertyChanged(nameof(SettingsText));
    }

    [RelayCommand]
    private void Print()
    {
        if (Results.Count == 0)
        {
            StatusMessage = "Aucun arrêt à imprimer : lancez d'abord une recherche.";
            return;
        }

        var first = Results.Min(s => s.ShiftDate);
        var last = Results.Max(s => s.ShiftDate);
        var dialog = new SaveFileDialog
        {
            Filter = "Document PDF (*.pdf)|*.pdf",
            FileName = first == last ? $"fiche_arrets_{first:yyyyMMdd}.pdf" : $"fiche_arrets_{first:yyyyMMdd}_{last:yyyyMMdd}.pdf",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            StopSheetPdf.Export(Results.ToList(), _settings, dialog.FileName);
            StatusMessage = $"Fiche enregistrée : {dialog.FileName}";
            // Opened in the PDF viewer, ready to print.
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Echec de la fiche des arrets : {ex}");
            StatusMessage = $"Échec de l'impression : {ex.Message}";
        }
    }
}

// Stops of one team over the search: count, total time, and the part that
// is real machine stops (not "Sans donnees").
public sealed record TeamStopTotal(string Team, int Count, double TotalMinutes, double StopMinutes)
{
    public string TotalText => ProductionStop.FormatDuration(TotalMinutes);
    public string StopText => ProductionStop.FormatDuration(StopMinutes);
}
