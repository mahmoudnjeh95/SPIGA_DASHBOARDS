using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One machine checkbox of the Filtres panel (dashboard and Historique).
public partial class MachineFilterOption : ObservableObject
{
    public string MachineId { get; init; } = "";

    [ObservableProperty] private bool isSelected = true;
}
