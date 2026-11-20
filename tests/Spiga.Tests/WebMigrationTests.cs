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
        Assert.False(NativePages.IsWpf(NativePage.StopStatistics));
        Assert.False(NativePages.IsWpf(NativePage.Settings));
        Assert.True(NativePages.IsWpf(NativePage.StopPlanning));
        Assert.DoesNotContain(ModuleCatalog.Navigation,
            n => n.Route is "maintenance/arrets" or "maintenance/statistiques-arrets" or "admin/parametres" && n.DesktopOnly);
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
        Assert.NotNull(typeof(StopSheetPdf).Assembly.GetManifestResourceStream("spiga-logo.png"));
    }
}
