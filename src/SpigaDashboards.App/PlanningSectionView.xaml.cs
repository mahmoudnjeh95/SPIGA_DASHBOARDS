using System.Windows;
using System.Windows.Controls;
using SpigaDashboards.App.ViewModels.Planning;
using SpigaDashboards.App.Views.Planning;

namespace SpigaDashboards.App;

public partial class PlanningSectionView : UserControl
{
    public PlanningSectionView(PlanningViewModel viewModel, PlanningSection section)
    {
        InitializeComponent();
        DataContext = viewModel;
        TitleText.Text = PlanningViewModel.Title(section);
        SectionHost.Content = section switch
        {
            PlanningSection.Qualification => new PannesView { DataContext = viewModel.Pannes },
            PlanningSection.Statistics => new StatsView { DataContext = viewModel.Stats },
            _ => new BoardView { DataContext = viewModel.Board },
        };
        // Loaded comes again each time the page is put back on screen: the
        // first time it loads, then it refreshes (as the tabs did).
        Loaded += async (_, _) => await viewModel.ShowAsync(section);
    }

    // "Fermer": the main window goes back to the previous screen.
    public event Action? CloseRequested;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
