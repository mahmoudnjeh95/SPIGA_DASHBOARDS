using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

public partial class MachineVisibilityOption : ObservableObject
{
    public string MachineId { get; init; } = "";
    public string OriginalMachineName { get; init; } = "";

    [ObservableProperty] private string machineName = "";
    [ObservableProperty] private bool isVisible;

    // TRS cible (%), as typed; validated on save.
    [ObservableProperty] private string trsTargetText = "";

    // TRS tolerance (points), as typed; blank = Couleurs default.
    [ObservableProperty] private string trsToleranceText = "";
}
