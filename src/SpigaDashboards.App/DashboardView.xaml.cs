using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App;

public partial class DashboardView : UserControl
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly SwitchableConnectionFactory _activeFactory;
    private readonly ProductionRepository _repository;
    private readonly SharedSettingsSync _sharedSettings;
    private TvWindow? _tvWindow;
    private bool _initialized;

    public DashboardView(
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
        // Escape closes the kiosk window (KioskWindow), never the main application.
        KeyDown += (_, e) => { if (e.Key == Key.Escape && Window.GetWindow(this) is KioskWindow kiosk) kiosk.Close(); };

        // Stop pop-up: at its saved place each time it comes up (moved on the
        // other window, or put back in Parametres), saved where it is dropped.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsStopAlertShown) && viewModel.IsStopAlertShown)
                StopAlert.SetOffset(_settings.StopAlertOffsetX, _settings.StopAlertOffsetY);
        };
        StopAlert.Moved += (x, y) =>
        {
            _settings.StopAlertOffsetX = x;
            _settings.StopAlertOffsetY = y;
            _settingsService.Save(_settings);
        };
        // Loaded comes again each time the view is put back on screen: once only.
        Loaded += async (_, _) =>
        {
            if (_initialized)
                return;
            _initialized = true;
            await viewModel.InitializeAsync();
            // Back on the TV after a restart, if it was left open there.
            if (_settings.TvScreen.IsOpen)
                OpenTvWindow(activate: false);
        };

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
        if (PageRequested != null)
        {
            PageRequested("parametres");
            return;
        }
        var window = new SettingsWindow(await CreateSettingsViewModelAsync()) { Owner = OwnerWindow };
        window.ShowDialog();
    }

    public async Task<SettingsViewModel> CreateSettingsViewModelAsync()
    {
        // Edit the shared settings as the other PCs last saved them.
        await _sharedSettings.PullAsync();
        return new SettingsViewModel(
            _settingsService, _settings, _activeFactory, _repository, _sharedSettings,
            onApplied: () =>
            {
                _ = _viewModel.ReconnectCommand.ExecuteAsync(null);
                _ = _tvWindow?.ReconnectAsync();
            });
    }

    public StopsViewModel CreateStopsViewModel()
        => new(_repository, new TeamScheduleService(_settings), _settings, _viewModel.CurrentRange);

    public PlanningViewModel CreatePlanningViewModel()
        => new(new PlanningContext(_repository, new PlanningRepository(_activeFactory), _settings, _settingsService,
            new TeamScheduleService(_settings)));

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
        var window = new HistoryWindow(historyViewModel) { Owner = OwnerWindow };
        window.ShowDialog();
    }

    private void StopsButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageRequested != null)
        {
            PageRequested("arrets");
            return;
        }
        new StopsWindow(CreateStopsViewModel()) { Owner = OwnerWindow }.ShowDialog();
    }

    private void PlanningButton_Click(object sender, RoutedEventArgs e)
    {
        if (PageRequested != null)
        {
            PageRequested("planification");
            return;
        }
        new PlanningWindow(CreatePlanningViewModel()) { Owner = OwnerWindow }.ShowDialog();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = OwnerWindow }.ShowDialog();
    }

    // The window the dialogs belong to: the one showing this view, else the
    // application's main window (view not on screen yet).
    private Window? OwnerWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    // The application closes: the TV window remembers it was open.
    public void CloseTvForShutdown() => _tvWindow?.CloseForShutdown();

    // ---- Used by SPIGA Suivi Production (ShellWindow) ----

    // Embedded in the main window, the theme follows the signed-in user's
    // choice: no theme button of its own.
    public void SetEmbedded(bool embedded)
        => ThemeToggleButton.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;

    // The station's settings (database connection, TV, cadences) stay with
    // the roles that have Paramètres > Administrer.
    public void AllowSettings(bool allowed)
        => SettingsButton.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;

    public void OpenTvScreen() => OpenTvWindow(activate: true);

    // Set in the main window: the Arrêts, Planification and Paramètres
    // buttons open those pages beside the menu ("arrets", "planification",
    // "parametres") instead of dialogs. Null in kiosk mode.
    public Action<string>? PageRequested { get; set; }

    private void NoteTextBox_GotFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = true;

    private void NoteTextBox_LostFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = false;
}
