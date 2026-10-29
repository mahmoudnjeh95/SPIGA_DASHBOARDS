using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels;

// Spec S6: queryable history (dates / secteur / machine / poste / equipe /
// poids) + Excel/PDF report export. Opens with the dashboard's current
// period and filters.
public partial class HistoryViewModel : ObservableObject
{
    private const string AllOption = "Toutes";
    private static readonly MachinePick AllMachines = new(AllOption, Array.Empty<string>());

    private readonly ProductionRepository _repository;
    private readonly TeamScheduleService _teamSchedule;
    private readonly AppSettings _settings;
    private List<Machine> _machines = new();

    // Last search, kept for the exports (same buckets as on screen).
    private ProductionReport? _report;

    [ObservableProperty] private DateTime? fromDate = DateTime.Now.Date.AddDays(-7);
    [ObservableProperty] private DateTime? toDate = DateTime.Now.Date;
    [ObservableProperty] private string selectedSector = AllOption;
    [ObservableProperty] private string selectedShift = AllOption;
    [ObservableProperty] private string selectedTeam = AllOption;
    [ObservableProperty] private MachinePick selectedMachine = AllMachines;
    [ObservableProperty] private string selectedWeight = AllOption;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private KpiSummary summary = new();
    // A search in progress: loading motion over the results.
    [ObservableProperty] private bool isBusy;

    public List<string> SectorOptions { get; } = new() { AllOption, "Couscous", "Pate courte", "Pate longue" };
    public List<string> ShiftOptions { get; } = new() { AllOption, "Matin", "Après-midi", "Nuit" };
    public ObservableCollection<string> TeamOptions { get; } = new();
    public ObservableCollection<MachinePick> MachineOptions { get; } = new();
    public ObservableCollection<string> WeightOptions { get; } = new();
    public ObservableCollection<HistoryRow> Results { get; } = new();

    public HistoryViewModel(ProductionRepository repository, TeamScheduleService teamSchedule, AppSettings settings,
        DashboardFilter? initialFilter = null, (DateTime From, DateTime To)? initialRange = null)
    {
        _repository = repository;
        _teamSchedule = teamSchedule;
        _settings = settings;

        TeamOptions.Add(AllOption);
        foreach (var name in teamSchedule.TeamNames)
            TeamOptions.Add(name);

        WeightOptions.Add(AllOption);
        foreach (var weight in settings.NominalRates.Where(r => r.WeightGrams > 0).Select(r => r.WeightGrams).Distinct().Order())
            WeightOptions.Add(weight.ToString("0.##", CultureInfo.InvariantCulture) + " g");

        MachineOptions.Add(AllMachines);

        if (initialRange is { } range)
        {
            FromDate = range.From.Date;
            ToDate = range.To.AddDays(-1).Date;
        }
        if (initialFilter is { } filter)
        {
            SelectedSector = filter.Sector ?? AllOption;
            SelectedShift = filter.ShiftId is { } s ? KpiCalculator.ShiftName(s) : AllOption;
            SelectedTeam = filter.Team is { } t && TeamOptions.Contains(t) ? t : AllOption;
            var weightText = filter.TargetWeight?.ToString("0.##", CultureInfo.InvariantCulture) + " g";
            SelectedWeight = WeightOptions.Contains(weightText) ? weightText : AllOption;
            // The dashboard's machine selection, as one entry ("Peseuse PC01, Peseuse PC02").
            if (filter.MachineIds.Count > 0)
                MachineOptions.Add(SelectedMachine = new MachinePick(filter.MachinesLabel, filter.MachineIds));
        }

        _ = LoadMachinesAsync();
    }

    private async Task LoadMachinesAsync()
    {
        try
        {
            _machines = await _repository.GetMachinesAsync();
            foreach (var machine in _machines.Where(m => !MachineOptions.Any(o => o.MachineIds.SequenceEqual(new[] { m.MachineId }))))
                MachineOptions.Add(new MachinePick(machine.MachineName, new[] { machine.MachineId }));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossible de charger les machines : {ex.Message}";
        }
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
            StatusMessage = $"Echec de la recherche : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private DashboardFilter BuildFilter() => new()
    {
        Sector = SelectedSector == AllOption ? null : SelectedSector,
        MachineIds = SelectedMachine.MachineIds,
        MachineNames = SelectedMachine.MachineIds.Select(MachineName).ToList(),
        ShiftId = SelectedShift switch { "Matin" => 1, "Après-midi" => 2, "Nuit" => 3, _ => null },
        Team = SelectedTeam == AllOption ? null : SelectedTeam,
        TargetWeight = SelectedWeight == AllOption ? null : double.Parse(SelectedWeight.Replace(" g", ""), CultureInfo.InvariantCulture),
    };

    private string MachineName(string machineId)
        => _machines.FirstOrDefault(m => m.MachineId == machineId)?.MachineName ?? machineId;

    private async Task RunSearchAsync()
    {
        var from = (FromDate ?? DateTime.Now.Date.AddDays(-7)).Date;
        var to = (ToDate ?? DateTime.Now.Date).Date.AddDays(1);
        if (to <= from)
            (from, to) = (to.AddDays(-1), from.AddDays(1));

        if (_machines.Count == 0)
            _machines = await _repository.GetMachinesAsync();
        var filter = BuildFilter();
        var machines = _machines.Where(filter.IncludesMachine).ToList();

        StatusMessage = "Recherche en cours...";
        var lots = filter.Apply(
            await _repository.GetLotsForMachinesAsync(machines.Select(m => m.MachineId).ToList(), from, to),
            _teamSchedule);

        var sectors = machines.ToDictionary(m => m.MachineId, m => m.Sector);
        var rowList = lots
            .Where(l => l.IsProducing)
            .OrderByDescending(l => l.StartTime)
            .ThenBy(l => l.MachineId)
            .Select(l =>
            {
                var k = KpiCalculator.Summarize(new[] { l }, KpiCalculator.AvailabilityPercent(new[] { l }));
                var avg = l.AvgWeight;
                return new HistoryRow
                {
                    StartTime = l.StartTime,
                    MachineId = l.MachineId,
                    MachineName = MachineName(l.MachineId),
                    Sector = sectors.GetValueOrDefault(l.MachineId, ""),
                    ShiftName = KpiCalculator.ShiftName(l.ShiftId),
                    Team = _teamSchedule.TeamOf(l),
                    ProductName = l.ProductName ?? "",
                    TargetNetWeight = l.TargetNetWeight,
                    TareGrams = _settings.FindTare(l.MachineId, l.TargetNetWeight),
                    AvgWeight = avg is { } a ? Math.Round(a, 1) : null,
                    DeviationGrams = k.WeightDeviationGrams,
                    ProductionKg = Math.Round(l.TotalBags * (avg ?? l.TargetNetWeight) / 1000.0, 1),
                    TotalBags = l.TotalBags,
                    BagsInTolerance = l.BagsInTolerance,
                    BagsOverTolerance = l.BagsOverTolerance,
                    BagsUnderTolerance = l.BagsUnderTolerance,
                    SpeedBagsPerMinute = k.SpeedBagsPerMinute ?? 0,
                    MaxSpeedBagsPerMinute = k.MaxSpeedBagsPerMinute,
                    AvailabilityPercent = k.AvailabilityPercent,
                    TrsPercent = k.TrsPercent,
                    IsConform = l.IsConform,
                };
            })
            .ToList();

        Results.Clear();
        foreach (var row in rowList)
            Results.Add(row);

        _report = ProductionReport.Build(lots, machines, rowList, _teamSchedule, _settings, from, to, filter.Describe());
        Summary = _report.Summary;
        StatusMessage = $"{rowList.Count} ligne(s) trouvee(s) (une par machine, heure et production).";
    }

    [RelayCommand]
    private void ExportExcel() => Export("Classeur Excel (*.xlsx)|*.xlsx", "xlsx", ExportService.ExportToExcel);

    [RelayCommand]
    private void ExportPdf() => Export("Document PDF (*.pdf)|*.pdf", "pdf", ExportService.ExportToPdf);

    private void Export(string dialogFilter, string extension, Action<ProductionReport, string> export)
    {
        if (_report == null || Results.Count == 0)
        {
            StatusMessage = "Aucun resultat a exporter : lancez d'abord une recherche.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = dialogFilter,
            FileName = $"rapport_production_{_report.From:yyyyMMdd}_{_report.To.AddDays(-1):yyyyMMdd}.{extension}",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            export(_report, dialog.FileName);
            StatusMessage = $"Exporte : {dialog.FileName}";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Echec de l'export {extension} : {ex}");
            StatusMessage = $"Echec de l'export : {ex.Message}";
        }
    }
}
