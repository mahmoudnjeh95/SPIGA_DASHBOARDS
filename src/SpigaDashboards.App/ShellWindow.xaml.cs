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
        // Using the dashboard counts as activity for the inactivity lock.
        DashboardHost.PreviewMouseDown += (_, _) => DashboardActivity?.Invoke();
        DashboardHost.PreviewKeyDown += (_, _) => DashboardActivity?.Invoke();
    }

    public event Action? DashboardActivity;

    public void ShowDashboard(DashboardView view)
    {
        if (!ReferenceEquals(DashboardHost.Child, view))
            DashboardHost.Child = view;
        WebColumn.Width = new GridLength(RailWidth);
        DashboardColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardHost.Visibility = Visibility.Visible;
    }

    // The view stays in the window, hidden: the slides keep their state.
    public void HideDashboard()
    {
        WebColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardColumn.Width = new GridLength(0);
        DashboardHost.Visibility = Visibility.Collapsed;
    }
}
