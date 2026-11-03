using System.Windows;
using System.Windows.Input;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SwitchableConnectionFactory _activeFactory;
    private readonly ProductionRepository _repository;
    private readonly SharedSettingsSync _sharedSettings;
    private TvWindow? _tvWindow;

    public MainWindow(
        MainViewModel viewModel,
        SettingsService settingsService,
        AppSettings settings,
        SwitchableConnectionFactory activeFactory,
        ProductionRepository repository,
        SharedSettingsSync sharedSettings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _settings = settings;
        _activeFactory = activeFactory;
        _repository = repository;
        _sharedSettings = sharedSettings;

        DataContext = viewModel;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        // Back on the TV after a restart, if it was left open there.
        ContentRendered += (_, _) => { if (_settings.TvScreen.IsOpen) OpenTvWindow(activate: false); };
        Closed += (_, _) => _tvWindow?.CloseForShutdown();

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

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Edit the shared settings as the other PCs last saved them.
        await _sharedSettings.PullAsync();
        var settingsViewModel = new SettingsViewModel(
            _settingsService, _settings, _activeFactory, _repository, _sharedSettings,
            onApplied: () =>
            {
                _ = _viewModel.ReconnectCommand.ExecuteAsync(null);
                _ = _tvWindow?.ReconnectAsync();
            });

        var window = new SettingsWindow(settingsViewModel) { Owner = this };
        window.ShowDialog();
    }

    private void TvScreenButton_Click(object sender, RoutedEventArgs e) => OpenTvWindow(activate: true);

    private void OpenTvWindow(bool activate)
    {
        if (_tvWindow != null)
        {
            if (_tvWindow.WindowState == WindowState.Minimized)
                _tvWindow.WindowState = WindowState.Normal;
            _tvWindow.Activate();
            return;
        }

        // Its own deck, not this window's: filters set here stay off the TV.
        var tvViewModel = new MainViewModel(_repository, _settings, new TeamScheduleService(_settings), _sharedSettings);
        _tvWindow = new TvWindow(tvViewModel, _settings, _settingsService) { ShowActivated = activate };
        _tvWindow.Closed += (_, _) => _tvWindow = null;
        _tvWindow.Show();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var historyViewModel = new HistoryViewModel(_repository, new TeamScheduleService(_settings), _settings,
            _viewModel.CurrentFilter, _viewModel.CurrentRange);
        var window = new HistoryWindow(historyViewModel) { Owner = this };
        window.ShowDialog();
    }

    private void StopsButton_Click(object sender, RoutedEventArgs e)
    {
        var stopsViewModel = new StopsViewModel(_repository, new TeamScheduleService(_settings), _settings, _viewModel.CurrentRange);
        var window = new StopsWindow(stopsViewModel) { Owner = this };
        window.ShowDialog();
    }

    private void PlanningButton_Click(object sender, RoutedEventArgs e)
    {
        var context = new PlanningContext(_repository, new PlanningRepository(_activeFactory), _settings, _settingsService,
            new TeamScheduleService(_settings));
        new PlanningWindow(new PlanningViewModel(context)) { Owner = this }.ShowDialog();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void NoteTextBox_GotFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = true;

    private void NoteTextBox_LostFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = false;
}
