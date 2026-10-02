<#
  Registers (or re-registers) the SpigaStatusCollector scheduled task: runs
  status_collector.ps1 every minute so the live dashboard has fresh machine
  status. Uses S4U logon (no stored password, no console window on the kiosk
  desktop). Run from an elevated PowerShell. Use -Uninstall to remove it.
#>
param(
    [switch]$Uninstall,
    [string]$TaskName = "SpigaStatusCollector"
)

$ErrorActionPreference = "Stop"

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed scheduled task '$TaskName'."
    return
}

$script = Join-Path $PSScriptRoot "status_collector.ps1"
if (-not (Test-Path $script)) { throw "status_collector.ps1 not found next to this installer." }

$psExe  = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$action = New-ScheduledTaskAction -Execute $psExe `
            -Argument ("-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"{0}`"" -f $script)

# Fire every minute, indefinitely, and again after a reboot.
$repeat  = (New-ScheduledTaskTrigger -Once -At (Get-Date) `
             -RepetitionInterval (New-TimeSpan -Minutes 1) `
             -RepetitionDuration (New-TimeSpan -Days 3650)).Repetition
$tNow         = New-ScheduledTaskTrigger -Once -At (Get-Date)
$tNow.Repetition = $repeat
$tBoot        = New-ScheduledTaskTrigger -AtStartup
$tBoot.Repetition = $repeat

# S4U: run as the current user without a stored password and without an
# interactive desktop session, so nothing flashes on the kiosk screen.
$principal = New-ScheduledTaskPrincipal -UserId ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME) `
                -LogonType S4U -RunLevel Highest

$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
                -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
                -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $tNow, $tBoot `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Registered scheduled task '$TaskName' (runs status_collector.ps1 every minute)."
