using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels.Slides;

namespace SpigaDashboards.App.ViewModels;

// Tabs of the Parametres window, in order (SettingsViewModel.SelectedTab).
public enum SettingsTab { Connexion, Machines, Affichage, Cadences, Pauses, Equipes, Couleurs }

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SwitchableConnectionFactory _activeFactory;
    private readonly ProductionRepository _repository;
    private readonly SharedSettingsSync _sharedSettings;
    // Shared part as it was when the window opened (see SharedSettingsSync.PushAsync).
    private string _sharedAtOpen;
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

    // Set when Enregistrer could not share the shared settings: the window stays open.
    [ObservableProperty] private string sharedSettingsMessage = "";

    [ObservableProperty] private int slideDurationSeconds;
    [ObservableProperty] private bool showStopAlerts;
    // "Replacer" ticked: the pop-up goes back to the top right on save.
    [ObservableProperty] private bool resetStopAlertPosition;
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
    public ObservableCollection<MachineChoice> MachineChoices { get; } = new();

    [ObservableProperty] private string nominalRatesStatusMessage = "";

    public ObservableCollection<PlannedBreakRowViewModel> PlannedBreaks { get; } = new();
    [ObservableProperty] private string stopThresholdText = "";
    [ObservableProperty] private string breaksStatusMessage = "";

    // Affichage: every slide the deck can have, in rotation order. Filled
    // once the machine list is loaded (LoadMachinesAsync).
    public ObservableCollection<SlideRowViewModel> Slides { get; } = new();
    [ObservableProperty] private string slidesStatusMessage = "";
    private List<Machine> _activeMachines = new();

    // Tab shown (SettingsTab); Enregistrer moves to the tab with the bad cell.
    [ObservableProperty] private int selectedTab;

    // Couleurs: the limits of ColorThresholds, as typed.
    [ObservableProperty] private string trsWarningText = "";
    [ObservableProperty] private string availabilityGoodText = "";
    [ObservableProperty] private string availabilityWarningText = "";
    [ObservableProperty] private string performanceGoodText = "";
    [ObservableProperty] private string performanceWarningText = "";
    [ObservableProperty] private string performanceHighText = "";
    [ObservableProperty] private string qualityGoodText = "";
    [ObservableProperty] private string qualityWarningText = "";
    [ObservableProperty] private string colorsStatusMessage = "";

    // Couleurs: scrolling stop bar (StopTickerSettings), as typed, and the
    // preview bar drawn from it.
    [ObservableProperty] private string tickerSpeedText = "";
    [ObservableProperty] private string tickerPanneColorText = "";
    [ObservableProperty] private string tickerOtherColorText = "";
    [ObservableProperty] private string tickerPlannedColorText = "";
    [ObservableProperty] private string tickerTextColorText = "";
    [ObservableProperty] private string tickerStatusMessage = "";
    [ObservableProperty] private double tickerPreviewSpeed = 120;
    [ObservableProperty] private Brush tickerPanneBrush = Brushes.Transparent;
    [ObservableProperty] private Brush tickerOtherBrush = Brushes.Transparent;
    [ObservableProperty] private Brush tickerPlannedBrush = Brushes.Transparent;
    [ObservableProperty] private Brush tickerTextBrush = Brushes.White;

    public event EventHandler? SettingsApplied;

    public SettingsViewModel(
        SettingsService settingsService,
        AppSettings settings,
        SwitchableConnectionFactory activeFactory,
        ProductionRepository repository,
        SharedSettingsSync sharedSettings,
        Action onApplied)
    {
        _settingsService = settingsService;
        _settings = settings;
        _activeFactory = activeFactory;
        _repository = repository;
        _sharedSettings = sharedSettings;
        _sharedAtOpen = SharedSettings.Serialize(settings);
        _onApplied = onApplied;

        Provider = settings.Provider;
        Host = settings.SqlServer.Host;
        Database = settings.SqlServer.Database;
        AuthMode = settings.SqlServer.AuthMode;
        Username = settings.SqlServer.Username;
        // Password is intentionally left blank: DPAPI-protected value already saved is
        // reused unless the user types a new one (see Save()).

        SlideDurationSeconds = settings.SlideDurationSeconds;
        ShowStopAlerts = settings.ShowStopAlerts;
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

        // Machines already used by a row are listed right away, the others once
        // the machine list is loaded (LoadMachinesAsync).
        MachineChoices.Add(new MachineChoice("", "Toutes"));
        foreach (var id in settings.NominalRates.Select(r => r.MachineId ?? "").Where(id => id.Length > 0).Distinct().Order())
            MachineChoices.Add(new MachineChoice(id, id));

        foreach (var entry in settings.NominalRates.OrderBy(r => r.MachineId).ThenBy(r => r.WeightGrams))
        {
            NominalRates.Add(new NominalRateRowViewModel
            {
                MachineId = entry.MachineId ?? "",
                WeightText = NominalRateRowViewModel.Format(entry.WeightGrams),
                RateText = entry.RatePerMinute > 0 ? NominalRateRowViewModel.Format(entry.RatePerMinute) : "",
                TareText = entry.TareGrams > 0 ? NominalRateRowViewModel.Format(entry.TareGrams) : "",
            });
        }

        foreach (var b in settings.PlannedBreaks.OrderBy(b => b.Start))
            PlannedBreaks.Add(new PlannedBreakRowViewModel
            {
                StartText = PlannedBreakRowViewModel.Format(b.Start),
                DurationText = b.DurationMinutes.ToString(CultureInfo.InvariantCulture),
            });
        StopThresholdText = settings.StopThresholdMinutes.ToString(CultureInfo.InvariantCulture);

        LoadColors(settings.Colors);
        LoadTicker(settings.StopTicker);

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
            PdfDocumentStatus = "Aucun document configuré : la diapositive document sera ignorée.";
        else if (!File.Exists(PdfDocumentPath))
            PdfDocumentStatus = "Fichier introuvable à ce chemin.";
        else
            PdfDocumentStatus = $"Mis à jour le {File.GetLastWriteTime(PdfDocumentPath):dd/MM/yyyy HH:mm}.";
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

    // No document: the PDF slide is skipped (applied on Enregistrer).
    [RelayCommand]
    private void ClearPdfDocument() => PdfDocumentPath = "";

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

    // Copies a row for another machine: the usual way to set a machine's own
    // cadence / sachet vide from the "Toutes" values.
    [RelayCommand]
    private void DuplicateNominalRate(NominalRateRowViewModel row)
        => NominalRates.Insert(NominalRates.IndexOf(row) + 1, new NominalRateRowViewModel
        {
            MachineId = row.MachineId,
            WeightText = row.WeightText,
            RateText = row.RateText,
            TareText = row.TareText,
        });

    [RelayCommand]
    private void RemoveNominalRate(NominalRateRowViewModel row) => NominalRates.Remove(row);

    // Parses the cadence table; on any bad cell, explains which one in
    // NominalRatesStatusMessage and returns null so the window stays open.
    private List<NominalRateEntry>? ParseNominalRates()
    {
        var entries = new List<NominalRateEntry>();
        foreach (var row in NominalRates)
        {
            if (row.WeightText.Trim().Length == 0 && row.RateText.Trim().Length == 0 && row.TareText.Trim().Length == 0)
                continue;
            var machine = row.MachineId ?? "";
            var where = machine.Length == 0 ? "" : $" ({machine})";
            if (!NominalRateRowViewModel.TryParse(row.WeightText, out var weight) || weight <= 0)
            {
                NominalRatesStatusMessage = $"Poids invalide{where} : « {row.WeightText} » (nombre de grammes attendu).";
                return null;
            }
            if (!NominalRateRowViewModel.TryParse(row.RateText, out var rate))
            {
                NominalRatesStatusMessage = $"Cadence invalide pour {row.WeightText} g{where} : « {row.RateText} ».";
                return null;
            }
            if (!NominalRateRowViewModel.TryParse(row.TareText, out var tare) || tare >= weight)
            {
                NominalRatesStatusMessage = $"Poids du sachet vide invalide pour {row.WeightText} g{where} : « {row.TareText} ».";
                return null;
            }
            if (entries.Any(e => e.MachineId == machine && e.WeightGrams == weight))
            {
                NominalRatesStatusMessage = $"Le poids {row.WeightText} g apparaît deux fois pour {(machine.Length == 0 ? "Toutes" : machine)}.";
                return null;
            }
            entries.Add(new NominalRateEntry { MachineId = machine, WeightGrams = weight, RatePerMinute = rate, TareGrams = tare });
        }
        NominalRatesStatusMessage = "";
        return entries.OrderBy(e => e.MachineId).ThenBy(e => e.WeightGrams).ToList();
    }

    // TRS cible per machine; blank = default. Null (message shown) on a bad cell.
    private Dictionary<string, double>? ParseTrsTargets()
    {
        var targets = new Dictionary<string, double>();
        foreach (var machine in Machines)
        {
            if (!NominalRateRowViewModel.TryParse(machine.TrsTargetText, out var target) || target > 100)
            {
                MachinesStatusMessage = $"TRS cible invalide pour {ChoiceLabel(machine)}  : « {machine.TrsTargetText} » (0 à 100 %).";
                return null;
            }
            if (target > 0)
                targets[machine.MachineId] = target;
        }
        return targets;
    }

    // TRS tolerance per machine, in points; blank = Couleurs default (not
    // stored). Null (message shown) on a bad cell.
    private Dictionary<string, double>? ParseTrsTolerances()
    {
        var tolerances = new Dictionary<string, double>();
        foreach (var machine in Machines)
        {
            if (machine.TrsToleranceText.Trim().Length == 0)
                continue;
            if (!NominalRateRowViewModel.TryParse(machine.TrsToleranceText, out var tolerance) || tolerance > 100)
            {
                MachinesStatusMessage = $"Tolérance invalide pour {ChoiceLabel(machine)} : « {machine.TrsToleranceText} » (0 à 100 %, vide = règle de l'onglet Couleurs).";
                return null;
            }
            tolerances[machine.MachineId] = tolerance;
        }
        return tolerances;
    }

    // Weight-deviation limits per machine (every box filled, orange beyond
    // green on each side). Null (message shown) on a bad cell.
    private Dictionary<string, WeightBand>? ParseWeightBands()
    {
        var bands = new Dictionary<string, WeightBand>();
        foreach (var machine in Machines)
        {
            double? Read(string text, string what)
            {
                if (text.Trim().Length > 0 && NominalRateRowViewModel.TryParse(text, out var value) && value <= 100)
                    return value;
                MachinesStatusMessage = $"Écart de poids {what} invalide pour {ChoiceLabel(machine)} : « {text} » (0 à 100 %).";
                return null;
            }

            if (Read(machine.WeightOverGoodText, "au-dessus (vert)") is not { } overGood
                || Read(machine.WeightOverWarningText, "au-dessus (orange)") is not { } overWarning
                || Read(machine.WeightUnderWarningText, "en dessous (vert)") is not { } underWarning
                || Read(machine.WeightUnderBadText, "en dessous (orange)") is not { } underBad)
                return null;
            if (overWarning < overGood || underBad < underWarning)
            {
                MachinesStatusMessage = $"Écart de poids pour {ChoiceLabel(machine)} : la limite orange doit être supérieure ou égale à la limite verte, de chaque côté.";
                return null;
            }
            bands[machine.MachineId] = new WeightBand
            {
                OverGood = overGood,
                OverWarning = overWarning,
                UnderWarning = underWarning,
                UnderBad = underBad,
            };
        }
        return bands;
    }

    [RelayCommand]
    private void AddPlannedBreak() => PlannedBreaks.Add(new PlannedBreakRowViewModel { DurationText = "20" });

    [RelayCommand]
    private void RemovePlannedBreak(PlannedBreakRowViewModel row) => PlannedBreaks.Remove(row);

    // Pauses table; null (message shown) on a bad cell. Blank rows are skipped.
    private List<PlannedBreak>? ParsePlannedBreaks()
    {
        var breaks = new List<PlannedBreak>();
        foreach (var row in PlannedBreaks)
        {
            if (row.StartText.Trim().Length == 0 && row.DurationText.Trim().Length == 0)
                continue;
            if (!PlannedBreakRowViewModel.TryParseStart(row.StartText, out var start))
            {
                BreaksStatusMessage = $"Heure de pause invalide : « {row.StartText} » (format 10:00 attendu).";
                return null;
            }
            if (!int.TryParse(row.DurationText.Trim(), out var minutes) || minutes <= 0 || minutes > 480)
            {
                BreaksStatusMessage = $"Durée invalide pour la pause de {row.StartText} : « {row.DurationText} » (1 à 480 min).";
                return null;
            }
            breaks.Add(new PlannedBreak { Start = start, DurationMinutes = minutes });
        }
        BreaksStatusMessage = "";
        return breaks.OrderBy(b => b.Start).ToList();
    }

    private int? ParseStopThreshold()
    {
        if (int.TryParse(StopThresholdText.Trim(), out var minutes) && minutes >= 1 && minutes <= 240)
            return minutes;
        BreaksStatusMessage = $"Seuil d'arrêt invalide : « {StopThresholdText} » (1 à 240 min).";
        return null;
    }

    // Every table cell checked before saving; false = a message says which
    // one, on the tab brought to the front.
    public bool Validate()
    {
        SettingsTab? bad = ParseTrsTargets() == null || ParseTrsTolerances() == null || ParseWeightBands() == null ? SettingsTab.Machines
            : ParseSlides() == null ? SettingsTab.Affichage
            : ParseNominalRates() == null ? SettingsTab.Cadences
            : ParsePlannedBreaks() == null || ParseStopThreshold() == null ? SettingsTab.Pauses
            : ParseColors() == null || ParseTicker() == null ? SettingsTab.Couleurs
            : null;
        if (bad is { } tab)
            SelectedTab = (int)tab;
        return bad == null;
    }

    private void LoadColors(ColorThresholds c)
    {
        TrsWarningText = NominalRateRowViewModel.Format(c.TrsWarningPercentOfTarget);
        AvailabilityGoodText = NominalRateRowViewModel.Format(c.AvailabilityGood);
        AvailabilityWarningText = NominalRateRowViewModel.Format(c.AvailabilityWarning);
        PerformanceGoodText = NominalRateRowViewModel.Format(c.PerformanceGood);
        PerformanceWarningText = NominalRateRowViewModel.Format(c.PerformanceWarning);
        PerformanceHighText = NominalRateRowViewModel.Format(c.PerformanceHigh);
        QualityGoodText = NominalRateRowViewModel.Format(c.QualityGood);
        QualityWarningText = NominalRateRowViewModel.Format(c.QualityWarning);
        ColorsStatusMessage = "";
    }

    // Back to the bands the app shipped with (applied on Enregistrer).
    [RelayCommand]
    private void ResetColors() => LoadColors(new ColorThresholds());

    // Couleurs tab; null (message shown) on a blank, bad or inconsistent value.
    private ColorThresholds? ParseColors()
    {
        double? Read(string text, string what, double max)
        {
            if (text.Trim().Length > 0 && NominalRateRowViewModel.TryParse(text, out var value) && value <= max)
                return value;
            ColorsStatusMessage = $"{what} invalide : « {text} » (nombre de 0 à {max:0} attendu).";
            return null;
        }
        bool Ordered(double low, double high, string message)
        {
            if (low <= high)
                return true;
            ColorsStatusMessage = message;
            return false;
        }

        if (Read(TrsWarningText, "TRS : seuil orange", 100) is not { } trsWarning
            || Read(AvailabilityGoodText, "Disponibilité : seuil vert", 100) is not { } availabilityGood
            || Read(AvailabilityWarningText, "Disponibilité : seuil orange", 100) is not { } availabilityWarning
            || Read(PerformanceGoodText, "Performance : seuil vert", 200) is not { } performanceGood
            || Read(PerformanceWarningText, "Performance : seuil orange", 200) is not { } performanceWarning
            || Read(PerformanceHighText, "Performance : seuil haut", 200) is not { } performanceHigh
            || Read(QualityGoodText, "Conformité : seuil vert", 100) is not { } qualityGood
            || Read(QualityWarningText, "Conformité : seuil orange", 100) is not { } qualityWarning)
            return null;

        if (!Ordered(availabilityWarning, availabilityGood, "Disponibilité : le seuil orange doit être inférieur ou égal au seuil vert.")
            || !Ordered(performanceWarning, performanceGood, "Performance : le seuil orange doit être inférieur ou égal au seuil vert.")
            || !Ordered(performanceGood, performanceHigh, "Performance : le seuil haut doit être supérieur ou égal au seuil vert.")
            || !Ordered(qualityWarning, qualityGood, "Conformité : le seuil orange doit être inférieur ou égal au seuil vert."))
            return null;

        ColorsStatusMessage = "";
        return new ColorThresholds
        {
            TrsWarningPercentOfTarget = trsWarning,
            AvailabilityGood = availabilityGood,
            AvailabilityWarning = availabilityWarning,
            PerformanceGood = performanceGood,
            PerformanceWarning = performanceWarning,
            PerformanceHigh = performanceHigh,
            QualityGood = qualityGood,
            QualityWarning = qualityWarning,
            // No longer edited here (Machines tab): the fallback stays as is.
            WeightOverGood = _settings.Colors.WeightOverGood,
            WeightOverWarning = _settings.Colors.WeightOverWarning,
            WeightUnderWarning = _settings.Colors.WeightUnderWarning,
            WeightUnderBad = _settings.Colors.WeightUnderBad,
        };
    }

    private void LoadTicker(StopTickerSettings t)
    {
        TickerSpeedText = t.SpeedPixelsPerSecond.ToString(CultureInfo.InvariantCulture);
        TickerPanneColorText = t.PanneColor;
        TickerOtherColorText = t.OtherColor;
        TickerPlannedColorText = t.PlannedColor;
        TickerTextColorText = t.TextColor;
        TickerStatusMessage = "";
    }

    [RelayCommand]
    private void ResetTicker() => LoadTicker(new StopTickerSettings());

    // The preview follows what is typed; a colour not understood (yet) keeps
    // the last good one.
    partial void OnTickerSpeedTextChanged(string value)
    {
        if (int.TryParse(value.Trim(), out var speed) && speed >= StopTickerSettings.MinSpeed && speed <= StopTickerSettings.MaxSpeed)
            TickerPreviewSpeed = speed;
    }
    partial void OnTickerPanneColorTextChanged(string value) => TickerPanneBrush = ToBrush(value) ?? TickerPanneBrush;
    partial void OnTickerOtherColorTextChanged(string value) => TickerOtherBrush = ToBrush(value) ?? TickerOtherBrush;
    partial void OnTickerPlannedColorTextChanged(string value) => TickerPlannedBrush = ToBrush(value) ?? TickerPlannedBrush;
    partial void OnTickerTextColorTextChanged(string value) => TickerTextBrush = ToBrush(value) ?? TickerTextBrush;

    // "#RRGGBB", "#AARRGGBB" or a colour name ("Red", "Orange"...).
    private static Brush? ToBrush(string text)
    {
        try
        {
            if (text.Trim().Length > 0 && ColorConverter.ConvertFromString(text.Trim()) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }
        return null;
    }

    // Couleurs tab, stop bar; null (message shown) on a bad value.
    private StopTickerSettings? ParseTicker()
    {
        if (!int.TryParse(TickerSpeedText.Trim(), out var speed) || speed < StopTickerSettings.MinSpeed || speed > StopTickerSettings.MaxSpeed)
        {
            TickerStatusMessage = $"Vitesse invalide : « {TickerSpeedText} » ({StopTickerSettings.MinSpeed} à {StopTickerSettings.MaxSpeed} pixels par seconde).";
            return null;
        }
        string? ReadColor(string text, string what)
        {
            if (ToBrush(text) != null)
                return text.Trim();
            TickerStatusMessage = $"Couleur « {what} » invalide : « {text} » (ex. #E74C3C ou Red).";
            return null;
        }
        if (ReadColor(TickerPanneColorText, "Panne / sans cause") is not { } panne
            || ReadColor(TickerOtherColorText, "Autre cause") is not { } other
            || ReadColor(TickerPlannedColorText, "Arrêt planifié / maintenance") is not { } planned
            || ReadColor(TickerTextColorText, "Texte") is not { } textColor)
            return null;

        TickerStatusMessage = "";
        return new StopTickerSettings
        {
            SpeedPixelsPerSecond = speed,
            PanneColor = panne,
            OtherColor = other,
            PlannedColor = planned,
            TextColor = textColor,
        };
    }

    private async Task LoadMachinesAsync()
    {
        Machines.Clear();
        try
        {
            var machines = await _repository.GetMachinesAsync();
            foreach (var machine in machines)
            {
                var weightBand = _settings.WeightBandFor(machine.MachineId);
                var option = new MachineVisibilityOption
                {
                    MachineId = machine.MachineId,
                    OriginalMachineName = machine.MachineName,
                    MachineName = machine.MachineName,
                    IsVisible = !_settings.HiddenMachineIds.Contains(machine.MachineId),
                    TrsTargetText = _settings.MachineTrsTargets.TryGetValue(machine.MachineId, out var target)
                        ? NominalRateRowViewModel.Format(target)
                        : "",
                    TrsToleranceText = _settings.MachineTrsTolerances.TryGetValue(machine.MachineId, out var tolerance)
                        ? NominalRateRowViewModel.Format(tolerance)
                        : "",
                    WeightOverGoodText = NominalRateRowViewModel.Format(weightBand.OverGood),
                    WeightOverWarningText = NominalRateRowViewModel.Format(weightBand.OverWarning),
                    WeightUnderWarningText = NominalRateRowViewModel.Format(weightBand.UnderWarning),
                    WeightUnderBadText = NominalRateRowViewModel.Format(weightBand.UnderBad),
                };
                Machines.Add(option);

                // The cadence table names machines as the list above does, even while being edited.
                var choice = MachineChoices.FirstOrDefault(c => c.Id == machine.MachineId);
                if (choice == null)
                {
                    choice = new MachineChoice(machine.MachineId, "");
                    MachineChoices.Add(choice);
                }
                choice.Label = ChoiceLabel(option);
                option.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MachineVisibilityOption.MachineName))
                        choice.Label = ChoiceLabel(option);
                };
            }
            MachinesStatusMessage = machines.Count == 0 ? "Aucune machine trouvée." : "";

            // "Actualiser la liste" keeps the slide edits not saved yet.
            _activeMachines = machines.Where(m => m.IsActive).ToList();
            LoadSlides(Slides.Count == 0 ? _settings.Slides : Slides.Select(r => new SlideSetting
            {
                Key = r.Key,
                Enabled = r.IsEnabled,
                DurationSeconds = int.TryParse(r.DurationText.Trim(), out var seconds) ? seconds : null,
            }).ToList());
        }
        catch (Exception ex)
        {
            MachinesStatusMessage = $"Impossible de charger la liste des machines : {ex.Message}";
        }
    }

    // Every slide of the deck (all active machines, document included even
    // with no PDF chosen yet), in the saved order.
    private void LoadSlides(IReadOnlyList<SlideSetting> saved)
    {
        Slides.Clear();
        var names = _activeMachines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var catalog = SlideDeck.Default(_activeMachines, PdfDocumentPath, listPdf: true);
        foreach (var slide in SlideDeck.Arrange(catalog, saved))
        {
            var setting = saved.FirstOrDefault(s => s.Key == slide.Key);
            Slides.Add(new SlideRowViewModel
            {
                Key = slide.Key,
                Label = SlideDeck.Label(slide, id => names.GetValueOrDefault(id, id)),
                IsEnabled = setting?.Enabled ?? true,
                DurationText = setting?.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
            });
        }
        RenumberSlides();
    }

    private void RenumberSlides()
    {
        for (var i = 0; i < Slides.Count; i++)
            Slides[i].Position = i + 1;
    }

    [RelayCommand]
    private void MoveSlideUp(SlideRowViewModel row) => MoveSlide(row, -1);

    [RelayCommand]
    private void MoveSlideDown(SlideRowViewModel row) => MoveSlide(row, +1);

    private void MoveSlide(SlideRowViewModel row, int offset)
    {
        var from = Slides.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Slides.Count)
            return;
        Slides.Move(from, to);
        RenumberSlides();
    }

    [RelayCommand]
    private void ResetSlides() => LoadSlides(Array.Empty<SlideSetting>());

    // Slide list; null (message shown) on a bad duration or with every slide off.
    private List<SlideSetting>? ParseSlides()
    {
        var slides = new List<SlideSetting>();
        foreach (var row in Slides)
        {
            int? seconds = null;
            if (row.DurationText.Trim().Length > 0)
            {
                if (!int.TryParse(row.DurationText.Trim(), out var value) || value < 1 || value > 3600)
                {
                    SlidesStatusMessage = $"Durée invalide pour « {row.Label} » : « {row.DurationText} » (1 à 3600 s, vide = durée par défaut).";
                    return null;
                }
                seconds = value;
            }
            slides.Add(new SlideSetting { Key = row.Key, Enabled = row.IsEnabled, DurationSeconds = seconds });
        }
        if (Slides.Count > 0 && !Slides.Any(r => r.IsEnabled))
        {
            SlidesStatusMessage = "Au moins une diapositive doit être affichée.";
            return null;
        }
        SlidesStatusMessage = "";
        // Slides of machines no longer active keep their place for when they come back.
        var listed = slides.Select(s => s.Key).ToHashSet();
        slides.AddRange(_settings.Slides.Where(s => !listed.Contains(s.Key)));
        return slides;
    }

    // A blank name is not saved (SaveAsync keeps the old one): show the old one meanwhile.
    private static string ChoiceLabel(MachineVisibilityOption option)
        => option.MachineName.Trim() is { Length: > 0 } name ? name : option.OriginalMachineName;

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
            ? $"Cible : SQL Server {Host}/{Database} (auth {AuthMode})"
            : "Cible : SQLite (dev)");

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
        _settings.ShowStopAlerts = ShowStopAlerts;
        if (ResetStopAlertPosition)
        {
            _settings.StopAlertOffsetX = 0;
            _settings.StopAlertOffsetY = 0;
        }
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
        _settings.PlannedBreaks = ParsePlannedBreaks() ?? _settings.PlannedBreaks;
        _settings.StopThresholdMinutes = ParseStopThreshold() ?? _settings.StopThresholdMinutes;
        _settings.Colors = ParseColors() ?? _settings.Colors;
        _settings.StopTicker = ParseTicker() ?? _settings.StopTicker;
        // Machine list not loaded (DB down): keep the targets and slides already saved.
        if (Machines.Count > 0)
        {
            _settings.MachineTrsTargets = ParseTrsTargets() ?? _settings.MachineTrsTargets;
            _settings.MachineTrsTolerances = ParseTrsTolerances() ?? _settings.MachineTrsTolerances;
            // Machines not listed this time (DB list changed) keep their limits.
            if (ParseWeightBands() is { } weightBands)
            {
                var merged = new Dictionary<string, WeightBand>(_settings.MachineWeightBands);
                foreach (var (id, band) in weightBands)
                    merged[id] = band;
                _settings.MachineWeightBands = merged;
            }
            _settings.Slides = ParseSlides() ?? _settings.Slides;
        }

        // The shared part goes to the base chosen above (a new PC's first
        // save only sets its connection: nothing shared is sent).
        _activeFactory.SwitchTo(BuildFactoryFromFields());
        _sharedSettings.Reset();
        var shared = await _sharedSettings.PushAsync(_sharedAtOpen);
        SharedSettingsMessage = shared.Message;
        // Saved: a second Enregistrer compares with what is now in the base.
        if (shared.Outcome == SharedSaveOutcome.Saved)
            _sharedAtOpen = SharedSettings.Serialize(_settings);

        _settingsService.Save(_settings);
        AppLogger.Log("Paramètres de connexion enregistrés et appliqués.");

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
                    MachinesStatusMessage = $"Impossible de renommer {machine.OriginalMachineName} : {ex.Message}";
                }
            }
        }

        _onApplied();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }
}
