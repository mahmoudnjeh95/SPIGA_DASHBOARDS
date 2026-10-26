using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One machine checkbox of the filter bar's machine list.
public partial class MachineFilterOption : ObservableObject
{
    public string MachineId { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string Sector { get; init; } = "";

    [ObservableProperty] private bool isSelected = true;
}
