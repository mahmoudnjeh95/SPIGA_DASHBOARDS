using System.Windows;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        WindowSizing.FitToWorkArea(this);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.Password = PasswordBox.Password;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Bad cadence cell: keep the window open, the message says which one.
        if (!_viewModel.ValidateNominalRates())
            return;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
