<#
  install_ricciarelli_collector.ps1 -- deploy + register the Ricciarelli collectors as
  auto-starting scheduled tasks, modeled exactly on the PC01 collector task
  (Administrateur / S4U / Highest, BootTrigger + 1-minute repetition, hidden, one -Once
  poll per tick, WITH -Write). Registers one task per panel:
    SPIGA - Ricciarelli RIC1 Collector  -> 192.168.2.53  (Line RIC1)
    SPIGA - Ricciarelli RIC2 Collector  -> 192.168.2.54  (Line RIC2)
  Survives reboot. Re-runnable (idempotent). RIC2 logs "unreachable" until 4660 is opened
  on .54 -- harmless; it starts collecting automatically once the port is up.

  Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File install_ricciarelli_collector.ps1
          powershell ... install_ricciarelli_collector.ps1 -Uninstall
#>
param([switch]$Uninstall)
$ErrorActionPreference='Stop'
$DeployDir = 'C:\ProgramData\SpigaRicciarelliCollector'
$DeployPs1 = Join-Path $DeployDir 'ricciarelli_collector.ps1'
$SrcPs1    = Join-Path $PSScriptRoot 'ricciarelli_collector.ps1'
$Panels = @(
  @{ Task='SPIGA - Ricciarelli RIC1 Collector'; Ip='192.168.2.53'; Line='RIC1' },
  @{ Task='SPIGA - Ricciarelli RIC2 Collector'; Ip='192.168.2.54'; Line='RIC2' }
)

if($Uninstall){
  foreach($p in $Panels){ Unregister-ScheduledTask -TaskName $p.Task -Confirm:$false -ErrorAction SilentlyContinue }
  "Uninstalled: RIC1 + RIC2 collector tasks removed. (Deployed script + logs left in $DeployDir.)"
  return
}

# 1) deploy the script to a stable location
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
Copy-Item $SrcPs1 $DeployPs1 -Force
"deployed -> $DeployPs1"

# 2) register one scheduled task per panel (mirror of SPIGA - PC01 Collector)
foreach($p in $Panels){
  Unregister-ScheduledTask -TaskName $p.Task -Confirm:$false -ErrorAction SilentlyContinue
  $action = New-ScheduledTaskAction -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
    -Argument ("-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$DeployPs1`" -Once -Write -Ip {0} -Line {1}" -f $p.Ip,$p.Line)
  $tBoot = New-ScheduledTaskTrigger -AtStartup
  $tNow  = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)
  $rep = (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration ([TimeSpan]::FromDays(3650))).Repetition
  $tBoot.Repetition = $rep; $tNow.Repetition = $rep
  $principal = New-ScheduledTaskPrincipal -UserId 'Administrateur' -LogonType S4U -RunLevel Highest
  $settings  = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName $p.Task -Action $action -Trigger @($tBoot,$tNow) -Principal $principal -Settings $settings `
    -Description ("Reads Ricciarelli CPM checkweigher {0} (TCP 4660) and writes produzione/lotto rows for line {1}. See tools/pc01-bridge/ricciarelli_collector.ps1." -f $p.Ip,$p.Line) | Out-Null
  Start-ScheduledTask -TaskName $p.Task
  "registered + started: $($p.Task)  ($($p.Ip) -> $($p.Line))"
}
"done. logs: C:\StatsLog\Ricciarelli_RIC1_collector.log / Ricciarelli_RIC2_collector.log"
