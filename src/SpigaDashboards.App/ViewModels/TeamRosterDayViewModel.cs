using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One row of the roster grid: which team (or -1 = Repos) works each of the
// 3 postes on this day of the rotation cycle.
public partial class TeamRosterDayViewModel : ObservableObject
{
    public int CycleDay { get; init; }

    [ObservableProperty] private int matinTeamIndex = -1;
    [ObservableProperty] private int soirTeamIndex = -1;
    [ObservableProperty] private int nuitTeamIndex = -1;
}
