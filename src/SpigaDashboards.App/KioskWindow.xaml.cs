using System.Windows;

namespace SpigaDashboards.App;

// The kiosk window (TV / hall PCs started with --kiosk): the dashboard
// alone, as SPIGA Dashboards was before SPIGA Suivi Production.
public partial class KioskWindow : Window
{
    public KioskWindow(DashboardView dashboard)
    {
        InitializeComponent();
        Content = dashboard;
        // The slide templates bind to their window's DataContext (shared with TvWindow).
        DataContext = dashboard.DataContext;
        Closed += (_, _) => dashboard.CloseTvForShutdown();
    }
}
