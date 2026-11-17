using System.Windows;

namespace SpigaDashboards.App;

public partial class ShellWindow : Window
{
    public ShellWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
    }
}
