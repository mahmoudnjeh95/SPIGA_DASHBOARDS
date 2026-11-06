using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

// Dashboard-only window for the production-floor TV: dragged onto the TV
// screen, then double-clicked to go full screen there. It runs its own deck
// (default view, auto-rotation), so filters, Historique or Parametres used on
// the main window never show up on the TV. Its place is remembered, and it
// reopens there on the next start.
public partial class TvWindow : Window
{
    private static readonly TimeSpan CursorHideDelay = TimeSpan.FromSeconds(3);

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _cursorTimer;
    private bool _closingForShutdown;

    public TvWindow(MainViewModel viewModel, AppSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _settingsService = settingsService;
        DataContext = viewModel;

        PlaceFromSettings();

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

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        // No mouse pointer left sitting over the dashboard.
        _cursorTimer = new DispatcherTimer { Interval = CursorHideDelay };
        _cursorTimer.Tick += (_, _) => { _cursorTimer.Stop(); Cursor = Cursors.None; };
        _cursorTimer.Start();

        // Maximize only once the window exists at its saved place, so it fills
        // that screen (the TV) and not the primary one.
        SourceInitialized += (_, _) => { if (_settings.TvScreen.FullScreen) SetFullScreen(true, save: false); };
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closing += (_, _) => SavePlacement(isOpen: _closingForShutdown);
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _cursorTimer.Stop();
            _viewModel.Stop();
        };

        UpdateFullScreenUi();
    }

    public bool IsFullScreen => WindowState == WindowState.Maximized;

    public Task ReconnectAsync() => _viewModel.ReconnectCommand.ExecuteAsync(null);

    // App closing (main window closed): keep IsOpen so the TV comes back next start.
    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    private void PlaceFromSettings()
    {
        var tv = _settings.TvScreen;
        Width = Math.Max(MinWidth, tv.Width);
        Height = Math.Max(MinHeight, tv.Height);

        // Saved spot on a screen that is no longer plugged in: start on the main one.
        if (tv.Left is double left && tv.Top is double top && IsOnVirtualScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
    }

    private bool IsOnVirtualScreen(double left, double top)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        // At least the title-bar area (top-left 100x100) must be visible.
        return left + 100 > screenLeft && left < screenLeft + SystemParameters.VirtualScreenWidth - 100
            && top >= screenTop - 10 && top < screenTop + SystemParameters.VirtualScreenHeight - 100;
    }

    private void SavePlacement(bool isOpen)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var tv = _settings.TvScreen;
        tv.IsOpen = isOpen;
        tv.FullScreen = IsFullScreen;
        if (!bounds.IsEmpty)
        {
            tv.Left = bounds.Left;
            tv.Top = bounds.Top;
            tv.Width = bounds.Width;
            tv.Height = bounds.Height;
        }

        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Écran TV : position non enregistrée ({ex.Message})");
        }
    }

    private void SetFullScreen(bool fullScreen, bool save = true)
    {
        // Topmost: nothing else pops up over the dashboard on the TV.
        ResizeMode = fullScreen ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        WindowState = fullScreen ? WindowState.Maximized : WindowState.Normal;
        Topmost = fullScreen;
        UpdateFullScreenUi();
        if (save)
            SavePlacement(isOpen: true);
    }

    private void UpdateFullScreenUi()
    {
        var fullScreen = IsFullScreen;
        PlacementHint.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenMenuItem.Header = fullScreen ? "Quitter le plein écran" : "Plein écran";
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("dd/MM/yyyy  HH:mm");

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            SetFullScreen(!IsFullScreen);
        else if (!IsFullScreen)
        {
            DragMove();
            SavePlacement(isOpen: true);
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (Cursor == Cursors.None)
            Cursor = null;
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsFullScreen)
            SetFullScreen(false);
        else if (e.Key == Key.F11)
            SetFullScreen(!IsFullScreen);
    }

    private void FullScreenMenuItem_Click(object sender, RoutedEventArgs e) => SetFullScreen(!IsFullScreen);

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => Close();
}
