<#
.SYNOPSIS
  Deploys a published Spiga.Web package to its IIS folder, with automatic rollback.

.DESCRIPTION
  Run by the GitHub Actions self-hosted runner on the plant server (see
  .github/workflows/web-ci-cd.yml); can also be run by hand with the same
  parameters, e.g. to redeploy a backup:

    .\Deploy-SpigaWeb.ps1 -PackagePath C:\inetpub\SpigaWeb-releases\20261126-101500 `
        -SitePath C:\inetpub\SpigaWeb -AppPool SpigaWeb -HealthUrl http://localhost/

  1. Copies the live site to <SitePath>-releases\<timestamp> (last -KeepReleases kept).
  2. Takes the site offline with app_offline.htm (ASP.NET Core Module unloads the app).
  3. Mirrors the package into the site folder. Never touched: the server's own
     configuration and data (appsettings.Production.json, *.db, logs, App_Data).
     appsettings.Production.json holds the 'stats' password and is readable only by
     SYSTEM, Administrators and the app pool: it is excluded from every copy,
     including the backups in <SitePath>-releases, so it never leaves the site folder.
  4. "Spiga.Web.dll --update-database" with the admin connection in $env:SPIGA_ADMIN_SQL
     (skipped when empty). Required on this server: the site's own login cannot
     apply table changes, so the site would fail to start on a pending migration.
  5. Brings the site back, recycles the app pool and checks HealthUrl.
     On any failure the previous release is put back and the script fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [Parameter(Mandatory)] [string] $SitePath,
    [Parameter(Mandatory)] [string] $AppPool,
    [Parameter(Mandatory)] [string] $HealthUrl,
    # The site is served over plain HTTP (internal network, no certificate to install
    # on the users' PCs). Only for an https:// HealthUrl on a self-signed certificate:
    # the health check then accepts exactly this certificate and nothing else.
    [string] $CertThumbprint = '',
    [int] $KeepReleases = 5,
    [int] $HealthTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

# Files and folders that belong to the server, not to the build.
$KeepFiles = @('appsettings.Production.json', 'app_offline.htm', '*.db', '*.db-wal', '*.db-shm', '*.lic')
$KeepDirs  = @('logs', 'App_Data', 'database')

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Copy-Mirror([string] $from, [string] $to) {
    # /MIR deletes what is not in the source, except the excluded files and folders.
    $rcArgs = @($from, $to, '/MIR', '/R:3', '/W:2', '/NP', '/NFL', '/NDL', '/XF') + $KeepFiles + @('/XD') + $KeepDirs
    & robocopy @rcArgs | Out-Host
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to failed (exit $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}

function Set-Offline([bool] $offline) {
    $marker = Join-Path $SitePath 'app_offline.htm'
    if ($offline) {
        '<!doctype html><meta charset="utf-8"><title>Mise &agrave; jour</title><p style="font-family:sans-serif">SPIGA Suivi Production est en cours de mise &agrave; jour. Merci de réessayer dans une minute.</p>' |
            Set-Content -Path $marker -Encoding UTF8
        Start-Sleep -Seconds 5   # let the module shut the app down and release the DLLs
    } elseif (Test-Path $marker) {
        Remove-Item $marker -Force
    }
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if ($CertThumbprint) {
    if (-not ('SpigaCertPin' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class SpigaCertPin {
    public static string Thumbprint;
    public static void Enable() {
        ServicePointManager.ServerCertificateValidationCallback =
            (sender, cert, chain, errors) => errors == SslPolicyErrors.None ||
                (cert != null && string.Equals(new X509Certificate2(cert).Thumbprint, Thumbprint, System.StringComparison.OrdinalIgnoreCase));
    }
}
'@
    }
    [SpigaCertPin]::Thumbprint = $CertThumbprint.Replace(' ', '')
    [SpigaCertPin]::Enable()
}

function Test-Health {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 15
            if ($r.StatusCode -eq 200) { return $true }
        } catch { Write-Host "   not ready: $($_.Exception.Message)" }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Restart-Pool {
    if ((Get-WebAppPoolState -Name $AppPool).Value -eq 'Started') { Restart-WebAppPool -Name $AppPool }
    else { Start-WebAppPool -Name $AppPool }
}

if (-not (Test-Path (Join-Path $PackagePath 'Spiga.Web.dll'))) { throw "No Spiga.Web.dll in $PackagePath" }
if (-not (Test-Path "IIS:\AppPools\$AppPool")) { throw "IIS app pool '$AppPool' does not exist" }
if (Test-Path (Join-Path $PackagePath 'version.txt')) { Get-Content (Join-Path $PackagePath 'version.txt') | ForEach-Object { Write-Host "   $_" } }

New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
$releases = "$($SitePath.TrimEnd('\'))-releases"
$backup = $null

# 1. Backup of what is live now
if (Test-Path (Join-Path $SitePath 'Spiga.Web.dll')) {
    $backup = Join-Path $releases (Get-Date -Format 'yyyyMMdd-HHmmss')
    Write-Step "Backup of the live site to $backup"
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    Copy-Mirror $SitePath $backup
}

try {
    Write-Step 'Site offline'
    Set-Offline $true

    Write-Step "Copy package to $SitePath"
    Copy-Mirror $PackagePath $SitePath

    if ($env:SPIGA_ADMIN_SQL) {
        Write-Step 'Database update (--update-database)'
        Push-Location $SitePath
        try {
            $env:ASPNETCORE_ENVIRONMENT = 'Production'
            $env:Spiga__AppDatabase__AdminSqlServer = $env:SPIGA_ADMIN_SQL
            $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
            if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
            & $dotnet .\Spiga.Web.dll --update-database | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "--update-database failed (exit $LASTEXITCODE)" }
        } finally {
            Remove-Item Env:\Spiga__AppDatabase__AdminSqlServer -ErrorAction SilentlyContinue
            Pop-Location
        }
    }

    Write-Step 'Site online'
    Set-Offline $false
    Restart-Pool

    Write-Step "Health check $HealthUrl"
    if (-not (Test-Health)) { throw "Health check failed after $HealthTimeoutSeconds s" }
    Write-Step 'Deployed'
}
catch {
    Write-Host "DEPLOY FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($backup) {
        Write-Step "Rolling back to $backup"
        Set-Offline $true
        Copy-Mirror $backup $SitePath
        Set-Offline $false
        Restart-Pool
        if (Test-Health) { Write-Host 'Rollback OK: previous release is live.' -ForegroundColor Yellow }
        else { Write-Host 'Rollback done but the site still does not answer. Check the IIS logs.' -ForegroundColor Red }
    } else {
        Set-Offline $false
    }
    throw
}

# Keep only the newest backups
if (Test-Path $releases) {
    Get-ChildItem $releases -Directory | Sort-Object Name -Descending | Select-Object -Skip $KeepReleases |
        Remove-Item -Recurse -Force
}
