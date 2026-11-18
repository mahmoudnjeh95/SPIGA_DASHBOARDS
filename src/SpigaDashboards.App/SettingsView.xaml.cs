using System.Windows;
using System.Windows.Controls;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _viewModel;

    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    // Saved or cancelled: the host closes its window, or the main window goes back.
    public event Action? CloseRequested;

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
        CloseRequested?.Invoke();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }
}
