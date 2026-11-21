using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace Spiga.Tests;

// The pages moved from WPF to Razor: Statistiques des arrêts and Paramètres.
public class WebMigrationTests
{
    private static StopQualification Stop(string machine, DateTime start, int minutes, string cause, string? component = null, string team = "Equipe A")
        => new()
        {
            MachineId = machine, StopStart = start, StopEnd = start.AddMinutes(minutes), ShiftDate = start.Date, ShiftId = start.Hour is >= 6 and < 14 ? 1 : 2,
            Team = team, CauseCode = cause, Component = component,
        };

    [Fact]
    public async Task Stop_statistics_count_the_qualified_stops_of_the_period_and_the_repeat_failures()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var admin = await app.SetupAdminAsync();
        var planning = new PlanningRepository(app.AppData, live.Factory);
        var now = app.Clock.Now; // 17/11/2026 10:00
        foreach (var day in new[] { -3, -2, -1 })
            await planning.SaveQualificationAsync(Stop("PC01", now.Date.AddDays(day).AddHours(9), 30, "MECA", "Capteur"));
        await planning.SaveQualificationAsync(Stop("CC01", now.Date.AddHours(8), 60, "NETT", team: "Equipe B"));
        // The 7 days before: compared with.
        await planning.SaveQualificationAsync(Stop("PC01", now.Date.AddDays(-10).AddHours(9), 20, "ELEC", "Moteur"));

        var settings = new AppSettings();
        var service = new StopStatsService(live.Factory, app.Options, settings, new TeamScheduleService(settings), app.Clock);
        var stats = await service.LoadAsync(admin, new StatsFilter(7, null, null, false));

        Assert.Equal("4", stats.Tiles[0].Value);
        Assert.Equal("2 h 30", stats.Tiles[1].Value);
        Assert.Equal("▲ 300 %", stats.Tiles[0].Delta);
        Assert.Equal("bad", stats.Tiles[0].DeltaCss);
        Assert.Equal(new[] { "Panne mécanique", "Nettoyage" }, stats.ByCause.Select(g => g.Label));
        Assert.Equal(100, stats.ByCause[^1].Cumul!.Value, 3);
        Assert.Equal(new[] { ("PC01", 90.0, 3), ("CC01", 60.0, 1) }, stats.ByMachine.Select(g => (g.Label[^4..], g.Minutes, g.Count)));
        Assert.Equal(12, stats.Weeks.Count);
        Assert.Equal(4, stats.Heat.Cast<int>().Sum());
        // No production figures in this base: MTBF and lost availability say so.
        Assert.Equal("…", stats.Tiles[2].Value);
        await service.AddRunHoursAsync(admin, new StatsFilter(7, null, null, false), stats);
        Assert.Equal(("—", "—"), (stats.Tiles[2].Value, stats.Tiles[4].Value));
        var repeat = Assert.Single(stats.Repeats);
        Assert.Equal(("PC01", "Capteur", 3, null as string), (repeat.MachineId, repeat.Component, repeat.Count, repeat.PlanRule));

        // Pannes only, one team.
        var pannes = await service.LoadAsync(admin, new StatsFilter(7, null, "Equipe A", true));
        Assert.Equal("3", pannes.Tiles[0].Value);
        Assert.Equal("Pannes", pannes.Tiles[0].Label);
    }

    [Fact]
    public async Task Stop_statistics_keep_to_the_machines_and_team_of_the_account()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var admin = await app.SetupAdminAsync();
        var planning = new PlanningRepository(app.AppData, live.Factory);
        var now = app.Clock.Now;
        await planning.SaveQualificationAsync(Stop("PC01", now.Date.AddHours(9), 30, "MECA", team: "Equipe B"));
        await planning.SaveQualificationAsync(Stop("CC01", now.Date.AddHours(9), 30, "MECA", team: "Equipe B"));
        await planning.SaveQualificationAsync(Stop("CC01", now.Date.AddDays(-1).AddHours(15), 30, "MECA", team: "Equipe A"));
        // Maintenance of the couscous lines, for one team.
        var chef = await app.UserAsync(admin, "maint.cc",
            TestApp.As(await app.RoleAsync("resp-maintenance"), new DataScope(new[] { Sectors.Couscous }, TestApp.DefaultTeam, Array.Empty<string>())));

        var settings = new AppSettings();
        var service = new StopStatsService(live.Factory, app.Options, settings, new TeamScheduleService(settings), app.Clock);
        Assert.Equal(new[] { "CC01" }, (await service.MachinesAsync(chef)).Select(m => m.MachineId));
        // Whatever team is asked for, the account's own.
        var stats = await service.LoadAsync(chef, new StatsFilter(7, null, "Equipe A", false));
        Assert.Equal("1", stats.Tiles[0].Value);
        Assert.Equal(TestApp.DefaultTeam, Assert.Single(stats.ByTeam).Label);
    }

    [Fact]
    public async Task Settings_point_to_the_tab_with_a_bad_cell()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var settings = new AppSettings();
        var sync = new SharedSettingsSync(new SharedSettingsRepository(app.AppData, live.Factory), settings, new SettingsService());
        var editor = new StationSettingsEditor(settings, new SettingsService(), sync,
            new ProductionRepository(live.Factory, app.AppData, settings), new NoDesktop());
        await editor.LoadAsync();

        Assert.Null(editor.Station);
        Assert.Equal(new[] { "CC01", "PC01" }, editor.Machines.Select(m => m.MachineId).Order());
        Assert.Null(editor.Validate());

        editor.Rates.Add(new RateRow { Weight = "abc", Rate = "40" });
        editor.Machines[0].TrsTarget = "120";
        Assert.Equal(SettingsTab.Machines, editor.Validate());
        Assert.Contains("TRS cible invalide", editor.Problems[SettingsTab.Machines]);
        Assert.Contains("Poids invalide", editor.Problems[SettingsTab.Cadences]);

        editor.Machines[0].TrsTarget = "80";
        Assert.Equal(SettingsTab.Cadences, editor.Validate());
        editor.Rates.RemoveAt(editor.Rates.Count - 1);
        editor.Ticker.Panne = "rougeâtre";
        Assert.Equal(SettingsTab.Couleurs, editor.Validate());
    }

    [Theory]
    [InlineData("#E74C3C", "#E74C3C")]
    [InlineData("#80E74C3C", "#E74C3C80")]
    [InlineData("Red", "#FF0000")]
    [InlineData("orange", "#FFA500")]
    [InlineData("#12345", null)]
    [InlineData("rougeâtre", null)]
    [InlineData("", null)]
    public void Ticker_colours_read_as_the_dashboard_reads_them(string text, string? css)
        => Assert.Equal(css, StationSettingsEditor.CssColor(text));

    [Theory]
    [InlineData("10:00", 10, 0)]
    [InlineData("9h30", 9, 30)]
    [InlineData("14h", 14, 0)]
    public void Break_times_accept_the_usual_spellings(string text, int hours, int minutes)
    {
        Assert.True(StationSettingsEditor.TryParseTime(text, out var time));
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
    }

    [Fact]
    public void The_moved_pages_are_no_longer_wpf()
    {
        Assert.False(NativePages.IsWpf(NativePage.Stops));
        Assert.False(NativePages.IsWpf(NativePage.StopQualification));
        Assert.False(NativePages.IsWpf(NativePage.StopPlanning));
        Assert.False(NativePages.IsWpf(NativePage.Dashboard));
        Assert.Equal("maintenance/planification?ligne=PC01&composant=Mo%C3%A2teur", NativePages.PreventiveRoute("PC01", "Moâteur"));
        Assert.False(NativePages.IsWpf(NativePage.StopStatistics));
        Assert.False(NativePages.IsWpf(NativePage.Settings));
        Assert.DoesNotContain(ModuleCatalog.Navigation,
            n => n.Route is "maintenance/arrets" or "maintenance/qualification-arrets" or "maintenance/statistiques-arrets"
                     or "maintenance/planification" or "admin/parametres"
                 && n.DesktopOnly);
    }

    // ---------- Arrêts ----------

    private static ProductionStop Detected(string machine, DateTime start, int minutes, DateTime? runStart = null)
        => new()
        {
            MachineId = machine, MachineName = machine, Start = start, End = start.AddMinutes(minutes), RunStart = runStart ?? start,
            ShiftDate = start.Date, ShiftId = start.Hour is >= 6 and < 14 ? 1 : 2, Team = "Equipe A",
        };

    [Fact]
    public void Stops_get_their_cause_and_causes_left_over_are_listed()
    {
        var day = new DateTime(2026, 11, 17);
        var first = Detected("PC01", day.AddHours(13).AddMinutes(30), 30);
        // The same stop, carried on into the next poste.
        var rest = Detected("PC01", day.AddHours(14), 20, runStart: first.Start);
        var other = Detected("CC01", day.AddHours(9), 15);
        var quals = new List<StopQualification>
        {
            new() { Id = 1, MachineId = "PC01", StopStart = first.Start.AddMinutes(2), StopEnd = first.End, CauseCode = "MECA", Component = "Capteur" },
            // Declared by hand, no detected stop.
            new() { Id = 2, MachineId = "CC01", StopStart = day.AddHours(16), StopEnd = day.AddHours(17), ShiftDate = day, ShiftId = 2,
                Team = "Equipe A", CauseCode = "ELEC", IsManual = true },
            // Outside the search: not listed.
            new() { Id = 3, MachineId = "CC01", StopStart = day.AddDays(-2).AddHours(9), StopEnd = day.AddDays(-2).AddHours(10), CauseCode = "MECA" },
        };
        var names = new Dictionary<string, string> { ["PC01"] = "Peseuse PC01", ["CC01"] = "Controle CC01" };
        var (from, to) = StopMatching.PostesOf(day, day);

        var stops = StopMatching.WithQualifications(new List<ProductionStop> { first, rest, other }, quals, names, from, to);

        Assert.Equal((new DateTime(2026, 11, 17, 6, 0, 0), new DateTime(2026, 11, 18, 6, 0, 0)), (from, to));
        Assert.Equal(4, stops.Count);
        Assert.Equal("MECA", first.Qualification?.CauseCode);
        Assert.Same(first.Qualification, rest.Qualification);
        Assert.Null(other.Qualification);
        var manual = Assert.Single(stops, s => s.IsManual);
        Assert.Equal(("Controle CC01", "Saisie manuelle"), (manual.MachineName, manual.TypeLabel));
        Assert.Equal(StopMatching.PostesOf(day, day.AddDays(-1)), StopMatching.PostesOf(day.AddDays(-1), day));

        Assert.Equal($"Suite {first.Start:dd/MM HH:mm}", rest.TypeLabel);
        // No reading at all: "Sans données", even when the silence began in an earlier poste.
        rest.NoDataMinutes = rest.NetMinutes;
        Assert.Equal("Sans données", rest.TypeLabel);

        var totals = Assert.Single(TeamStopTotal.Of(stops));
        Assert.Equal(("Equipe A", 4, 125.0), (totals.Team, totals.Count, totals.TotalMinutes));
    }

    [Fact]
    public async Task Stops_page_keeps_to_the_machines_and_team_of_the_account()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var admin = await app.SetupAdminAsync();
        var planning = new PlanningRepository(app.AppData, live.Factory);
        var today = app.Clock.Now.Date; // 17/11/2026
        await planning.SaveQualificationAsync(Stop("PC01", today.AddHours(9), 30, "MECA", team: TestApp.DefaultTeam));
        await planning.SaveQualificationAsync(Stop("CC01", today.AddHours(8), 20, "NETT", team: TestApp.DefaultTeam));
        await planning.SaveQualificationAsync(Stop("CC01", today.AddHours(15), 40, "ELEC", team: "Equipe Z"));
        await planning.SaveQualificationAsync(Stop("CC01", today.AddDays(-5).AddHours(9), 40, "ELEC", team: TestApp.DefaultTeam));
        var chef = await app.UserAsync(admin, "maint.cc",
            TestApp.As(await app.RoleAsync("resp-maintenance"), new DataScope(new[] { Sectors.Couscous }, TestApp.DefaultTeam, Array.Empty<string>())));

        var settings = new AppSettings();
        var service = new StopListService(live.Factory, app.Options, settings, new TeamScheduleService(settings), app.Clock);

        var all = await service.SearchAsync(admin, new StopsFilter(today, today));
        // This test base has no production tables: the qualified stops still show, with a warning.
        Assert.StartsWith("Base de production injoignable", all.ProductionError);
        Assert.Equal(new[] { "CC01", "CC01", "PC01" }, all.Stops.Select(s => s.MachineId).Order());
        Assert.Equal(90, all.NetMinutes);
        Assert.Equal(new[] { "CC01" }, (await service.SearchAsync(admin, new StopsFilter(today, today, ShiftId: 2))).Stops.Select(s => s.MachineId));
        // The page narrows what it read to a poste or a team without reading again.
        var afternoon = StopListService.Narrow(all, admin, new StopsFilter(today, today, ShiftId: 2));
        Assert.Equal(("CC01", 40.0), (Assert.Single(afternoon.Stops).MachineId, Assert.Single(afternoon.Teams).TotalMinutes));

        // The couscous lines, the account's team only, whatever team is asked for.
        Assert.Equal(new[] { "CC01" }, (await service.MachinesAsync(chef)).Select(m => m.MachineId));
        var mine = await service.SearchAsync(chef, new StopsFilter(today, today, Team: "Equipe Z"));
        var stop = Assert.Single(mine.Stops);
        Assert.Equal(("CC01", "Nettoyage", TestApp.DefaultTeam), (stop.MachineId, stop.CauseLabel, stop.Team));
        Assert.Equal(2, (await service.SearchAsync(chef, new StopsFilter(today.AddDays(-5), today))).Stops.Count);

        await Assert.ThrowsAsync<RuleViolationException>(() => service.SearchAsync(admin, new StopsFilter(today.AddYears(-1), today)));
    }

    [Fact]
    public void Stop_sheet_is_a_pdf_with_the_logo()
    {
        var settings = new AppSettings();
        var day = new DateTime(2026, 11, 17);
        var stops = new List<ProductionStop> { Detected("PC01", day.AddHours(9), 30), Detected("CC01", day.AddHours(15), 12) };

        var pdf = StopSheetPdf.Build(stops, settings, day.AddHours(16));

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.NotNull(typeof(StopSheetPdf).Assembly.GetManifestResourceStream("cds-logo.png"));
    }

    // ---------- Qualification des arrêts ----------

    private static QualRow Row(string machine, DateTime start, int minutes, string team, DateTime? runStart = null, StopQualification? q = null)
        => new()
        {
            MachineId = machine, MachineName = machine, Start = start, End = start.AddMinutes(minutes), RunStart = runStart ?? start,
            ShiftDate = start.Date, ShiftId = start.Hour is >= 6 and < 14 ? 1 : 2, Team = team, Minutes = minutes, Qualification = q,
        };

    [Fact]
    public async Task A_cause_goes_to_every_poste_of_the_stop_and_the_view_counts_again()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var admin = await app.SetupAdminAsync();
        var settings = new AppSettings();
        var service = new StopQualificationService(live.Factory, app.Options, settings, new TeamScheduleService(settings), app.Clock);
        var author = new ChangeAuthor("admin", "Admin", "PC-TEST");
        var day = app.Clock.Now.Date;
        // One long stop over two postes (two teams), and another stop.
        var first = Row("PC01", day.AddHours(13).AddMinutes(30), 30, "Equipe B");
        var rest = Row("PC01", day.AddHours(14), 40, "Equipe A", runStart: first.Start);
        var other = Row("CC01", day.AddHours(9), 15, "Equipe B");
        var board = new QualBoard { All = new() { first, rest, other } };

        Assert.Equal((3, 0.0), (board.View(null, true).OpenCount, board.View(null, true).Rate));
        await Assert.ThrowsAsync<RuleViolationException>(() => service.QualifyAsync(admin, author, board, new[] { first }, new QualForm(null)));

        var written = await service.QualifyAsync(admin, author, board, new[] { first },
            new QualForm("MECA", " Capteur ", "Karim", "changé", day.AddDays(1), "08:30"));

        Assert.Equal(2, written);
        Assert.Equal(("MECA", "Capteur", "Equipe A"), (rest.Qualification!.CauseCode, rest.Qualification.Component, rest.Qualification.Team));
        Assert.Equal(day.AddDays(1).AddHours(8.5), first.Qualification!.ExpectedEnd);
        var view = board.View(null, true);
        Assert.Equal(other, Assert.Single(view.Rows));
        Assert.Equal(67, view.Rate);
        // Team B's view: its poste of the long stop, and its other stop.
        Assert.Equal(2, board.View("Equipe B", false).Rows.Count);

        var saved = await new PlanningRepository(app.AppData, live.Factory).GetQualificationsAsync(day, day.AddDays(1));
        Assert.Equal(2, saved.Count);
        // Removed from the whole stop (both postes), not given back by the next load.
        Assert.Equal(2, await service.RemoveAsync(admin, author, board, rest));
        Assert.Equal(3, board.View(null, true).Rows.Count);
        Assert.Empty(await new PlanningRepository(app.AppData, live.Factory).GetQualificationsAsync(day, day.AddDays(1)));
    }

    [Fact]
    public async Task A_panne_declared_by_hand_shows_and_keeps_to_the_account_lines()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var admin = await app.SetupAdminAsync();
        var chef = await app.UserAsync(admin, "maint.cc",
            TestApp.As(await app.RoleAsync("resp-maintenance"), new DataScope(new[] { Sectors.Couscous }, TestApp.DefaultTeam, Array.Empty<string>())));
        var settings = new AppSettings();
        var service = new StopQualificationService(live.Factory, app.Options, settings, new TeamScheduleService(settings), app.Clock);
        var author = new ChangeAuthor("maint.cc", "Maintenance", "PC-TEST");
        var machines = await service.MachinesAsync(chef);
        Assert.Equal(new[] { "CC01" }, machines.Select(m => m.MachineId));
        var day = app.Clock.Now.Date; // 17/11 10:00

        await Assert.ThrowsAsync<RuleViolationException>(() => service.DeclareAsync(chef, author, machines,
            new ManualStop("PC01", day, TimeSpan.FromHours(8), TimeSpan.FromHours(9)), new QualForm("ELEC")));
        await Assert.ThrowsAsync<RuleViolationException>(() => service.DeclareAsync(chef, author, machines,
            new ManualStop("CC01", day, TimeSpan.FromHours(8), TimeSpan.FromHours(9)), new QualForm("ELEC", ExpectedDate: day, ExpectedTime: "25h")));
        // A poste of the account's team (the rotation moves them), and one of another team.
        var rota = new TeamScheduleService(settings);
        var postes = Enumerable.Range(0, 6).SelectMany(d => new[] { (Day: day.AddDays(-d), Shift: 1, Hour: 8), (Day: day.AddDays(-d), Shift: 2, Hour: 15) })
            .Where(p => p.Day.AddHours(p.Hour) < app.Clock.Now).ToList();
        var own = postes.First(p => rota.GetTeamForShift(p.Day, p.Shift) == TestApp.DefaultTeam);
        var foreign = postes.First(p => rota.GetTeamForShift(p.Day, p.Shift) != TestApp.DefaultTeam);
        await Assert.ThrowsAsync<RuleViolationException>(() => service.DeclareAsync(chef, author, machines,
            new ManualStop("CC01", foreign.Day, TimeSpan.FromHours(foreign.Hour), TimeSpan.FromHours(foreign.Hour + 1)), new QualForm("ELEC")));
        var q = await service.DeclareAsync(chef, author, machines,
            new ManualStop("CC01", own.Day, TimeSpan.FromHours(own.Hour), TimeSpan.FromHours(own.Hour + 1)), new QualForm("ELEC"));
        Assert.Equal((true, TestApp.DefaultTeam), (q.IsManual, q.Team));

        var board = await service.LoadAsync(chef, author, new QualFilter(7));
        // No production tables in this test base: said, and the declared panne still listed.
        Assert.NotNull(board.ProductionError);
        var row = Assert.Single(board.View(StopQualificationService.TeamFor(chef, "Equipe Z"), false).Rows);
        Assert.Equal(("CC01", "Panne électrique", true), (row.MachineId, row.CauseLabel, row.IsManual));
        Assert.Contains("Capteur", board.Components);

        // Removing a declared panne takes it off the list.
        await service.RemoveAsync(chef, author, board, row);
        Assert.Empty(board.View(null, false).Rows);
    }

    [Fact]
    public void Same_component_failing_again_asks_for_a_preventive()
    {
        var day = new DateTime(2026, 11, 17);
        var board = new QualBoard
        {
            Recent30 = Enumerable.Range(1, 2).Select(i => new StopQualification
                { Id = i, MachineId = "PC01", CauseCode = "MECA", Component = "Capteur", StopStart = day.AddDays(-i) }).ToList(),
        };
        Assert.Contains("3e panne en 30 jours", StopQualificationService.RepeatWarning(board, "PC01", "Peseuse PC01", "MECA", "capteur ", 0));
        Assert.Equal("", StopQualificationService.RepeatWarning(board, "PC01", "Peseuse PC01", "NETT", "Capteur", 0));
        Assert.Equal("", StopQualificationService.RepeatWarning(board, "PC01", "Peseuse PC01", "MECA", "Capteur", 1));
        Assert.Equal(day.AddHours(6), StopQualificationService.ReadExpected(day, ""));
        Assert.Null(StopQualificationService.ReadExpected(null, "10:00"));
    }

    // ---------- Planification des arrêts ----------

    [Fact]
    public void Board_periods_slots_and_drags_snap_as_in_a_calendar()
    {
        var thu = new DateTime(2026, 11, 19, 10, 0, 0);
        var week = PlanningBoard.StartOf(thu, PlanRange.Week);
        Assert.Equal(new DateTime(2026, 11, 16, 6, 0, 0), week);
        Assert.Equal(new DateTime(2026, 11, 1, 6, 0, 0), PlanningBoard.StartOf(thu, PlanRange.Month));
        // The Nuit after midnight belongs to the day before.
        Assert.Equal(new DateTime(2026, 11, 18, 6, 0, 0), PlanningBoard.StartOf(new DateTime(2026, 11, 19, 3, 0, 0), PlanRange.Day));
        Assert.Equal(new DateTime(2026, 11, 19, 6, 0, 0), PlanningBoard.Rescope(week, PlanRange.Week, PlanRange.Day, thu));
        Assert.Equal(week.AddDays(-7), PlanningBoard.Step(week, PlanRange.Week, -1));

        // A click on a week picks its poste; a drag snaps to postes.
        var at = (DateTime t) => (t - week).TotalHours / (7 * 24);
        Assert.Equal((week.AddHours(32), week.AddHours(40)), PlanningBoard.PickSlot(week, PlanRange.Week, at(week.AddHours(35)), at(week.AddHours(35))));
        Assert.Equal((week.AddHours(8), week.AddHours(32)), PlanningBoard.PickSlot(week, PlanRange.Week, at(week.AddHours(30)), at(week.AddHours(9))));
        // On a day: an hour for a click.
        var day = PlanningBoard.StartOf(thu, PlanRange.Day);
        Assert.Equal((day.AddHours(4), day.AddHours(5)), PlanningBoard.PickSlot(day, PlanRange.Day, 4.4 / 24, 4.4 / 24));

        var e = new MaintenanceEvent { MachineId = "PC01", PlannedStart = week.AddHours(8), DurationMinutes = 120 };
        Assert.Equal((week.AddHours(16), week.AddHours(18)), PlanningBoard.Moved(e, PlanRange.Week, week, PlanDrag.Move, 7.0 / 168));
        Assert.Equal((week.AddHours(8), week.AddHours(16)), PlanningBoard.Moved(e, PlanRange.Week, week, PlanDrag.End, 5.0 / 168));
        // Never shorter than a step.
        Assert.Equal((week.AddHours(2), week.AddHours(10)), PlanningBoard.Moved(e, PlanRange.Week, week, PlanDrag.Start, 6.0 / 168));

        Assert.Equal(new DateTime(2026, 11, 19, 14, 30, 0), PlanningBoard.At(thu, "14h30"));
        Assert.Null(PlanningBoard.At(thu, "25:00"));
        Assert.Equal(new DateTime(2026, 11, 20, 6, 0, 0), PlanningBoard.PosteEnd(new DateTime(2026, 11, 19, 22, 0, 0), 3));
        Assert.Throws<RuleViolationException>(() => PlanningBoard.Span(thu, "10:00", thu, "09:00"));
    }

    [Fact]
    public void Board_shows_slots_pannes_rules_and_alerts()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        var start = PlanningBoard.StartOf(now, PlanRange.Week);
        var machines = new List<Machine>
        {
            new() { MachineId = "PC01", MachineName = "Peseuse PC01", Sector = "Pate courte", IsActive = true },
            new() { MachineId = "CC01", MachineName = "Controle CC01", Sector = "Couscous", IsActive = true },
        };
        var rule = new MaintenancePlan { Id = 7, MachineId = "PC01", Task = "Graissage", Kind = MaintenanceKind.Calendar, IntervalDays = 7, LastDoneAt = now.AddDays(-9) };
        var data = new PlanData
        {
            Now = now, Range = PlanRange.Week, Start = start, End = PlanningBoard.EndOf(start, PlanRange.Week), Machines = machines, Shown = machines,
            Events = new()
            {
                new() { Id = 1, MachineId = "PC01", Task = "Pas de commande", Kind = SlotKind.PlannedStop, PlannedStart = now.AddHours(-1), DurationMinutes = 480 },
                new() { Id = 2, MachineId = "CC01", Task = "Vidange", PlannedStart = now.AddDays(-2), DurationMinutes = 60 },
                new() { Id = 3, MachineId = "CC01", Task = "Courroie", PlannedStart = now.AddDays(-1), DurationMinutes = 60, Status = PlanStatus.Done, DoneAt = now.AddDays(-1).AddMinutes(30) },
            },
            Pannes = new() { new() { Id = 9, MachineId = "PC01", CauseCode = "MECA", StopStart = now.AddHours(-5), StopEnd = now.AddHours(-4) } },
            Plans = new() { rule },
        };

        var view = PlanningBoard.Build(data);

        Assert.Equal(7, view.Columns.Count);
        Assert.Equal(new[] { "Pâte courte", "Couscous" }, view.Sectors.Select(s => s.Name));
        var pc = view.Sectors[0].Rows.Single();
        Assert.Equal(("Arrêt planifié", "stop"), (pc.StateText, pc.StateCss));
        Assert.Equal(new[] { PlanBlockKind.PlannedStop, PlanBlockKind.Panne }, pc.Blocks.Select(b => b.Kind));
        var cc = view.Sectors[1].Rows.Single().Blocks;
        Assert.Equal(new[] { PlanBlockKind.MaintenanceDone, PlanBlockKind.MaintenanceLate }, cc.Select(b => b.Kind));
        Assert.True(cc[1].Draggable);
        Assert.False(cc[0].Draggable);
        Assert.Equal(("1", "8 h 00 d'arrêt prévu · 1 en cours"), (view.Stops.Value, view.Stops.Sub));
        Assert.Equal(("2", "1 préventif en retard"), (view.Maintenances.Value, view.Maintenances.Sub));
        var r = Assert.Single(view.Rules);
        Assert.Equal(("En retard de 2 j", "bad"), (r.StatusText, r.StatusCss));
        Assert.Equal(2, view.Alerts.Count);
        Assert.Contains(view.Alerts, a => a.Text.Contains("Graissage"));
        Assert.Contains(view.Alerts, a => a.Text.Contains("« Vidange »") && a.Machine == "Controle CC01");

        var late = PlanningBoard.Detail(data, cc[1], canValidate: false);
        Assert.Equal(("Non validée", false, true), (late.Status, late.CanClose, late.CanEdit));
        Assert.Equal("Valider", PlanningBoard.Detail(data, cc[1], canValidate: true).CloseText);
        var stop = PlanningBoard.Detail(data, pc.Blocks[0], canValidate: false);
        Assert.Equal(("En cours", true, "Terminer"), (stop.Status, stop.CanClose, stop.CloseText));
    }

    [Fact]
    public async Task Board_writes_slots_and_rules_within_the_account_lines()
    {
        using var app = new TestApp();
        using var live = new LiveDb();
        var admin = await app.SetupAdminAsync();
        var chef = await app.UserAsync(admin, "maint.cc",
            TestApp.As(await app.RoleAsync("resp-maintenance"), new DataScope(new[] { Sectors.Couscous }, TestApp.DefaultTeam, Array.Empty<string>())));
        var settings = new AppSettings();
        var service = new PlanningBoardService(live.Factory, app.Options, settings, app.Clock);
        var author = new ChangeAuthor("maint.cc", "Maintenance", "PC-TEST");
        var machines = await service.MachinesAsync(chef);
        Assert.Equal(new[] { "CC01" }, machines.Select(m => m.MachineId));
        var day = app.Clock.Now.Date; // 17/11 10:00

        await Assert.ThrowsAsync<RuleViolationException>(() => service.SaveEventAsync(chef, author, machines,
            new EventForm(SlotKind.Maintenance, "PC01", "Graissage", day, "14:00", day, "15:00"), null, null));
        await Assert.ThrowsAsync<RuleViolationException>(() => service.SaveEventAsync(chef, author, machines,
            new EventForm(SlotKind.Maintenance, "CC01", "Graissage", day, "14:00", day, "13:00"), null, null));
        var rule = await service.SavePlanAsync(chef, author, machines, new PlanForm("CC01", "Graissage", MaintenanceKind.Calendar, "14", "60", day.AddDays(-20)), null);
        var e = await service.SaveEventAsync(chef, author, machines,
            new EventForm(SlotKind.Maintenance, "CC01", " Graissage ", day, "08h", day, "09:30", "Karim"), null, rule.Id);
        Assert.Equal(("Graissage", 90, "Karim"), (e.Task, e.DurationMinutes, e.Technician));
        var stop = await service.SaveEventAsync(chef, author, machines,
            new EventForm(SlotKind.PlannedStop, "CC01", "Jour férié", day.AddDays(1), "06:00", day.AddDays(2), "06:00", "ignoré"), null, null);
        Assert.Null(stop.Technician);

        var start = PlanningBoard.StartOf(day, PlanRange.Week);
        var data = await service.LoadAsync(chef, machines, new[] { "CC01" }, PlanRange.Week, start);
        Assert.Equal(2, data.Events.Count);
        Assert.Single(data.Plans);

        // Moved, then validated (without the right: refused; with it, the rule starts again).
        await service.MoveEventAsync(chef, author, e, day.AddHours(10), day.AddHours(11));
        await Assert.ThrowsAsync<RuleViolationException>(() => service.CloseAsync(chef, author, data, e, canValidate: false));
        await service.CloseAsync(chef, author, data, e, canValidate: true);
        Assert.Equal(PlanStatus.Done, e.Status);
        Assert.Equal(app.Clock.Now, data.Plans.Single().LastDoneAt);
        // Deleted: cancelled, off the board.
        await service.DeleteAsync(chef, author, stop);
        data = await service.LoadAsync(chef, machines, new[] { "CC01" }, PlanRange.Week, start);
        Assert.Equal(new[] { e.Id }, data.Events.Select(x => x.Id));

        await Assert.ThrowsAsync<RuleViolationException>(() => service.MarkPlanDoneAsync(chef, author, rule, canValidate: false));
        await service.DeletePlanAsync(chef, author, rule);
        Assert.Empty((await service.LoadAsync(chef, machines, new[] { "CC01" }, PlanRange.Week, start)).Plans);
    }
}
