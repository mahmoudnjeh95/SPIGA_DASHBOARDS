using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SpigaDashboards.App.Licensing;

namespace SpigaDashboards.App.Services;

// Watches for the USB licence dongle. Without it the windows show only
// "Licence non detectee"; plugging it back in unlocks them without a restart.
// Once seen, a pulled dongle (or a USB hiccup) keeps the app open for a minute
// before locking, so the floor screens don't blank on a glitch.
public sealed partial class LicenseService : ObservableObject
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(1);

    private readonly DispatcherTimer _timer;
    private long? _lastSeenTicks;
    private bool _checking;

    [ObservableProperty] private bool isLicensed;
    // Dongle missing but still within the grace minute: the warning to show.
    [ObservableProperty] private string? graceMessage;

    public LicenseService()
    {
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public DongleLicense? License { get; private set; }

    // First check on the spot, so the window opens already locked or unlocked.
    public void Start()
    {
        Apply(DongleLicense.FindPlugged());
        if (!IsLicensed)
            AppLogger.Log("Licence non detectee au demarrage");
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (_checking)
            return;
        _checking = true;
        try
        {
            // Off the UI thread: a slow drive must not freeze the dashboard.
            Apply(await Task.Run(DongleLicense.FindPlugged));
        }
        finally
        {
            _checking = false;
        }
    }

    private void Apply(DongleLicense? found)
    {
        var now = Environment.TickCount64;
        if (found != null)
        {
            if (!IsLicensed || GraceMessage != null || License?.Id != found.Id)
                AppLogger.Log($"Licence {found.Id} ({found.Client}) detectee");
            License = found;
            _lastSeenTicks = now;
            GraceMessage = null;
            IsLicensed = true;
            return;
        }

        var remaining = _lastSeenTicks is { } seen ? GracePeriod - TimeSpan.FromMilliseconds(now - seen) : TimeSpan.Zero;
        if (remaining > TimeSpan.Zero)
        {
            if (GraceMessage == null)
                AppLogger.Log("Cle de licence retiree, verrouillage dans 1 min si elle n'est pas rebranchee");
            GraceMessage = $"Clé de licence retirée : l'affichage se verrouille dans {Math.Ceiling(remaining.TotalSeconds)} s";
            return;
        }

        if (IsLicensed)
            AppLogger.Log("Licence non detectee : affichage verrouille");
        License = null;
        _lastSeenTicks = null;
        GraceMessage = null;
        IsLicensed = false;
    }
}
