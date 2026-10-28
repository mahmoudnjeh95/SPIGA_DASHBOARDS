using System.Windows;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

public partial class StopsWindow : Window
{
    public StopsWindow(StopsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowSizing.FitToWorkArea(this);
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
