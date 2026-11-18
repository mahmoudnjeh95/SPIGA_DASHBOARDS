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
        if (!_settingsAllowed)
            return;
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
        // Station-wide: every machine (only accounts covering the whole plant
        // get here, see AllowSettings), never the dashboard's limited view, or
        // saving would lose the settings of the machines it does not list.
        return new SettingsViewModel(
            _settingsService, _settings, _activeFactory, new ProductionRepository(_activeFactory, _settings), _sharedSettings,
            onApplied: () =>
            {
                _ = _viewModel.ReconnectCommand.ExecuteAsync(null);
                _ = _tvWindow?.ReconnectAsync();
            });
    }

    // access: the machines the page may show (SPIGA Suivi Production: the
    // scope of the account's right for that page).
    public StopsViewModel CreateStopsViewModel(MachineAccess access, string? team, bool canExport)
    {
        var stops = new StopsViewModel(new ProductionRepository(_activeFactory, _settings, access), new TeamScheduleService(_settings),
            _settings, _viewModel.CurrentRange);
        stops.LockTeam(team);
        stops.CanExport = canExport;
        return stops;
    }

    // canValidate: « Valider les interventions » (maintenance done).
    public PlanningViewModel CreatePlanningViewModel(MachineAccess access, bool canValidate)
        => new(new PlanningContext(new ProductionRepository(_activeFactory, _settings, access), new PlanningRepository(_activeFactory, access),
            _settings, _settingsService, new TeamScheduleService(_settings)) { CanValidate = canValidate });

    // The machines this dashboard shows (and its Historique, Arrêts).
    public MachineAccess Access => _repository.Access;

    private void TvScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tvAllowed)
            OpenTvWindow(activate: true);
    }

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
        // The hall screen shows the station's machines, whoever opened it
        // (only accounts covering the whole plant may, see AllowTv).
        var tvViewModel = new MainViewModel(new ProductionRepository(_activeFactory, _settings), _settings,
            new TeamScheduleService(_settings), _sharedSettings);
        _tvWindow = new TvWindow(tvViewModel, _settings, _settingsService) { ShowActivated = activate };
        _tvWindow.Closed += (_, _) => _tvWindow = null;
        _tvWindow.Show();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var historyViewModel = new HistoryViewModel(_repository, new TeamScheduleService(_settings), _settings,
            _viewModel.CurrentFilter, _viewModel.CurrentRange);
        historyViewModel.LockTeam(_viewModel.LockedTeam);
        historyViewModel.CanExport = _exportAllowed;
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
        new StopsWindow(CreateStopsViewModel(_repository.Access, _viewModel.LockedTeam, _exportAllowed)) { Owner = OwnerWindow }.ShowDialog();
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

    // Embedded in the main window: the theme follows the signed-in user's
    // choice (no theme button of its own), the menu beside it already shows
    // the Spiga logo, Arrêts and Paramètres, so the top bar names the page
    // and keeps only what the menu has not (TV, slides, Historique).
    public void SetEmbedded(bool embedded)
    {
        _embedded = embedded;
        ThemeToggleButton.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        TopBarLogo.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        TopBarTitle.Text = embedded ? "Tableau de bord" : "SPIGA Dashboards";
        StopsButton.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        AllowSettings(_settingsAllowed);
    }

    private bool _embedded;
    private bool _settingsAllowed = true;

    // The station's settings (database connection, TV, cadences) stay with
    // the roles that have Paramètres > Administrer.
    public void AllowSettings(bool allowed)
    {
        _settingsAllowed = allowed;
        SettingsButton.Visibility = allowed && !_embedded ? Visibility.Visible : Visibility.Collapsed;
        ReconnectSettingsButton.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _tvAllowed = true;

    // The hall screen: « Lancer l'écran TV » over the whole plant.
    public void AllowTv(bool allowed)
    {
        _tvAllowed = allowed;
        TvScreenButton.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _exportAllowed = true;

    // Excel / PDF of Historique and Arrêts: « Exporter » covering what is shown.
    public void AllowExport(bool allowed) => _exportAllowed = allowed;

    public void OpenTvScreen()
    {
        if (_tvAllowed)
            OpenTvWindow(activate: true);
    }

    // Set in the main window: the Arrêts and Paramètres buttons open those
    // pages beside the menu ("arrets", "parametres") instead of dialogs.
    // Null in kiosk mode.
    public Action<string>? PageRequested { get; set; }

    // A poste of another team: not selected, a note says why.
    private void ShiftButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: { } tag } button || !int.TryParse(tag.ToString(), out var shiftId) || _viewModel.IsShiftOwn(shiftId))
            return;
        e.Handled = true;
        ShowNote(button, _viewModel.ForeignShiftMessage(shiftId));
    }

    private ToolTip? _note;

    private void ShowNote(FrameworkElement target, string text)
    {
        if (_note != null)
            _note.IsOpen = false;
        _note = new ToolTip
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            IsOpen = true,
        };
        var note = _note;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            note.IsOpen = false;
        };
        timer.Start();
    }

    // The signed-in account's team (null = several or all): see MainViewModel.LockTeam.
    public void LockTeam(string? team) => _viewModel.LockTeam(team);

    private void NoteTextBox_GotFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = true;

    private void NoteTextBox_LostFocus(object sender, RoutedEventArgs e) => _viewModel.IsEditingNote = false;
}
