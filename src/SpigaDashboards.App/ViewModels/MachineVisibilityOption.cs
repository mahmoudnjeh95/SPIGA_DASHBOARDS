using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

public partial class MachineVisibilityOption : ObservableObject
{
    public string MachineId { get; init; } = "";
    public string OriginalMachineName { get; init; } = "";

    [ObservableProperty] private string machineName = "";
    [ObservableProperty] private bool isVisible;
}
