using CommunityToolkit.Mvvm.ComponentModel;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App.ViewModels.Slides;

// One sector slide: 1-2 machines side by side (2 unless the sector has an
// odd machine out, e.g. PC2's third machine gets its own slide with no
// Secondary panel rather than a blank second slot).
public partial class MachinePairSlideViewModel : ObservableObject
{
    [ObservableProperty] private string sectorLabel = "";
    [ObservableProperty] private MachineDashboardViewModel primary = null!;
    [ObservableProperty] private MachineDashboardViewModel? secondary;

    public static async Task<MachinePairSlideViewModel> LoadAsync(
        ProductionRepository repository,
        TeamScheduleService teamSchedule,
        string sector,
        IReadOnlyList<Machine> machines,
        DashboardPeriod period)
    {
        var slide = new MachinePairSlideViewModel { SectorLabel = sector };

        var primary = new MachineDashboardViewModel(repository, teamSchedule);
        await primary.LoadAsync(machines[0], period);
        slide.Primary = primary;

        if (machines.Count > 1)
        {
            var secondary = new MachineDashboardViewModel(repository, teamSchedule);
            await secondary.LoadAsync(machines[1], period);
            slide.Secondary = secondary;
        }

        return slide;
    }
}
