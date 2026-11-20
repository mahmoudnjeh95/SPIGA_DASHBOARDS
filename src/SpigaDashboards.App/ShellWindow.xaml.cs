using System.Windows;

namespace SpigaDashboards.App;

public partial class ShellWindow : Window
{
    // Width of the navigation rail of the Razor screens (.rail in spiga.css).
    private const double RailWidth = 236;

    public ShellWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
        // Using a WPF page counts as activity for the inactivity lock.
        DashboardHost.PreviewMouseDown += (_, _) => NativeActivity?.Invoke();
        DashboardHost.PreviewKeyDown += (_, _) => NativeActivity?.Invoke();
    }

    public event Action? NativeActivity;

    // A WPF page (dashboard, arrêts, planification, paramètres) beside the rail.
    public void ShowNative(FrameworkElement view)
    {
        if (!ReferenceEquals(DashboardHost.Child, view))
            DashboardHost.Child = view;
        // The slide templates bind to their window's DataContext (shared
        // with TvWindow): give it the dashboard's.
        DataContext = view is DashboardView ? view.DataContext : null;
        WebColumn.Width = new GridLength(RailWidth);
        DashboardColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardHost.Visibility = Visibility.Visible;
    }

    // The page stays loaded, hidden: it keeps its state for when it comes back.
    public void HideNative()
    {
        WebColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardColumn.Width = new GridLength(0);
        DashboardHost.Visibility = Visibility.Collapsed;
    }
}
