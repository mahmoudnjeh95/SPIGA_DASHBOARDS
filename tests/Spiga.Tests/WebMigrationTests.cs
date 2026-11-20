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
        Assert.False(NativePages.IsWpf(NativePage.StopStatistics));
        Assert.False(NativePages.IsWpf(NativePage.Settings));
        Assert.True(NativePages.IsWpf(NativePage.StopPlanning));
        Assert.DoesNotContain(ModuleCatalog.Navigation, n => n.Route is "maintenance/statistiques-arrets" or "admin/parametres" && n.DesktopOnly);
    }
}
