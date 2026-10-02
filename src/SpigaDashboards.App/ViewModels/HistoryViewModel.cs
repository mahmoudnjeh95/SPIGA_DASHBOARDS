using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels;

// Spec S6: queryable history (day/poste/equipe/machine) + Excel/PDF export.
public partial class HistoryViewModel : ObservableObject
{
    private const string AllOption = "Toutes";

    private readonly ProductionRepository _repository;
    private readonly TeamScheduleService _teamSchedule;

    [ObservableProperty] private DateTime? fromDate = DateTime.Now.Date.AddDays(-7);
    [ObservableProperty] private DateTime? toDate = DateTime.Now.Date;
    [ObservableProperty] private string selectedShift = AllOption;
    [ObservableProperty] private string selectedTeam = AllOption;
    [ObservableProperty] private string selectedMachine = AllOption;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private KpiSummary summary = new();

    public List<string> ShiftOptions { get; } = new() { AllOption, "Matin", "Soir", "Nuit" };
    public ObservableCollection<string> TeamOptions { get; } = new();
    public ObservableCollection<string> MachineOptions { get; } = new();
    public ObservableCollection<HistoryRow> Results { get; } = new();

    public HistoryViewModel(ProductionRepository repository, TeamScheduleService teamSchedule, AppSettings settings)
    {
        _repository = repository;
        _teamSchedule = teamSchedule;

        TeamOptions.Add(AllOption);
        foreach (var name in settings.TeamRotation.TeamNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
                TeamOptions.Add(name);
        }

        MachineOptions.Add(AllOption);
        _ = LoadMachinesAsync();
    }

    private async Task LoadMachinesAsync()
    {
        var machines = await _repository.GetMachinesAsync();
        foreach (var machine in machines)
            MachineOptions.Add(machine.MachineId);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        // An unhandled exception here (async command) would take the whole app
        // down, so surface any failure as a status message instead.
        try
        {
            await RunSearchAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Echec de la recherche : {ex.Message}";
        }
    }

    private async Task RunSearchAsync()
    {
        var from = (FromDate ?? DateTime.Now.Date.AddDays(-7)).Date;
        var to = (ToDate ?? DateTime.Now.Date).Date.AddDays(1);

        int? shiftId = SelectedShift switch
        {
            "Matin" => 1,
            "Soir" => 2,
            "Nuit" => 3,
            _ => null,
        };
        var machineId = SelectedMachine == AllOption ? null : SelectedMachine;

        var lots = await _repository.GetLotsFilteredAsync(from, to, shiftId, machineId);

        var lotsWithTeam = lots
            .Select(l => (Lot: l, Team: _teamSchedule.GetTeamForShift(TeamScheduleService.GetShiftDate(l.StartTime, l.ShiftId), l.ShiftId)))
            .ToList();

        if (SelectedTeam != AllOption)
            lotsWithTeam = lotsWithTeam.Where(x => x.Team == SelectedTeam).ToList();

        var rowList = lotsWithTeam.Select(x => new HistoryRow
        {
            StartTime = x.Lot.StartTime,
            MachineId = x.Lot.MachineId,
            ShiftName = KpiCalculator.ShiftName(x.Lot.ShiftId),
            Team = x.Team,
            ProductName = x.Lot.ProductName,
            TargetNetWeight = x.Lot.TargetNetWeight,
            AvgWeight = x.Lot.AvgWeight,
            TotalBags = x.Lot.TotalBags,
            BagsInTolerance = x.Lot.BagsInTolerance,
            BagsOverTolerance = x.Lot.BagsOverTolerance,
            BagsUnderTolerance = x.Lot.BagsUnderTolerance,
            IsConform = x.Lot.IsConform,
        }).ToList();

        Results.Clear();
        foreach (var row in rowList)
            Results.Add(row);

        // Availability (needed for TRS) isn't meaningful for a filtered, possibly
        // multi-machine, non-contiguous lot set, so it's left at 0 and TRS isn't
        // shown in the summary strip -- this view is about raw records, not OEE.
        Summary = KpiCalculator.Summarize(lotsWithTeam.Select(x => x.Lot).ToList(), availabilityPercent: 0);
        StatusMessage = $"{rowList.Count} lot(s) trouve(s).";
    }

    [RelayCommand]
    private void ExportExcel()
    {
        if (Results.Count == 0)
        {
            StatusMessage = "Aucun resultat a exporter.";
            return;
        }

        var dialog = new SaveFileDialog { Filter = "Classeur Excel (*.xlsx)|*.xlsx", FileName = "historique.xlsx" };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            ExportService.ExportToExcel(Results.ToList(), dialog.FileName);
            StatusMessage = $"Exporte : {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Echec de l'export Excel : {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportPdf()
    {
        if (Results.Count == 0)
        {
            StatusMessage = "Aucun resultat a exporter.";
            return;
        }

        var dialog = new SaveFileDialog { Filter = "Document PDF (*.pdf)|*.pdf", FileName = "historique.pdf" };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            ExportService.ExportToPdf(Summary, Results.ToList(), dialog.FileName);
            StatusMessage = $"Exporte : {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Echec de l'export PDF : {ex.Message}";
        }
    }
}
