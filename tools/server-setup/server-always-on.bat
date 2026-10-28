@echo off
setlocal
title SPIGA server - always on
rem ===================================================================
rem  Keeps this PC (SQL Server + StatsViewer + SPIGA Dashboards) up 24/7
rem    1. Windows Update off   - no downloads, no forced restarts
rem    2. Never sleeps         - no sleep/hibernate, screen/disk/USB/NIC stay on
rem    3. Self-healing         - SQL Server restarts itself if it crashes,
rem                              PC reboots itself after a blue screen
rem  Safe to run again at any time (e.g. after a big Windows change).
rem  Undo part 1 with restore-windows-update.bat
rem ===================================================================

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator rights...
    if "%~1"=="" (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    ) else (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%~1' -Verb RunAs"
    )
    exit /b
)

echo.
echo [1/3] Turning off Windows Update...
set "WU=HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"
reg add "%WU%\AU" /v NoAutoUpdate /t REG_DWORD /d 1 /f >nul
reg add "%WU%\AU" /v AUOptions /t REG_DWORD /d 1 /f >nul
reg add "%WU%\AU" /v NoAutoRebootWithLoggedOnUsers /t REG_DWORD /d 1 /f >nul
reg add "%WU%" /v DoNotConnectToWindowsUpdateInternetLocations /t REG_DWORD /d 1 /f >nul
reg add "%WU%" /v ExcludeWUDriversInQualityUpdate /t REG_DWORD /d 1 /f >nul

rem Windows Update, Update Orchestrator, Update Medic (re-enables updates), Delivery Optimization.
rem Medic is protected on some builds; failures there are ignored, the policy above still applies.
for %%S in (wuauserv UsoSvc WaaSMedicSvc dosvc) do (
    net stop %%S /y >nul 2>&1
    sc config %%S start= disabled >nul 2>&1
    reg add "HKLM\SYSTEM\CurrentControlSet\Services\%%S" /v Start /t REG_DWORD /d 4 /f >nul 2>&1
)

rem Scheduled tasks that scan, download and reboot for updates
powershell -NoProfile -Command "Get-ScheduledTask -TaskPath '\Microsoft\Windows\WindowsUpdate\','\Microsoft\Windows\UpdateOrchestrator\','\Microsoft\Windows\WaaSMedic\' -ErrorAction SilentlyContinue | ForEach-Object { try { Disable-ScheduledTask -InputObject $_ -ErrorAction Stop | Out-Null } catch {} }"

echo.
echo [2/3] Power settings: never sleep...
rem High performance plan, then every timeout to "never"
powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1
powercfg /hibernate off >nul 2>&1
for %%T in (standby hibernate monitor disk) do (
    powercfg /change %%T-timeout-ac 0
    powercfg /change %%T-timeout-dc 0
)
rem USB selective suspend off, PCIe link power saving off, closing a lid does nothing
powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 >nul 2>&1
powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0 >nul 2>&1
powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0 >nul 2>&1
powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0 >nul 2>&1
powercfg /setactive SCHEME_CURRENT

rem No screen saver
reg add "HKCU\Control Panel\Desktop" /v ScreenSaveActive /t REG_SZ /d 0 /f >nul

rem Windows must not switch off the network card to save power (applies at next restart)
powershell -NoProfile -Command "Get-NetAdapter -Physical | ForEach-Object { try { Disable-NetAdapterPowerManagement -Name $_.Name -NoRestart -ErrorAction Stop } catch {} }"

echo.
echo [3/3] Self-healing...
rem SQL Server: restart 1 minute after any crash (counter resets daily)
sc failure "MSSQL$SQLEXPRESS" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul
sc config "MSSQL$SQLEXPRESS" start= auto >nul
rem Reboot automatically after a blue screen instead of waiting on it
reg add "HKLM\SYSTEM\CurrentControlSet\Control\CrashControl" /v AutoReboot /t REG_DWORD /d 1 /f >nul

echo.
echo ===================== Result =====================
powershell -NoProfile -Command "Get-Service wuauserv,UsoSvc,WaaSMedicSvc,dosvc,'MSSQL$SQLEXPRESS' | Format-Table Name,Status,StartType -AutoSize | Out-String -Width 120"
powercfg /getactivescheme
echo.
echo Done. No restart needed.
echo Reminder: in the BIOS set "Restore on AC power loss" = Power On,
echo so the PC starts again by itself after a power cut.
echo.
if /i not "%~1"=="/nopause" pause
