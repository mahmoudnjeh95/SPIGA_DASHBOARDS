using System.Windows;
using System.Windows.Input;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SwitchableConnectionFactory _activeFactory;
    private readonly ProductionRepository _repository;

    public MainWindow(
        MainViewModel viewModel,
        SettingsService settingsService,
        AppSettings settings,
        SwitchableConnectionFactory activeFactory,
        ProductionRepository repository)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _settings = settings;
        _activeFactory = activeFactory;
        _repository = repository;

        DataContext = viewModel;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await viewModel.InitializeAsync();

        UpdateThemeToggleLabel();
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Toggle(_settings, _settingsService);
        UpdateThemeToggleLabel();
    }

    private void UpdateThemeToggleLabel()
    {
        ThemeToggleButton.Content = ThemeService.CurrentTheme == AppTheme.Dark ? "Mode clair" : "Mode sombre";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsViewModel = new SettingsViewModel(
            _settingsService, _settings, _activeFactory, _repository,
            onApplied: () => _ = _viewModel.ReconnectCommand.ExecuteAsync(null));

        var window = new SettingsWindow(settingsViewModel) { Owner = this };
        window.ShowDialog();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var historyViewModel = new HistoryViewModel(_repository, new TeamScheduleService(_settings), _settings);
        var window = new HistoryWindow(historyViewModel) { Owner = this };
        window.ShowDialog();
    }

    private void NoteTextBox_GotFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = true;

    private void NoteTextBox_LostFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = false;
}
