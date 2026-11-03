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
        // Bad cadence or pause cell: keep the window open, the message says which one.
        if (!_viewModel.Validate())
            return;
        await _viewModel.SaveCommand.ExecuteAsync(null);
        // The shared settings did not reach the base: the message says what to do.
        if (_viewModel.SharedSettingsMessage.Length > 0)
            return;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
