using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SpigaDashboards.App.Licensing;

namespace SpigaLicenseManager;

public partial class MainWindow : Window
{
    private sealed record UsbStick(string Root, string Label, UsbDriveIdentity Identity)
    {
        public string Display => $"{Root}  {Label}  —  {Identity.Model}  —  série {Identity.Serial ?? "(aucune)"}";
    }

    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush OkBack = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush BadBack = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x72));

    private readonly ManagerSettings _settings = ManagerSettings.Load();
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private LicenseIssuer _issuer;
    private bool _scanning;

    public MainWindow()
    {
        InitializeComponent();
        _issuer = new LicenseIssuer(_settings.KeysDir);
        ShowKeyState();
        ShowEntries();
        ShowStickState();

        // Sticks plugged or pulled show up without a refresh button.
        _scanTimer.Tick += async (_, _) => await ScanAsync();
        _scanTimer.Start();
        Loaded += async (_, _) => await ScanAsync();
    }

    private UsbStick? SelectedStick => DriveCombo.SelectedItem as UsbStick;

    private void ShowKeyState()
    {
        KeysDirText.Text = _issuer.KeysDir;
        var problem = _issuer.KeyProblem();
        KeyStatusText.Text = problem == null ? "✔ Clé privée SPIGA prête" : $"✖ {problem}";
        KeyStatusText.Foreground = problem == null ? Ok : Bad;
        UpdateButtons();
    }

    private void ShowEntries() => EntriesList.ItemsSource = _issuer.Entries().Reverse().ToList();

    private void UpdateButtons()
    {
        VerifyButton.IsEnabled = SelectedStick != null;
        IssueButton.IsEnabled = SelectedStick?.Identity.Serial != null && _issuer.KeyProblem() == null;
    }

    private async Task ScanAsync()
    {
        if (_scanning)
            return;
        _scanning = true;
        try
        {
            // Off the UI thread: a card reader or a slow stick must not freeze the window.
            var sticks = await Task.Run(FindUsbSticks);
            var current = DriveCombo.Items.Cast<UsbStick>().ToList();
            if (sticks.SequenceEqual(current))
                return;

            var selectedRoot = SelectedStick?.Root;
            DriveCombo.ItemsSource = sticks;
            DriveCombo.SelectedItem = sticks.FirstOrDefault(s => s.Root == selectedRoot) ?? sticks.FirstOrDefault();
            if (sticks.Count == 0)
                ShowStickState();
        }
        finally
        {
            _scanning = false;
        }
    }

    private static List<UsbStick> FindUsbSticks()
    {
        var sticks = new List<UsbStick>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed))
                continue;
            try
            {
                var root = drive.RootDirectory.FullName;
                if (drive.IsReady && UsbDriveIdentity.Read(root) is { IsUsb: true } identity)
                    sticks.Add(new UsbStick(root, drive.VolumeLabel, identity));
            }
            catch (IOException)
            {
                // Pulled out mid-scan.
            }
        }
        return sticks;
    }

    private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ResultBorder.Visibility = Visibility.Collapsed;
        ShowStickState();
    }

    private void ShowStickState()
    {
        UpdateButtons();
        if (SelectedStick is not { } stick)
        {
            DriveStateText.Text = "Branchez une clé USB…";
            DriveStateText.Foreground = Neutral;
            IdBox.Text = "";
            return;
        }

        IdBox.Text = _issuer.NextId(stick.Identity.Serial);
        if (stick.Identity.Serial == null)
        {
            DriveStateText.Text = "Cette clé ne donne pas de numéro de série : elle ne peut pas servir de licence.";
            DriveStateText.Foreground = Bad;
        }
        else if (DongleLicense.Check(stick.Root, out var reason) is { } license)
        {
            DriveStateText.Text = $"Licence {license.Id} pour « {license.Client} » (émise le {license.Issued}) : valide";
            DriveStateText.Foreground = Ok;
            ClientBox.Text = license.Client;
            IdBox.Text = license.Id;
        }
        else if (!File.Exists(Path.Combine(stick.Root, DongleLicense.FileName)))
        {
            DriveStateText.Text = "Vierge : pas encore de licence";
            DriveStateText.Foreground = Neutral;
        }
        else
        {
            DriveStateText.Text = $"Licence invalide : {reason}";
            DriveStateText.Foreground = Bad;
        }
    }

    private void Issue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStick is not { } stick)
            return;
        var client = ClientBox.Text.Trim();
        var id = IdBox.Text.Trim();
        if (client.Length == 0 || id.Length == 0)
        {
            ShowResult(false, "Renseignez le client et l'ID.");
            return;
        }

        if (DongleLicense.Check(stick.Root, out _) is { } existing
            && MessageBox.Show(this, $"Cette clé contient déjà la licence {existing.Id} (« {existing.Client} »).\nLa remplacer ?",
                "Clé déjà préparée", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var license = _issuer.Issue(stick.Root, client, id);
            ShowResult(true, $"✔ Clé prête : {license.Id} pour « {license.Client} », vérifiée. Vous pouvez la retirer.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowResult(false, $"✖ {ex.Message}");
        }
        ShowEntries();
        ShowStickState();
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStick is not { } stick)
            return;
        ShowStickState();
        if (DongleLicense.Check(stick.Root, out var reason) is { } license)
            ShowResult(true, $"✔ Licence valide : {license.Id} pour « {license.Client} », émise le {license.Issued}. Cette clé fera fonctionner SPIGA Dashboards.");
        else
            ShowResult(false, $"✖ Licence invalide : {reason}");
    }

    private void ShowResult(bool ok, string message)
    {
        ResultText.Text = message;
        ResultText.Foreground = ok ? Ok : Bad;
        ResultBorder.Background = ok ? OkBack : BadBack;
        ResultBorder.Visibility = Visibility.Visible;
    }

    private void ChangeKeysDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Dossier contenant private.pem",
            InitialDirectory = Directory.Exists(_issuer.KeysDir) ? _issuer.KeysDir : null,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _settings.KeysDir = dialog.FolderName;
        _settings.Save();
        _issuer = new LicenseIssuer(dialog.FolderName);
        ShowKeyState();
        ShowEntries();
        ShowStickState();
    }
}
