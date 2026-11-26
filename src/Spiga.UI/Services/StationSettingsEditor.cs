using System.Globalization;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels.Slides;

namespace Spiga.UI.Services;

// Administration > Paramètres (web and desktop): what was the WPF
// Paramètres window (SettingsViewModel). The shared part (cadences, pauses,
// stop threshold, teams, TRS targets, weight limits, colours, stop bar, slide
// rotation, machine names) is saved in the database for every PC and the
// web site; the rest (connection, machines shown, slide document, stop
// alert, refresh) only concerns the PC the desktop application runs on
// (IStationHost), and is not offered in a browser.
public sealed class StationSettingsEditor(
    AppSettings settings, SettingsService settingsService, SharedSettingsSync shared, ProductionRepository repository, IDesktopBridge desktop)
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    // Shared part as it was when the form was filled (SharedSettingsSync.PushAsync).
    private string _sharedAtOpen = "";
    private List<Machine> _activeMachines = new();

    public IStationHost? Station => desktop.Station;

    // ---- the form ----

    // Connexion (desktop only). Password blank = keep the saved one.
    public DatabaseProvider Provider { get; set; }
    public string Host { get; set; } = "";
    public string Database { get; set; } = "";
    public SqlAuthMode AuthMode { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    public List<MachineSettingsRow> Machines { get; } = new();
    public List<RateRow> Rates { get; } = new();
    public List<BreakRow> Breaks { get; } = new();
    public string StopThreshold { get; set; } = "";
    public string[] TeamNames { get; } = new string[4];
    public DateTime RotationStart { get; set; }
    public List<RosterDay> Roster { get; } = new();
    // Rotation of the kiosk application (by sector) and of the web dashboard
    // and TV screen (by line): two decks, two orders.
    public List<SlideRow> Slides { get; } = new();
    public List<SlideRow> WebSlides { get; } = new();
    public int SlideDurationSeconds { get; set; }

    // Paramètres > Lignes (web dashboard).
    public List<LineRow> Lines { get; } = new();

    // Desktop only.
    public int DataRefreshMinutes { get; set; }
    public bool ShowStopAlerts { get; set; }
    public bool ResetStopAlertPosition { get; set; }
    public string PdfDocumentPath { get; set; } = "";

    public ColorFields Colors { get; private set; } = new();
    public TickerFields Ticker { get; private set; } = new();

    // What is wrong, per tab (shown on that tab).
    public Dictionary<SettingsTab, string> Problems { get; } = new();

    // Machines of the database, by id: the cadence table names them.
    public IEnumerable<(string Id, string Label)> MachineChoices
        => Machines.Select(m => (m.MachineId, m.Label))
            .Concat(Rates.Select(r => r.MachineId).Where(id => id.Length > 0 && Machines.All(m => m.MachineId != id)).Distinct().Select(id => (id, id)));

    // ---- load ----

    public async Task LoadAsync()
    {
        // Edit the shared settings as the other PCs last saved them.
        await shared.PullAsync();
        _sharedAtOpen = SharedSettings.Serialize(settings);
        Problems.Clear();

        Provider = settings.Provider;
        Host = settings.SqlServer.Host;
        Database = settings.SqlServer.Database;
        AuthMode = settings.SqlServer.AuthMode;
        Username = settings.SqlServer.Username;
        Password = "";

        SlideDurationSeconds = settings.SlideDurationSeconds;
        ShowStopAlerts = settings.ShowStopAlerts;
        ResetStopAlertPosition = false;
        DataRefreshMinutes = settings.DataRefreshIntervalMinutes;
        PdfDocumentPath = settings.PdfDocumentPath;

        for (var i = 0; i < 4; i++)
            TeamNames[i] = settings.TeamRotation.TeamNames.ElementAtOrDefault(i) ?? "";
        RotationStart = settings.TeamRotation.RotationStartDate;
        LoadRoster(settings.TeamRotation.Pattern);

        Rates.Clear();
        foreach (var entry in settings.NominalRates.OrderBy(r => r.MachineId).ThenBy(r => r.WeightGrams))
            Rates.Add(new RateRow
            {
                MachineId = entry.MachineId ?? "",
                Weight = Format(entry.WeightGrams),
                Rate = entry.RatePerMinute > 0 ? Format(entry.RatePerMinute) : "",
                Tare = entry.TareGrams > 0 ? Format(entry.TareGrams) : "",
            });

        Breaks.Clear();
        foreach (var b in settings.PlannedBreaks.OrderBy(b => b.Start))
            Breaks.Add(new BreakRow { Start = FormatTime(b.Start), Duration = b.DurationMinutes.ToString(CultureInfo.InvariantCulture) });
        StopThreshold = settings.StopThresholdMinutes.ToString(CultureInfo.InvariantCulture);

        ResetColors(settings.Colors);
        ResetTicker(settings.StopTicker);
        Lines.Clear();
        Lines.AddRange(settings.DashboardLines.Select(LineRow.Of));
        await LoadMachinesAsync();
    }

    // "Actualiser la liste" keeps the slide edits not saved yet.
    public async Task LoadMachinesAsync()
    {
        Machines.Clear();
        Problems.Remove(SettingsTab.Machines);
        try
        {
            var machines = await repository.GetMachinesAsync();
            foreach (var machine in machines)
            {
                var band = settings.WeightBandFor(machine.MachineId);
                Machines.Add(new MachineSettingsRow
                {
                    MachineId = machine.MachineId,
                    OriginalName = machine.MachineName,
                    Name = machine.MachineName,
                    Sector = machine.Sector,
                    Visible = !settings.HiddenMachineIds.Contains(machine.MachineId),
                    TrsTarget = settings.MachineTrsTargets.TryGetValue(machine.MachineId, out var target) ? Format(target) : "",
                    TrsTolerance = settings.MachineTrsTolerances.TryGetValue(machine.MachineId, out var tolerance) ? Format(tolerance) : "",
                    OverGood = Format(band.OverGood),
                    OverWarning = Format(band.OverWarning),
                    UnderWarning = Format(band.UnderWarning),
                    UnderBad = Format(band.UnderBad),
                });
            }
            if (machines.Count == 0)
                Problems[SettingsTab.Machines] = "Aucune machine trouvée.";
            _activeMachines = machines.Where(m => m.IsActive).ToList();
            LoadSlides(Slides.Count == 0 ? settings.Slides : CurrentSlides(Slides));
            LoadWebSlides(WebSlides.Count == 0 ? settings.WebSlides : CurrentSlides(WebSlides));
        }
        catch (Exception ex)
        {
            Problems[SettingsTab.Machines] = $"Impossible de charger la liste des machines : {ex.Message}";
            if (Slides.Count == 0)
                LoadSlides(settings.Slides);
            if (WebSlides.Count == 0)
                LoadWebSlides(settings.WebSlides);
        }
    }

    private static List<SlideSetting> CurrentSlides(List<SlideRow> rows) => rows.Select(r => new SlideSetting
    {
        Key = r.Key,
        Enabled = r.Enabled,
        DurationSeconds = int.TryParse(r.Duration.Trim(), out var seconds) ? seconds : null,
    }).ToList();

    // ---- table rows ----

    public void AddRosterDay() => Roster.Add(new RosterDay { CycleDay = Roster.Count });

    public void RemoveRosterDay()
    {
        if (Roster.Count > 1)
            Roster.RemoveAt(Roster.Count - 1);
    }

    private void LoadRoster(List<TeamRosterEntry> pattern)
    {
        Roster.Clear();
        var cycle = pattern.Count == 0 ? 4 : pattern.Max(p => p.CycleDay) + 1;
        for (var day = 0; day < cycle; day++)
            Roster.Add(new RosterDay
            {
                CycleDay = day,
                Matin = pattern.FirstOrDefault(p => p.CycleDay == day && p.ShiftId == 1)?.TeamIndex ?? -1,
                Soir = pattern.FirstOrDefault(p => p.CycleDay == day && p.ShiftId == 2)?.TeamIndex ?? -1,
                Nuit = pattern.FirstOrDefault(p => p.CycleDay == day && p.ShiftId == 3)?.TeamIndex ?? -1,
            });
    }

    public void AddRate() => Rates.Add(new RateRow());

    // Copies a row for another machine: the usual way to set a machine's own
    // cadence / sachet vide from the "Toutes" values.
    public void DuplicateRate(RateRow row)
        => Rates.Insert(Rates.IndexOf(row) + 1, new RateRow { MachineId = row.MachineId, Weight = row.Weight, Rate = row.Rate, Tare = row.Tare });

    public void AddBreak() => Breaks.Add(new BreakRow { Duration = "20" });

    // Every slide of the deck (all active machines, document included even
    // with none chosen yet), in the saved order.
    private void LoadSlides(IReadOnlyList<SlideSetting> saved)
    {
        Slides.Clear();
        var names = _activeMachines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var catalog = SlideDeck.Default(_activeMachines, PdfDocumentPath, listPdf: true);
        foreach (var slide in SlideDeck.Arrange(catalog, saved))
        {
            var setting = saved.FirstOrDefault(s => s.Key == slide.Key);
            Slides.Add(new SlideRow
            {
                Key = slide.Key,
                Label = SlideDeck.Label(slide, id => names.GetValueOrDefault(id, id)),
                Enabled = setting?.Enabled ?? true,
                Duration = setting?.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
            });
        }
    }

    // The web deck as the lines last saved make it (lines edited in the
    // Lignes tab show here once saved).
    private void LoadWebSlides(IReadOnlyList<SlideSetting> saved)
    {
        WebSlides.Clear();
        var names = _activeMachines.ToDictionary(m => m.MachineId, m => m.MachineName);
        var catalog = WebDeck.Default(_activeMachines, _activeMachines, settings.DashboardLines);
        foreach (var slide in SlideDeck.Arrange(catalog, saved))
        {
            var setting = saved.FirstOrDefault(s => s.Key == slide.Key);
            WebSlides.Add(new SlideRow
            {
                Key = slide.Key,
                Label = WebDeck.Label(slide, id => names.GetValueOrDefault(id, id), settings.DashboardLines),
                Enabled = setting?.Enabled ?? true,
                Duration = setting?.DurationSeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
            });
        }
    }

    public static void MoveSlide(List<SlideRow> list, SlideRow row, int offset)
    {
        var from = list.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= list.Count)
            return;
        list.RemoveAt(from);
        list.Insert(to, row);
    }

    public void ResetSlides() => LoadSlides(Array.Empty<SlideSetting>());

    public void ResetWebSlides() => LoadWebSlides(Array.Empty<SlideSetting>());

    public void AddLine() => Lines.Add(new LineRow { Sector = SlideDeck.SectorOrder[0] });

    public void MoveLine(LineRow row, int offset)
    {
        var from = Lines.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Lines.Count)
            return;
        Lines.RemoveAt(from);
        Lines.Insert(to, row);
    }

    // The names a line's machine ids stand for ("RIC 2 PL · STV 3 PL"),
    // unknown ids flagged.
    public string LinePreview(LineRow row)
        => string.Join(" · ", WebDeck.Split(row.Machines).Select(id =>
            Machines.FirstOrDefault(m => string.Equals(m.MachineId, id, StringComparison.OrdinalIgnoreCase)) is { } m ? m.Label : $"{id} (inconnue)"));

    public void ResetColors(ColorThresholds? c = null)
    {
        c ??= new ColorThresholds();
        Colors = new ColorFields
        {
            TrsWarning = Format(c.TrsWarningPercentOfTarget),
            AvailabilityGood = Format(c.AvailabilityGood),
            AvailabilityWarning = Format(c.AvailabilityWarning),
            PerformanceGood = Format(c.PerformanceGood),
            PerformanceWarning = Format(c.PerformanceWarning),
            PerformanceHigh = Format(c.PerformanceHigh),
            QualityGood = Format(c.QualityGood),
            QualityWarning = Format(c.QualityWarning),
        };
    }

    public void ResetTicker(StopTickerSettings? t = null)
    {
        t ??= new StopTickerSettings();
        Ticker = new TickerFields
        {
            Speed = t.SpeedPixelsPerSecond.ToString(CultureInfo.InvariantCulture),
            Panne = t.PanneColor,
            Other = t.OtherColor,
            Planned = t.PlannedColor,
            Text = t.TextColor,
        };
    }

    // ---- check and save ----

    // Connexion: the fields as typed (password blank = the saved one).
    public Task<(bool Success, string Message)> TestConnectionAsync()
        => Station?.TestAsync(Provider, new SqlServerSettings
        {
            Host = Host,
            Database = Database,
            AuthMode = AuthMode,
            Username = Username,
            EncryptedPassword = string.IsNullOrEmpty(Password) ? settings.SqlServer.EncryptedPassword : PasswordProtector.Protect(Password),
        }) ?? Task.FromResult((false, "Propre au poste : réglé dans l'application installée."));

    // Every cell checked; the first tab with a problem, or null.
    public SettingsTab? Validate()
    {
        Problems.Clear();
        var bad = new List<SettingsTab>();
        void Check(SettingsTab tab, bool ok)
        {
            if (!ok)
                bad.Add(tab);
        }
        Check(SettingsTab.Machines, ParseTrsTargets() != null && ParseTrsTolerances() != null && ParseWeightBands() != null);
        Check(SettingsTab.Lignes, Machines.Count == 0 || ParseLines() != null);
        Check(SettingsTab.Affichage, ParseSlides() != null && ParseWebSlides() != null && ParseDisplay());
        Check(SettingsTab.Cadences, ParseRates() != null);
        Check(SettingsTab.Pauses, ParseBreaks() != null && ParseStopThreshold() != null);
        Check(SettingsTab.Couleurs, ParseColors() != null && ParseTicker() != null);
        return bad.Count == 0 ? null : bad.Min();
    }

    // Applies the form (Validate first). Returns the message to show when the
    // shared part could not be saved (another PC saved meanwhile, base down).
    public async Task<string> SaveAsync(ChangeAuthor author)
    {
        if (Validate() != null)
            throw new InvalidOperationException("Paramètres invalides.");

        var station = Station;
        if (station != null)
        {
            settings.Provider = Provider;
            settings.SqlServer.Host = Host;
            settings.SqlServer.Database = Database;
            settings.SqlServer.AuthMode = AuthMode;
            settings.SqlServer.Username = Username;
            if (!string.IsNullOrEmpty(Password))
                settings.SqlServer.EncryptedPassword = PasswordProtector.Protect(Password);
            settings.ShowStopAlerts = ShowStopAlerts;
            if (ResetStopAlertPosition)
            {
                settings.StopAlertOffsetX = 0;
                settings.StopAlertOffsetY = 0;
            }
            settings.DataRefreshIntervalMinutes = Math.Max(1, DataRefreshMinutes);
            settings.PdfDocumentPath = PdfDocumentPath.Trim();
            if (Machines.Count > 0)
                settings.HiddenMachineIds = Machines.Where(m => !m.Visible).Select(m => m.MachineId).ToList();
        }

        settings.SlideDurationSeconds = Math.Max(1, SlideDurationSeconds);
        settings.TeamRotation.TeamNames = TeamNames.Select(n => n.Trim()).ToArray();
        settings.TeamRotation.RotationStartDate = RotationStart.Date;
        settings.TeamRotation.Pattern = Roster.SelectMany(day => new[] { (1, day.Matin), (2, day.Soir), (3, day.Nuit) }
                .Where(x => x.Item2 >= 0)
                .Select(x => new TeamRosterEntry { CycleDay = day.CycleDay, ShiftId = x.Item1, TeamIndex = x.Item2 }))
            .ToList();
        settings.NominalRates = ParseRates()!;
        settings.PlannedBreaks = ParseBreaks()!;
        settings.StopThresholdMinutes = ParseStopThreshold()!.Value;
        settings.Colors = ParseColors()!;
        settings.StopTicker = ParseTicker()!;
        // Machine list not loaded (base down): keep the targets and slides already saved.
        if (Machines.Count > 0)
        {
            settings.MachineTrsTargets = ParseTrsTargets()!;
            settings.MachineTrsTolerances = ParseTrsTolerances()!;
            // Machines not listed this time keep their limits.
            var merged = new Dictionary<string, WeightBand>(settings.MachineWeightBands);
            foreach (var (id, band) in ParseWeightBands()!)
                merged[id] = band;
            settings.MachineWeightBands = merged;
            settings.Slides = ParseSlides()!;
            settings.WebSlides = ParseWebSlides()!;
            // Line ids are checked against the machines: only with the list loaded.
            settings.DashboardLines = ParseLines()!;
        }

        // A new connection: the databases switch first, so the shared part
        // goes to the base chosen here.
        if (station != null)
            await station.SwitchAsync();
        var result = await shared.PushAsync(_sharedAtOpen);
        // Saved: a second Enregistrer compares with what is now in the base.
        if (result.Outcome == SharedSaveOutcome.Saved)
            _sharedAtOpen = SharedSettings.Serialize(settings);
        settingsService.Save(settings);
        AppLogger.Log($"Paramètres enregistrés par {author.Login} ({author.Workstation}).");

        var message = result.Message;
        foreach (var machine in Machines)
        {
            var name = machine.Name.Trim();
            if (name.Length == 0 || name == machine.OriginalName)
                continue;
            try
            {
                await repository.UpdateMachineNameAsync(machine.MachineId, name, author);
                machine.OriginalName = name;
            }
            catch (Exception ex)
            {
                message += (message.Length > 0 ? " " : "") + $"Impossible de renommer {machine.OriginalName} : {ex.Message}";
            }
        }

        station?.Applied();
        return message;
    }

    private Dictionary<string, double>? ParseTrsTargets()
    {
        var targets = new Dictionary<string, double>();
        foreach (var m in Machines)
        {
            if (!TryParse(m.TrsTarget, out var target) || target > 100)
                return Fail<Dictionary<string, double>>(SettingsTab.Machines, $"TRS cible invalide pour {m.Label} : « {m.TrsTarget} » (0 à 100 %).");
            if (target > 0)
                targets[m.MachineId] = target;
        }
        return targets;
    }

    private Dictionary<string, double>? ParseTrsTolerances()
    {
        var tolerances = new Dictionary<string, double>();
        foreach (var m in Machines.Where(m => m.TrsTolerance.Trim().Length > 0))
        {
            if (!TryParse(m.TrsTolerance, out var tolerance) || tolerance > 100)
                return Fail<Dictionary<string, double>>(SettingsTab.Machines,
                    $"Tolérance invalide pour {m.Label} : « {m.TrsTolerance} » (0 à 100 %, vide = règle de l'onglet Couleurs).");
            tolerances[m.MachineId] = tolerance;
        }
        return tolerances;
    }

    private Dictionary<string, WeightBand>? ParseWeightBands()
    {
        var bands = new Dictionary<string, WeightBand>();
        foreach (var m in Machines)
        {
            double? Read(string text, string what)
            {
                if (text.Trim().Length > 0 && TryParse(text, out var value) && value <= 100)
                    return value;
                Problems[SettingsTab.Machines] = $"Écart de poids {what} invalide pour {m.Label} : « {text} » (0 à 100 %).";
                return null;
            }
            if (Read(m.OverGood, "au-dessus (vert)") is not { } overGood
                || Read(m.OverWarning, "au-dessus (orange)") is not { } overWarning
                || Read(m.UnderWarning, "en dessous (vert)") is not { } underWarning
                || Read(m.UnderBad, "en dessous (orange)") is not { } underBad)
                return null;
            if (overWarning < overGood || underBad < underWarning)
                return Fail<Dictionary<string, WeightBand>>(SettingsTab.Machines,
                    $"Écart de poids pour {m.Label} : la limite orange doit être supérieure ou égale à la limite verte, de chaque côté.");
            bands[m.MachineId] = new WeightBand { OverGood = overGood, OverWarning = overWarning, UnderWarning = underWarning, UnderBad = underBad };
        }
        return bands;
    }

    private List<SlideSetting>? ParseSlides() => ParseSlides(Slides, settings.Slides, "");

    private List<SlideSetting>? ParseWebSlides() => ParseSlides(WebSlides, settings.WebSlides, " (tableau de bord web)");

    private List<DashboardLine>? ParseLines()
    {
        var (lines, problem) = WebDeck.Parse(Lines, Machines.Select(m => m.MachineId).ToList(),
            settings.DashboardLines.SelectMany(l => l.MachineIds).ToList());
        return problem != null ? Fail<List<DashboardLine>>(SettingsTab.Lignes, problem) : lines;
    }

    private List<SlideSetting>? ParseSlides(List<SlideRow> rows, List<SlideSetting> saved, string deck)
    {
        var slides = new List<SlideSetting>();
        foreach (var row in rows)
        {
            int? seconds = null;
            if (row.Duration.Trim().Length > 0)
            {
                if (!int.TryParse(row.Duration.Trim(), out var value) || value < 1 || value > 3600)
                    return Fail<List<SlideSetting>>(SettingsTab.Affichage,
                        $"Durée invalide pour « {row.Label} »{deck} : « {row.Duration} » (1 à 3600 s, vide = durée par défaut).");
                seconds = value;
            }
            slides.Add(new SlideSetting { Key = row.Key, Enabled = row.Enabled, DurationSeconds = seconds });
        }
        if (rows.Count > 0 && !rows.Any(r => r.Enabled))
            return Fail<List<SlideSetting>>(SettingsTab.Affichage, $"Au moins une diapositive doit être affichée{deck}.");
        // Slides of machines no longer active keep their place for when they come back.
        var listed = slides.Select(s => s.Key).ToHashSet();
        slides.AddRange(saved.Where(s => !listed.Contains(s.Key)));
        return slides;
    }

    private bool ParseDisplay()
    {
        if (SlideDurationSeconds is < 1 or > 3600)
            return Fails(SettingsTab.Affichage, "Durée d'affichage par défaut invalide (1 à 3600 s).");
        if (Station != null && DataRefreshMinutes is < 1 or > 60)
            return Fails(SettingsTab.Affichage, "Fréquence d'actualisation invalide (1 à 60 min).");
        return true;
    }

    private List<NominalRateEntry>? ParseRates()
    {
        var entries = new List<NominalRateEntry>();
        foreach (var row in Rates)
        {
            if (row.Weight.Trim().Length == 0 && row.Rate.Trim().Length == 0 && row.Tare.Trim().Length == 0)
                continue;
            var machine = row.MachineId;
            var where = machine.Length == 0 ? "" : $" ({machine})";
            if (!TryParse(row.Weight, out var weight) || weight <= 0)
                return Fail<List<NominalRateEntry>>(SettingsTab.Cadences, $"Poids invalide{where} : « {row.Weight} » (nombre de grammes attendu).");
            if (!TryParse(row.Rate, out var rate))
                return Fail<List<NominalRateEntry>>(SettingsTab.Cadences, $"Cadence invalide pour {row.Weight} g{where} : « {row.Rate} ».");
            if (!TryParse(row.Tare, out var tare) || tare >= weight)
                return Fail<List<NominalRateEntry>>(SettingsTab.Cadences, $"Poids du sachet vide invalide pour {row.Weight} g{where} : « {row.Tare} ».");
            if (entries.Any(e => e.MachineId == machine && e.WeightGrams == weight))
                return Fail<List<NominalRateEntry>>(SettingsTab.Cadences,
                    $"Le poids {row.Weight} g apparaît deux fois pour {(machine.Length == 0 ? "Toutes" : machine)}.");
            entries.Add(new NominalRateEntry { MachineId = machine, WeightGrams = weight, RatePerMinute = rate, TareGrams = tare });
        }
        return entries.OrderBy(e => e.MachineId).ThenBy(e => e.WeightGrams).ToList();
    }

    private List<PlannedBreak>? ParseBreaks()
    {
        var breaks = new List<PlannedBreak>();
        foreach (var row in Breaks)
        {
            if (row.Start.Trim().Length == 0 && row.Duration.Trim().Length == 0)
                continue;
            if (!TryParseTime(row.Start, out var start))
                return Fail<List<PlannedBreak>>(SettingsTab.Pauses, $"Heure de pause invalide : « {row.Start} » (format 10:00 attendu).");
            if (!int.TryParse(row.Duration.Trim(), out var minutes) || minutes <= 0 || minutes > 480)
                return Fail<List<PlannedBreak>>(SettingsTab.Pauses, $"Durée invalide pour la pause de {row.Start} : « {row.Duration} » (1 à 480 min).");
            breaks.Add(new PlannedBreak { Start = start, DurationMinutes = minutes });
        }
        return breaks.OrderBy(b => b.Start).ToList();
    }

    private int? ParseStopThreshold()
    {
        if (int.TryParse(StopThreshold.Trim(), out var minutes) && minutes is >= 1 and <= 240)
            return minutes;
        Problems[SettingsTab.Pauses] = $"Seuil d'arrêt invalide : « {StopThreshold} » (1 à 240 min).";
        return null;
    }

    private ColorThresholds? ParseColors()
    {
        var c = Colors;
        double? Read(string text, string what, double max)
        {
            if (text.Trim().Length > 0 && TryParse(text, out var value) && value <= max)
                return value;
            Problems[SettingsTab.Couleurs] = $"{what} invalide : « {text} » (nombre de 0 à {max:0} attendu).";
            return null;
        }
        if (Read(c.TrsWarning, "TRS : seuil orange", 100) is not { } trsWarning
            || Read(c.AvailabilityGood, "Disponibilité : seuil vert", 100) is not { } availabilityGood
            || Read(c.AvailabilityWarning, "Disponibilité : seuil orange", 100) is not { } availabilityWarning
            || Read(c.PerformanceGood, "Performance : seuil vert", 200) is not { } performanceGood
            || Read(c.PerformanceWarning, "Performance : seuil orange", 200) is not { } performanceWarning
            || Read(c.PerformanceHigh, "Performance : seuil haut", 200) is not { } performanceHigh
            || Read(c.QualityGood, "Conformité : seuil vert", 100) is not { } qualityGood
            || Read(c.QualityWarning, "Conformité : seuil orange", 100) is not { } qualityWarning)
            return null;
        var order = availabilityWarning > availabilityGood ? "Disponibilité : le seuil orange doit être inférieur ou égal au seuil vert."
            : performanceWarning > performanceGood ? "Performance : le seuil orange doit être inférieur ou égal au seuil vert."
            : performanceGood > performanceHigh ? "Performance : le seuil haut doit être supérieur ou égal au seuil vert."
            : qualityWarning > qualityGood ? "Conformité : le seuil orange doit être inférieur ou égal au seuil vert."
            : null;
        if (order != null)
            return Fail<ColorThresholds>(SettingsTab.Couleurs, order);
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
            // Set per machine (Machines tab): the fallback stays as is.
            WeightOverGood = settings.Colors.WeightOverGood,
            WeightOverWarning = settings.Colors.WeightOverWarning,
            WeightUnderWarning = settings.Colors.WeightUnderWarning,
            WeightUnderBad = settings.Colors.WeightUnderBad,
        };
    }

    private StopTickerSettings? ParseTicker()
    {
        var t = Ticker;
        if (!int.TryParse(t.Speed.Trim(), out var speed) || speed < StopTickerSettings.MinSpeed || speed > StopTickerSettings.MaxSpeed)
            return Fail<StopTickerSettings>(SettingsTab.Couleurs,
                $"Vitesse invalide : « {t.Speed} » ({StopTickerSettings.MinSpeed} à {StopTickerSettings.MaxSpeed} pixels par seconde).");
        string? ReadColor(string text, string what)
        {
            if (CssColor(text) != null)
                return text.Trim();
            Problems[SettingsTab.Couleurs] = $"Couleur « {what} » invalide : « {text} » (ex. #E74C3C ou Red).";
            return null;
        }
        if (ReadColor(t.Panne, "Panne / sans cause") is not { } panne
            || ReadColor(t.Other, "Autre cause") is not { } other
            || ReadColor(t.Planned, "Arrêt planifié / maintenance") is not { } planned
            || ReadColor(t.Text, "Texte") is not { } text)
            return null;
        return new StopTickerSettings { SpeedPixelsPerSecond = speed, PanneColor = panne, OtherColor = other, PlannedColor = planned, TextColor = text };
    }

    private T? Fail<T>(SettingsTab tab, string message) where T : class
    {
        Problems[tab] = message;
        return null;
    }

    private bool Fails(SettingsTab tab, string message)
    {
        Problems[tab] = message;
        return false;
    }

    // ---- formats ----

    public static string Format(double value) => value.ToString("0.##", French);

    // "12,5", "12.5"; blank = 0. Never negative.
    public static bool TryParse(string text, out double value)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (normalized.Length == 0)
        {
            value = 0;
            return true;
        }
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    public static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // "10:00", "9:30", "10h", "10h30".
    public static bool TryParseTime(string text, out TimeSpan start)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace('h', ':');
        if (normalized.EndsWith(':'))
            normalized += "00";
        return TimeSpan.TryParseExact(normalized, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out start)
               && start < TimeSpan.FromDays(1);
    }

    // A colour as the dashboard reads it ("#RRGGBB", "#AARRGGBB", or a colour
    // name: Red, Orange…), as CSS for the preview; null if not understood.
    public static string? CssColor(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('#'))
        {
            var hex = t[1..];
            if (!hex.All(Uri.IsHexDigit))
                return null;
            return hex.Length switch
            {
                3 or 6 => t,
                // WPF: alpha first; CSS: alpha last.
                8 => "#" + hex[2..] + hex[..2],
                _ => null,
            };
        }
        var named = System.Drawing.Color.FromName(t);
        return t.Length > 0 && named.IsKnownColor && !named.IsSystemColor ? $"#{named.R:X2}{named.G:X2}{named.B:X2}" : null;
    }
}

// Tabs of Paramètres, in order.
public enum SettingsTab { Connexion, Machines, Lignes, Affichage, Cadences, Pauses, Equipes, Couleurs }

public sealed class MachineSettingsRow
{
    public string MachineId { get; init; } = "";
    public string OriginalName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sector { get; init; } = "";
    public bool Visible { get; set; } = true;
    public string TrsTarget { get; set; } = "";
    public string TrsTolerance { get; set; } = "";
    public string OverGood { get; set; } = "";
    public string OverWarning { get; set; } = "";
    public string UnderWarning { get; set; } = "";
    public string UnderBad { get; set; } = "";

    // A blank name is not saved (the old one stays): shown meanwhile.
    public string Label => Name.Trim() is { Length: > 0 } name ? name : OriginalName;
}

public sealed class RateRow
{
    public string MachineId { get; set; } = "";
    public string Weight { get; set; } = "";
    public string Rate { get; set; } = "";
    public string Tare { get; set; } = "";
}

public sealed class BreakRow
{
    public string Start { get; set; } = "";
    public string Duration { get; set; } = "";
}

// One day of the rotation cycle: the team index (or -1 = Repos) on each poste.
public sealed class RosterDay
{
    public int CycleDay { get; init; }
    public int Matin { get; set; } = -1;
    public int Soir { get; set; } = -1;
    public int Nuit { get; set; } = -1;
}

public sealed class SlideRow
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Enabled { get; set; } = true;
    public string Duration { get; set; } = "";
}

public sealed class ColorFields
{
    public string TrsWarning { get; set; } = "";
    public string AvailabilityGood { get; set; } = "";
    public string AvailabilityWarning { get; set; } = "";
    public string PerformanceGood { get; set; } = "";
    public string PerformanceWarning { get; set; } = "";
    public string PerformanceHigh { get; set; } = "";
    public string QualityGood { get; set; } = "";
    public string QualityWarning { get; set; } = "";
}

public sealed class TickerFields
{
    public string Speed { get; set; } = "";
    public string Panne { get; set; } = "";
    public string Other { get; set; } = "";
    public string Planned { get; set; } = "";
    public string Text { get; set; } = "";
}

// The desktop application's own part of Paramètres (IDesktopBridge.Station).
public interface IStationHost
{
    // Production and application databases tested / switched to what the
    // editor's settings now say (Provider, SqlServer).
    Task<(bool Success, string Message)> TestAsync(DatabaseProvider provider, SqlServerSettings sql);
    Task SwitchAsync();

    // The slide document: picked with the Windows file dialog; its state.
    Task<string?> BrowsePdfAsync();
    string PdfStatus(string path);

    void OpenConsole();

    // Saved: the dashboard and the TV screen reload with the new settings.
    void Applied();
}
