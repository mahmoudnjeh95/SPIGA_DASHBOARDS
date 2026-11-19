<#
  install_pc01_collector.ps1  --  deploy + register the PC01 collector as an
  auto-starting scheduled task, modeled exactly on the existing "SpigaStatusCollector"
  task (Administrateur / S4U / Highest, BootTrigger + 1-minute repetition, hidden,
  one -Once poll per tick). Survives reboot. Re-runnable (idempotent).

  Also points StatsViewer's PC01 node at a dead IP so StatsViewer never reclaims
  PC01's single 5001 client on restart (reversible: see -Uninstall).

  Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File install_pc01_collector.ps1
          powershell ... install_pc01_collector.ps1 -Uninstall
#>
param([switch]$Uninstall)
$ErrorActionPreference='Stop'
$TaskName   = 'SPIGA - PC01 Collector'
$DeployDir  = 'C:\ProgramData\SpigaPC01Collector'
$DeployPs1  = Join-Path $DeployDir 'pc01_collector.ps1'
$SrcPs1     = Join-Path $PSScriptRoot 'pc01_collector.ps1'
$RegPC01    = 'HKCU:\Software\Stiavelli\StatsViewer\Settings\PC01'
$RealIpDword= 3232236067           # 192.168.2.35 (original)
$DeadIpDword= 2130706433           # 127.0.0.1    (so StatsViewer can't grab real PC01)

if($Uninstall){
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  if(Test-Path $RegPC01){ Set-ItemProperty $RegPC01 -Name IPAddress -Value $RealIpDword -Type DWord }
  "Uninstalled: task removed, StatsViewer PC01 IP restored to 192.168.2.35. (Re-enable PC01 in StatsViewer to resume.)"
  return
}

# 1) deploy script to stable location
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
Copy-Item $SrcPs1 $DeployPs1 -Force
"deployed -> $DeployPs1"

# 2) stop StatsViewer from reclaiming PC01's port on its next restart (dead IP)
if(Test-Path $RegPC01){
  $cur = (Get-ItemProperty $RegPC01).IPAddress
  Set-ItemProperty $RegPC01 -Name IPAddress -Value $DeadIpDword -Type DWord
  "StatsViewer PC01 IP: $cur -> $DeadIpDword (127.0.0.1); real PC01 now owned by the collector. Reversible via -Uninstall."
}

# 3) register the scheduled task (mirror of SpigaStatusCollector)
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
  -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$DeployPs1`" -Once"
$tBoot = New-ScheduledTaskTrigger -AtStartup
$tNow  = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)
# repeat every 1 minute, effectively forever, on both triggers
$rep = (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration ([TimeSpan]::FromDays(3650))).Repetition
$tBoot.Repetition = $rep
$tNow.Repetition  = $rep
$principal = New-ScheduledTaskPrincipal -UserId 'Administrateur' -LogonType S4U -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
  -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($tBoot,$tNow) -Principal $principal -Settings $settings `
  -Description 'Reads PC01 checkweigher (192.168.2.35) port-5001 supervisor and writes produzione/lotto rows, since its reflashed firmware cannot feed StatsViewer. See tools/pc01-bridge.' | Out-Null
"registered task: $TaskName (boot + every 1 min, Administrateur/S4U/Highest, hidden)"
Start-ScheduledTask -TaskName $TaskName
"started task now."
