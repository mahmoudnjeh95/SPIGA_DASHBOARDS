using CommunityToolkit.Mvvm.ComponentModel;

namespace SpigaDashboards.App.ViewModels;

// One slide of the "Affichage" list in Parametres: shown or not, its place
// (moved with the arrows) and its own duration, kept as text (blank = the
// default duration).
public partial class SlideRowViewModel : ObservableObject
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";

    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private string durationText = "";
    [ObservableProperty] private int position;
}
