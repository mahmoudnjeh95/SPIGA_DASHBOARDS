using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SwitchableConnectionFactory _activeFactory;
    private readonly ProductionRepository _repository;
    private readonly Action _onApplied;

    [ObservableProperty] private DatabaseProvider provider;
    [ObservableProperty] private string host = "";
    [ObservableProperty] private string database = "";
    [ObservableProperty] private SqlAuthMode authMode;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";

    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private string testResultMessage = "";
    [ObservableProperty] private bool? testResultSuccess;

    [ObservableProperty] private string machinesStatusMessage = "";

    [ObservableProperty] private int slideDurationSeconds;
    [ObservableProperty] private int dataRefreshIntervalMinutes;

    [ObservableProperty] private string team1Name = "";
    [ObservableProperty] private string team2Name = "";
    [ObservableProperty] private string team3Name = "";
    [ObservableProperty] private string team4Name = "";
    [ObservableProperty] private DateTime? rotationStartDate;

    [ObservableProperty] private string pdfDocumentPath = "";
    [ObservableProperty] private string pdfDocumentStatus = "";

    public ObservableCollection<MachineVisibilityOption> Machines { get; } = new();
    public ObservableCollection<TeamOption> TeamOptions { get; } = new();
    public ObservableCollection<TeamRosterDayViewModel> RosterDays { get; } = new();
    public ObservableCollection<NominalRateRowViewModel> NominalRates { get; } = new();

    [ObservableProperty] private string nominalRatesStatusMessage = "";

    public event EventHandler? SettingsApplied;

    public SettingsViewModel(
        SettingsService settingsService,
        AppSettings settings,
        SwitchableConnectionFactory activeFactory,
        ProductionRepository repository,
        Action onApplied)
    {
        _settingsService = settingsService;
        _settings = settings;
        _activeFactory = activeFactory;
        _repository = repository;
        _onApplied = onApplied;

        Provider = settings.Provider;
        Host = settings.SqlServer.Host;
        Database = settings.SqlServer.Database;
        AuthMode = settings.SqlServer.AuthMode;
        Username = settings.SqlServer.Username;
        // Password is intentionally left blank: DPAPI-protected value already saved is
        // reused unless the user types a new one (see Save()).

        SlideDurationSeconds = settings.SlideDurationSeconds;
        DataRefreshIntervalMinutes = settings.DataRefreshIntervalMinutes;

        // TeamOptions must exist before the TeamXName assignments below: setting
        // those properties fires OnTeamXNameChanged, which indexes into TeamOptions.
        TeamOptions.Add(new TeamOption { Index = -1, Name = "Repos" });
        TeamOptions.Add(new TeamOption { Index = 0, Name = "" });
        TeamOptions.Add(new TeamOption { Index = 1, Name = "" });
        TeamOptions.Add(new TeamOption { Index = 2, Name = "" });
        TeamOptions.Add(new TeamOption { Index = 3, Name = "" });

        var teamNames = settings.TeamRotation.TeamNames;
        Team1Name = teamNames.ElementAtOrDefault(0) ?? "";
        Team2Name = teamNames.ElementAtOrDefault(1) ?? "";
        Team3Name = teamNames.ElementAtOrDefault(2) ?? "";
        Team4Name = teamNames.ElementAtOrDefault(3) ?? "";
        RotationStartDate = settings.TeamRotation.RotationStartDate;

        LoadRosterDays(settings.TeamRotation.Pattern);

        PdfDocumentPath = settings.PdfDocumentPath;
        UpdatePdfDocumentStatus();

        foreach (var entry in settings.NominalRates.OrderBy(r => r.WeightGrams))
        {
            NominalRates.Add(new NominalRateRowViewModel
            {
                WeightText = NominalRateRowViewModel.Format(entry.WeightGrams),
                RateText = entry.RatePerMinute > 0 ? NominalRateRowViewModel.Format(entry.RatePerMinute) : "",
            });
        }

        _ = LoadMachinesAsync();
    }

    // Index in TeamOptions == TeamIndex + 1 (slot 0 is "Repos" at index -1).
    partial void OnTeam1NameChanged(string value) => TeamOptions[1].Name = value;
    partial void OnTeam2NameChanged(string value) => TeamOptions[2].Name = value;
    partial void OnTeam3NameChanged(string value) => TeamOptions[3].Name = value;
    partial void OnTeam4NameChanged(string value) => TeamOptions[4].Name = value;

    partial void OnPdfDocumentPathChanged(string value) => UpdatePdfDocumentStatus();

    private void UpdatePdfDocumentStatus()
    {
        if (string.IsNullOrWhiteSpace(PdfDocumentPath))
            PdfDocumentStatus = "Aucun document configure : la diapositive 8 sera ignoree.";
        else if (!File.Exists(PdfDocumentPath))
            PdfDocumentStatus = "Fichier introuvable a ce chemin.";
        else
            PdfDocumentStatus = $"Mis a jour le {File.GetLastWriteTime(PdfDocumentPath):dd/MM/yyyy HH:mm}.";
    }

    [RelayCommand]
    private void BrowsePdfDocument()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Documents PDF (*.pdf)|*.pdf",
            Title = "Choisir le document externe",
        };
        if (dialog.ShowDialog() == true)
            PdfDocumentPath = dialog.FileName;
    }

    private void LoadRosterDays(List<TeamRosterEntry> pattern)
    {
        RosterDays.Clear();
        var cycleLength = pattern.Count == 0 ? 4 : pattern.Max(p => p.CycleDay) + 1;
        for (var day = 0; day < cycleLength; day++)
        {
            RosterDays.Add(new TeamRosterDayViewModel
            {
                CycleDay = day,
                MatinTeamIndex = pattern.FirstOrDefault(p => p.CycleDay == day && p.ShiftId == 1)?.TeamIndex ?? -1,
                SoirTeamIndex = pattern.FirstOrDefault(p => p.CycleDay == day && p.ShiftId == 2)?.TeamIndex ?? -1,
                NuitTeamIndex = pattern.FirstOrDefault(p => p.CycleDay == day && p.ShiftId == 3)?.TeamIndex ?? -1,
            });
        }
    }

    [RelayCommand]
    private void AddRosterDay()
    {
        RosterDays.Add(new TeamRosterDayViewModel { CycleDay = RosterDays.Count });
    }

    [RelayCommand]
    private void RemoveRosterDay()
    {
        if (RosterDays.Count > 1)
            RosterDays.RemoveAt(RosterDays.Count - 1);
    }

    [RelayCommand]
    private void AddNominalRate() => NominalRates.Add(new NominalRateRowViewModel());

    [RelayCommand]
    private void RemoveNominalRate(NominalRateRowViewModel row) => NominalRates.Remove(row);

    // Parses the cadence table; on any bad cell, explains which one in
    // NominalRatesStatusMessage and returns null so the window stays open.
    private List<NominalRateEntry>? ParseNominalRates()
    {
        var entries = new List<NominalRateEntry>();
        foreach (var row in NominalRates)
        {
            if (row.WeightText.Trim().Length == 0 && row.RateText.Trim().Length == 0)
                continue;
            if (!NominalRateRowViewModel.TryParse(row.WeightText, out var weight) || weight <= 0)
            {
                NominalRatesStatusMessage = $"Poids invalide : \"{row.WeightText}\" (nombre de grammes attendu).";
                return null;
            }
            if (!NominalRateRowViewModel.TryParse(row.RateText, out var rate))
            {
                NominalRatesStatusMessage = $"Cadence invalide pour {row.WeightText} g : \"{row.RateText}\".";
                return null;
            }
            if (entries.Any(e => e.WeightGrams == weight))
            {
                NominalRatesStatusMessage = $"Le poids {row.WeightText} g apparait deux fois.";
                return null;
            }
            entries.Add(new NominalRateEntry { WeightGrams = weight, RatePerMinute = rate });
        }
        NominalRatesStatusMessage = "";
        return entries.OrderBy(e => e.WeightGrams).ToList();
    }

    public bool ValidateNominalRates() => ParseNominalRates() != null;

    private async Task LoadMachinesAsync()
    {
        Machines.Clear();
        try
        {
            var machines = await _repository.GetMachinesAsync();
            foreach (var machine in machines)
            {
                Machines.Add(new MachineVisibilityOption
                {
                    MachineId = machine.MachineId,
                    OriginalMachineName = machine.MachineName,
                    MachineName = machine.MachineName,
                    IsVisible = !_settings.HiddenMachineIds.Contains(machine.MachineId),
                });
            }
            MachinesStatusMessage = machines.Count == 0 ? "Aucune machine trouvee." : "";
        }
        catch (Exception ex)
        {
            MachinesStatusMessage = $"Impossible de charger la liste des machines : {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshMachinesAsync() => await LoadMachinesAsync();

    [RelayCommand]
    private void OpenConsole() => AppLogger.OpenConsole();

    private IDbConnectionFactory BuildFactoryFromFields()
    {
        if (Provider == DatabaseProvider.Sqlite)
            return new SqliteConnectionFactory(DatabasePathResolver.ResolveSqlitePath());

        var sqlSettings = new SqlServerSettings
        {
            Host = Host,
            Database = Database,
            AuthMode = AuthMode,
            Username = Username,
            EncryptedPassword = string.IsNullOrEmpty(Password)
                ? _settings.SqlServer.EncryptedPassword
                : PasswordProtector.Protect(Password),
        };
        return new SqlServerConnectionFactory(sqlSettings);
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResultMessage = "";
        TestResultSuccess = null;

        AppLogger.Log(Provider == DatabaseProvider.SqlServer
            ? $"Cible : SQL Server {Host}/{Database} (auth {AuthMode})"
            : "Cible : SQLite (dev)");

        var (success, message) = await ConnectionTester.TestAsync(BuildFactoryFromFields());

        TestResultSuccess = success;
        TestResultMessage = message;
        IsTesting = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _settings.Provider = Provider;
        _settings.SqlServer.Host = Host;
        _settings.SqlServer.Database = Database;
        _settings.SqlServer.AuthMode = AuthMode;
        _settings.SqlServer.Username = Username;
        if (!string.IsNullOrEmpty(Password))
            _settings.SqlServer.EncryptedPassword = PasswordProtector.Protect(Password);

        _settings.HiddenMachineIds = Machines.Where(m => !m.IsVisible).Select(m => m.MachineId).ToList();

        _settings.SlideDurationSeconds = Math.Max(1, SlideDurationSeconds);
        _settings.DataRefreshIntervalMinutes = Math.Max(1, DataRefreshIntervalMinutes);

        _settings.TeamRotation.TeamNames = new[] { Team1Name, Team2Name, Team3Name, Team4Name };
        if (RotationStartDate.HasValue)
            _settings.TeamRotation.RotationStartDate = RotationStartDate.Value;

        var pattern = new List<TeamRosterEntry>();
        foreach (var day in RosterDays)
        {
            if (day.MatinTeamIndex >= 0)
                pattern.Add(new TeamRosterEntry { CycleDay = day.CycleDay, ShiftId = 1, TeamIndex = day.MatinTeamIndex });
            if (day.SoirTeamIndex >= 0)
                pattern.Add(new TeamRosterEntry { CycleDay = day.CycleDay, ShiftId = 2, TeamIndex = day.SoirTeamIndex });
            if (day.NuitTeamIndex >= 0)
                pattern.Add(new TeamRosterEntry { CycleDay = day.CycleDay, ShiftId = 3, TeamIndex = day.NuitTeamIndex });
        }
        _settings.TeamRotation.Pattern = pattern;

        _settings.PdfDocumentPath = PdfDocumentPath;
        _settings.NominalRates = ParseNominalRates() ?? _settings.NominalRates;

        _settingsService.Save(_settings);
        _activeFactory.SwitchTo(BuildFactoryFromFields());
        AppLogger.Log("Parametres de connexion enregistres et appliques.");

        foreach (var machine in Machines)
        {
            var trimmedName = machine.MachineName.Trim();
            if (trimmedName.Length > 0 && trimmedName != machine.OriginalMachineName)
            {
                try
                {
                    await _repository.UpdateMachineNameAsync(machine.MachineId, trimmedName);
                }
                catch (Exception ex)
                {
                    MachinesStatusMessage = $"Impossible de renommer {machine.MachineId} : {ex.Message}";
                }
            }
        }

        _onApplied();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }
}
