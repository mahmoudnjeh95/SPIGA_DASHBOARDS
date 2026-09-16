using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One entry in a roster-grid ComboBox: Index -1 is "Repos" (no team assigned
// that poste), 0-3 map to AppSettings.TeamRotation.TeamNames. Name is kept in
// sync with the editable team-name fields so renaming a team live-updates
// every cell already showing it, without rebuilding the ComboBox items (which
// would drop the current selection).
public partial class TeamOption : ObservableObject
{
    public int Index { get; init; }
    [ObservableProperty] private string name = "";
}
