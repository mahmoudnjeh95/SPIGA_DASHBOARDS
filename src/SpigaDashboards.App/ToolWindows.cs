using System.Windows;
using System.Windows.Media.Imaging;
using SpigaDashboards.App.Services;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App;

// The dashboard's dialogs in kiosk mode (KioskWindow): the same views the
// main window of SPIGA Suivi Production shows beside its menu.

public sealed class StopsWindow : Window
{
    public StopsWindow(StopsViewModel viewModel)
    {
        ToolWindow.Setup(this, "Arrêts de production", 1200, 800);
        var view = new StopsView(viewModel);
        view.CloseRequested += Close;
        Content = view;
        WindowSizing.FitToWorkArea(this);
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ToolWindow.Setup(this, "Paramètres", 780, 720);
        ResizeMode = ResizeMode.CanResize;
        var view = new SettingsView(viewModel);
        view.CloseRequested += Close;
        Content = view;
        WindowSizing.FitToWorkArea(this);
    }
}

internal static class ToolWindow
{
    public static void Setup(Window window, string title, double width, double height)
    {
        window.Title = title;
        window.Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/spiga-logo.png"));
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Language = System.Windows.Markup.XmlLanguage.GetLanguage("fr-FR");
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
    }
}
