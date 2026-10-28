using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // <Version> in the csproj, kept in step with the installer's.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
        RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription}";
        SettingsFolderText.Text = SettingsService.SettingsFolder;
        CopyrightText.Text = $"© {DateTime.Now.Year} L2I. Tous droits réservés.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
