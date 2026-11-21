using System.Windows;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// The production-floor TV window (was the WPF TvWindow): the web Écran TV in
// its own web view, so its own session (ScreenAccount, never locked) and its
// own deck: filters used on the main window never show up on the TV. Its
// place and full screen are remembered, and it reopens there on the next start.
public partial class WebTvWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private bool _closingForShutdown;

    // main: the TV kiosk (--tv), the application's only window: always full
    // screen at start, and it does not change whether the TV window of the
    // other modes reopens.
    private readonly bool _main;

    public WebTvWindow(IServiceProvider services, AppSettings settings, SettingsService settingsService, bool main = false)
    {
        _main = main;
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        WebView.Services = services;
        WebView.RootComponents[0].Parameters = new Dictionary<string, object?> { ["Screen"] = "tv" };
        PlaceFromSettings();

        // The page's full screen (button or F11) fills the screen the window is on.
        WebView.BlazorWebViewInitialized += (_, e) =>
        {
            var core = e.WebView.CoreWebView2;
            core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        };
        // Maximize only once the window exists at its saved place, so it fills
        // that screen (the TV) and not the primary one.
        SourceInitialized += (_, _) => { if (_main || _settings.TvScreen.FullScreen) SetFullScreen(true, save: false); };
        Closing += (_, _) => SavePlacement(isOpen: _main ? _settings.TvScreen.IsOpen : _closingForShutdown);
    }

    // App closing (main window closed): keep IsOpen so the TV comes back next start.
    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    private bool IsFullScreen => WindowStyle == WindowStyle.None;

    private void SetFullScreen(bool fullScreen, bool save = true)
    {
        if (fullScreen == IsFullScreen)
            return;
        // Topmost: nothing else pops up over the dashboard on the TV.
        WindowState = WindowState.Normal;
        WindowStyle = fullScreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = fullScreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowState = fullScreen ? WindowState.Maximized : WindowState.Normal;
        Topmost = fullScreen;
        if (save)
            SavePlacement(isOpen: _main ? _settings.TvScreen.IsOpen : true);
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

    private static bool IsOnVirtualScreen(double left, double top)
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
            AppLogger.Log($"Écran TV : position non enregistrée ({ex.Message})");
        }
    }
}
