@echo off
setlocal
title SPIGA server - restore Windows Update
rem ===================================================================
rem  Undoes part 1 of server-always-on.bat: Windows Update back to the
rem  Windows defaults so you can install patches. Power settings stay.
rem  After patching (and restarting), run server-always-on.bat again.
rem ===================================================================

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo Restoring Windows Update...
set "WU=HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"
reg delete "%WU%\AU" /v NoAutoUpdate /f >nul 2>&1
reg delete "%WU%\AU" /v AUOptions /f >nul 2>&1
reg delete "%WU%\AU" /v NoAutoRebootWithLoggedOnUsers /f >nul 2>&1
reg delete "%WU%" /v DoNotConnectToWindowsUpdateInternetLocations /f >nul 2>&1
reg delete "%WU%" /v ExcludeWUDriversInQualityUpdate /f >nul 2>&1

rem Original start types on this PC: wuauserv/WaaSMedicSvc manual, UsoSvc/dosvc delayed automatic
set "SVC=HKLM\SYSTEM\CurrentControlSet\Services"
sc config wuauserv start= demand >nul 2>&1 || reg add "%SVC%\wuauserv" /v Start /t REG_DWORD /d 3 /f >nul 2>&1
sc config WaaSMedicSvc start= demand >nul 2>&1 || reg add "%SVC%\WaaSMedicSvc" /v Start /t REG_DWORD /d 3 /f >nul 2>&1
sc config UsoSvc start= delayed-auto >nul 2>&1 || reg add "%SVC%\UsoSvc" /v Start /t REG_DWORD /d 2 /f >nul 2>&1
sc config dosvc start= delayed-auto >nul 2>&1 || reg add "%SVC%\dosvc" /v Start /t REG_DWORD /d 2 /f >nul 2>&1

powershell -NoProfile -Command "Get-ScheduledTask -TaskPath '\Microsoft\Windows\WindowsUpdate\','\Microsoft\Windows\UpdateOrchestrator\','\Microsoft\Windows\WaaSMedic\' -ErrorAction SilentlyContinue | ForEach-Object { try { Enable-ScheduledTask -InputObject $_ -ErrorAction Stop | Out-Null } catch {} }"

net start UsoSvc >nul 2>&1
net start wuauserv >nul 2>&1

echo.
powershell -NoProfile -Command "Get-Service wuauserv,UsoSvc,WaaSMedicSvc,dosvc | Format-Table Name,Status,StartType -AutoSize | Out-String -Width 120"
echo Windows Update is back on. Open Settings ^> Windows Update to install patches.
echo.
pause
