using System.Windows;
using System.Windows.Controls;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App;

public partial class PlanningView : UserControl
{
    private bool _initialized;

    public PlanningView(PlanningViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Loaded comes again each time the view is put back on screen: once only.
        Loaded += async (_, _) =>
        {
            if (_initialized)
                return;
            _initialized = true;
            await viewModel.InitializeAsync();
        };
    }

    // "Fermer": the host closes its window, or the main window goes back.
    public event Action? CloseRequested;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
