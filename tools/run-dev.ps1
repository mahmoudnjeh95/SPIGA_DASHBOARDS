# Dev run: starts SpigaDashboards.App on the dev database and keeps the data
# live while it runs (database\live_seed.py writes a snapshot per line every
# -Cadence seconds and stops by itself when the app closes).
#
#   powershell -File tools\run-dev.ps1              # build, then run
#   powershell -File tools\run-dev.ps1 -NoBuild
#   powershell -File tools\run-dev.ps1 -Reseed      # new 30-day history first
#   powershell -File tools\run-dev.ps1 -Kiosk       # classic dashboard only
#   powershell -File tools\run-dev.ps1 -Both        # sign-in shell AND kiosk dashboard, side by side
#   powershell -File tools\run-dev.ps1 -Web         # browser host instead (http://localhost:5000)
param(
    [int]$Cadence = 60,
    [switch]$NoBuild,
    [switch]$Reseed,
    [switch]$Kiosk,
    [switch]$Both,
    [switch]$Web
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) { $python = (Get-Command py -ErrorAction Stop).Source }

if ($Reseed) {
    # The generator recreates the file: keep the tables the app added
    # (shared settings, planning), which it does not know about.
    $backup = Join-Path $env:TEMP "spiga_dashboards.before-reseed.db"
    Copy-Item database\spiga_dashboards.db $backup -Force
    & $python database\generate_seed_data.py --synthetic 30
    & $python -c @"
import sqlite3, sys
c = sqlite3.connect(r'database\spiga_dashboards.db'); c.execute('attach ? as old', (sys.argv[1],))
for name, sql in c.execute("select name, sql from old.sqlite_master where type='table' and name not in (select name from main.sqlite_master) and name not like 'sqlite_%'").fetchall():
    c.execute(sql); c.execute(f'insert into main.[{name}] select * from old.[{name}]')
    for (s,) in c.execute("select sql from old.sqlite_master where tbl_name=? and type in ('index','trigger') and sql is not null", (name,)).fetchall(): c.execute(s)
c.commit()
"@ $backup
}

if ($Web) {
    if (-not $NoBuild) { dotnet build src\Spiga.Web | Out-Host }
    # Development: the stylesheets and scripts of Spiga.UI are served from its
    # wwwroot (without it _content/Spiga.UI/* answer 404 and the browser keeps
    # an old cached copy).
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $app = Start-Process dotnet -ArgumentList 'run', '--no-build', '--project', 'src\Spiga.Web', '--urls', 'http://localhost:5000' -PassThru
    Start-Process 'http://localhost:5000'
} else {
    if (-not $NoBuild) { dotnet build SpigaDashboards.sln -c Debug | Select-String 'error|Build succeeded' | Out-Host }
    $exe = Join-Path $root 'src\SpigaDashboards.App\bin\Debug\net8.0-windows\SpigaDashboards.App.exe'
    $apps = @()
    # Without --kiosk the app still opens the kiosk on a PC installed as a
    # screen (registry KioskMode=1): set it to 0 on a dev PC to get the shell.
    if (-not $Kiosk) { $apps += Start-Process $exe -WorkingDirectory (Split-Path $exe) -PassThru }
    if ($Kiosk -or $Both) { $apps += Start-Process $exe -ArgumentList '--kiosk' -WorkingDirectory (Split-Path $exe) -PassThru }
}
if ($Web) { $apps = @($app) }

$ids = $apps | ForEach-Object { $_.Id }
Write-Host "App PID(s) $($ids -join ', '): live data every $Cadence s until they close (Ctrl+C stops the data only)."
& $python database\live_seed.py --cadence $Cadence --while-pid @ids
