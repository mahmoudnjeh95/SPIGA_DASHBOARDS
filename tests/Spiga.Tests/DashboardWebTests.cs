using Spiga.Core.Security;
using Spiga.UI.Services;
using SpigaDashboards.App.Models;
using Xunit;

namespace Spiga.Tests;

// The web dashboard's charts (ECharts through SpigaChart): stacked bars and
// the weight per lot on a time axis.
public class DashboardWebTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 6, 0, 0);

    private static LotSpan Lot(string production, int fromMinute, int toMinute, long bags, double average, double target = 500)
        => new() { ProductionId = production, StartTime = Start.AddMinutes(fromMinute), EndTime = Start.AddMinutes(toMinute), Bags = bags, Average = average, Target = target };

    [Fact]
    public void Weight_per_lot_groups_lots_up_to_1000_bags_and_breaks_the_line_on_a_gap()
    {
        var lots = new[]
        {
            Lot("P1", 0, 10, 500, 501),
            Lot("P1", 10, 20, 500, 503),   // with the first: one 1000-bag group, average 502
            Lot("P1", 60, 80, 1000, 499),  // 40 min later: a gap before it
        };
        var chart = DeckCharts.WeightLots(lots, Start, Start.AddHours(8), live: false);

        Assert.True(chart.HasData);
        Assert.NotNull(chart.Times);
        var avg = chart.Series.Single(s => s.Name.StartsWith("Poids moyen")).Values;
        Assert.Equal(new double?[] { 502, 499 }, avg.Where(v => v is { } x && !double.IsNaN(x)).ToArray());
        Assert.Contains(avg, v => v is { } x && double.IsNaN(x));
        Assert.True(chart.Series.Single(s => s.Name == "Cible").Reference);
        Assert.Equal(chart.Times!.OrderBy(t => t), chart.Times);
    }

    [Fact]
    public void Weight_per_lot_leaves_out_the_group_in_progress()
    {
        var lots = new[] { Lot("P1", 0, 10, 400, 501) };
        Assert.False(DeckCharts.WeightLots(lots, Start, Start.AddHours(8), live: true).HasData);
        Assert.True(DeckCharts.WeightLots(lots, Start, Start.AddHours(8), live: false).HasData);
    }

    [Fact]
    public void Stacked_bars_pile_every_part_on_one_stack()
    {
        var chart = DeckCharts.Stacked(new[] { "PC01", "PC02" },
            new (string, IReadOnlyList<double?>)[] { ("Equipe A", new double?[] { 10, 20 }), ("Equipe B", new double?[] { 5, 0 }) },
            v => v.ToString("0"));
        Assert.Equal(2, chart.Series.Count);
        Assert.All(chart.Series, s => Assert.Equal("s", s.Stack));

        var empty = DeckCharts.Stacked(new[] { "PC01" }, new (string, IReadOnlyList<double?>)[] { ("Equipe A", new double?[] { 0 }) }, v => "");
        Assert.False(empty.HasData);
    }

    [Fact]
    public void The_dashboard_is_a_razor_page_in_both_hosts()
        => Assert.False(NativePages.IsWpf(NativePage.Dashboard));

    [Fact]
    public void A_hall_screen_reads_the_whole_plant_and_writes_nothing()
    {
        var screen = ScreenAccount.Create(new AppSettings());
        Assert.True(NativePages.IsAllowed(screen, NativePage.Dashboard));
        Assert.True(NativePages.IsAllowed(screen, NativePage.Stops));
        Assert.True(screen.CanEverywhere(Perm.EcranTv));
        Assert.False(DashboardDeckService.CanEditNote(screen));
        Assert.False(NativePages.IsAllowed(screen, NativePage.Settings));
        Assert.False(NativePages.IsAllowed(screen, NativePage.StopQualification));
        Assert.False(screen.Can(Perm.ComptesGerer));
    }

    private static Actor Account(params string[] permissions)
    {
        var role = new Role { Code = "r", Name = "R", Permissions = new PermissionSet(permissions) };
        return new Actor(new AppUser { Id = 9, Login = "u", DisplayName = "U" },
            new[] { new Actor.Grant(role, new RoleAssignment { RoleName = "R", Scope = DataScope.Everything }) });
    }

    [Fact]
    public void Each_account_opens_what_its_rights_give()
    {
        var screens = new[] { Perm.View(Modules.Conditionnement), Perm.TableauDetaille };
        Assert.Equal(new[] { SpigaMode.Tv }, OpenModes.For(Account(screens.Append(Perm.EcranTv).ToArray())));
        Assert.Equal(new[] { SpigaMode.Dashboard }, OpenModes.For(Account(screens.Append(Perm.OuvrirDashboard).ToArray())));
        Assert.Equal(new[] { SpigaMode.App, SpigaMode.Dashboard },
            OpenModes.For(Account(screens.Append(Perm.OuvrirApplication).Append(Perm.OuvrirDashboard).ToArray())));
        Assert.Equal(new[] { SpigaMode.App, SpigaMode.Dashboard, SpigaMode.Tv },
            OpenModes.For(Account(screens.Concat(new[] { Perm.OuvrirApplication, Perm.OuvrirDashboard, Perm.EcranTv }).ToArray())));
        // The screens need the detailed dashboard to show anything.
        Assert.Empty(OpenModes.For(Account(Perm.EcranTv, Perm.OuvrirDashboard)));
    }

    [Fact]
    public void The_role_templates_keep_the_application_and_the_screens_open_alone()
    {
        Assert.All(RoleTemplates.All.Where(t => !t.Code.StartsWith("ecran-")), t => Assert.Contains(Perm.OuvrirApplication, t.Permissions));
        var tv = RoleTemplates.All.Single(t => t.Code == "ecran-tv");
        Assert.Equal(new[] { SpigaMode.Tv }, OpenModes.For(Account(tv.Permissions)));
        var dashboard = RoleTemplates.All.Single(t => t.Code == "ecran-dashboard");
        Assert.Equal(new[] { SpigaMode.Dashboard }, OpenModes.For(Account(dashboard.Permissions)));
    }
}
