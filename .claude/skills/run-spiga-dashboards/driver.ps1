<#
.SYNOPSIS
  Build/launch/drive/screenshot SpigaDashboards.App (WPF desktop kiosk app) via UI Automation.

.DESCRIPTION
  Each invocation is a fresh, stateless PowerShell process: it locates the already-running
  app (or launches a new one) by process name, then drives it through UI Automation
  (System.Windows.Automation). There is no persistent REPL - re-run this script per action.

.PARAMETER Action
  build       - dotnet build the solution (Debug).
  launch      - start the built exe (no-op if already running); prints PID.
  status      - list PID + top-level window name/rect if running.
  screenshot  - save a PNG of the main (or named) window to -OutFile.
  listelements- dump Name/ControlType of every automation element in the window (debug aid).
  click       - invoke a Button (or any element supporting InvokePattern) by -Name.
  settext     - set a text control's value by -Name to -Text (falls back to click+SendKeys).
  gettext     - print the Name.Value / text content of the element named -Name.
  select      - select a filter-bar button by -Name: a period ("Aujourd'hui", "Poste prec.",
                "Hier", "Semaine", "Mois", "3 mois", "Dates..."), a sector or a poste.
  close       - gracefully close the app (WindowPattern.Close on the main window).
  kill        - Stop-Process the app (last resort).

.EXAMPLE
  powershell -File driver.ps1 -Action build
  powershell -File driver.ps1 -Action launch
  powershell -File driver.ps1 -Action screenshot -OutFile C:\tmp\main.png
  powershell -File driver.ps1 -Action click -Name Historique
  powershell -File driver.ps1 -Action screenshot -OutFile C:\tmp\history.png
  powershell -File driver.ps1 -Action click -Name Fermer
  powershell -File driver.ps1 -Action close
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('build', 'launch', 'status', 'screenshot', 'listelements', 'click', 'settext', 'gettext', 'select', 'close', 'kill')]
    [string]$Action,

    [string]$Name,
    [string]$Text,
    [string]$OutFile,
    [string]$WindowName = "SPIGA Dashboards",
    [int]$TimeoutMs = 10000
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$ProjDir    = Join-Path $RepoRoot 'src\SpigaDashboards.App'
$SlnPath    = Join-Path $RepoRoot 'SpigaDashboards.sln'
$ExePath    = Join-Path $ProjDir 'bin\Debug\net8.0-windows\SpigaDashboards.App.exe'
$ProcName   = 'SpigaDashboards.App'

function Add-UiaAssemblies {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
}

function Get-AppProcess {
    Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Select-Object -First 1
}

# Root automation element for a top-level window, found by exact Name among Desktop's children
# (owned dialogs like HistoryWindow/SettingsWindow nest *under* this in the Descendants tree,
# so -WindowName only ever needs to be the top-level "SPIGA Dashboards" window).
function Get-AppWindowElement {
    param([string]$Title = $WindowName)
    Add-UiaAssemblies
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Title)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { throw "Top-level window '$Title' not found - is the app running? (driver.ps1 -Action launch)" }
    return $win
}

function Set-Foreground {
    param($WinElement)
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Driver2 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@ -ErrorAction SilentlyContinue
    $hwnd = [IntPtr]$WinElement.Current.NativeWindowHandle
    [Win32Driver2]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
    [Win32Driver2]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
}

# Matches on UIA Name OR AutomationId - most Buttons only have a Name (from their Content
# text), but plain TextBoxes like the sticky note (x:Name="NoteTextBox") render an EMPTY
# Name and only expose their x:Name via AutomationId. Try both so one -Name argument works
# for either kind of control.
function Find-ElementByName {
    param($Root, [string]$ElemName)
    Add-UiaAssemblies
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $ElemName)
    $idCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ElemName)
    $orCond = New-Object System.Windows.Automation.OrCondition($nameCond, $idCond)
    $el = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $orCond)
    if (-not $el) { throw "No element named/AutomationId '$ElemName' found under window '$($Root.Current.Name)'." }
    return $el
}

switch ($Action) {

    'build' {
        & dotnet build $SlnPath -c Debug
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
    }

    'launch' {
        $existing = Get-AppProcess
        if ($existing) {
            Write-Output "Already running: PID $($existing.Id)"
            return
        }
        if (-not (Test-Path $ExePath)) { throw "Exe not found at $ExePath - run -Action build first." }
        $p = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
        $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
        do {
            Start-Sleep -Milliseconds 300
            $ready = (Get-Process -Id $p.Id -ErrorAction SilentlyContinue).MainWindowTitle -eq $WindowName
        } while (-not $ready -and (Get-Date) -lt $deadline)
        if (-not $ready) { throw "App launched (PID $($p.Id)) but main window never appeared within ${TimeoutMs}ms." }
        Write-Output "Launched: PID $($p.Id)"
    }

    'status' {
        $p = Get-AppProcess
        if (-not $p) { Write-Output "Not running."; return }
        Write-Output "PID $($p.Id)  Title='$($p.MainWindowTitle)'  Responding=$($p.Responding)"
        try {
            $win = Get-AppWindowElement
            $r = $win.Current.BoundingRectangle
            Write-Output "Window rect: $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
        } catch { Write-Output "(UIA window lookup failed: $_)" }
    }

    'screenshot' {
        if (-not $OutFile) { throw "-OutFile is required for screenshot." }
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Driver {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@ -ErrorAction SilentlyContinue

        $win = Get-AppWindowElement -Title $WindowName
        $hwnd = [IntPtr]$win.Current.NativeWindowHandle
        [Win32Driver]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
        [Win32Driver]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 500

        $r = $win.Current.BoundingRectangle
        $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        $g.Dispose()
        New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
        $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Output "Saved: $OutFile"
    }

    'listelements' {
        $win = Get-AppWindowElement
        Add-UiaAssemblies
        $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                             [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            $n = $el.Current.Name
            if ([string]::IsNullOrWhiteSpace($n)) { continue }
            "{0,-16} {1}" -f $el.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $n
        }
    }

    'click' {
        if (-not $Name) { throw "-Name is required for click." }
        $win = Get-AppWindowElement
        $el = Find-ElementByName -Root $win -ElemName $Name
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$null)) {
            $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pattern.Invoke()
        } else {
            throw "Element '$Name' does not support InvokePattern (ControlType=$($el.Current.ControlType.ProgrammaticName))."
        }
        Write-Output "Clicked '$Name'"
    }

    'settext' {
        if (-not $Name) { throw "-Name is required for settext." }
        $win = Get-AppWindowElement
        $el = Find-ElementByName -Root $win -ElemName $Name
        Add-UiaAssemblies
        $valPatternObj = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valPatternObj)) {
            $valPatternObj.SetValue($Text)
        } else {
            # Fallback: focus + select-all + type
            $el.SetFocus()
            [System.Windows.Forms.SendKeys]::SendWait('^a')
            [System.Windows.Forms.SendKeys]::SendWait([System.Windows.Forms.SendKeys]::Escape)
            [System.Windows.Forms.SendKeys]::SendWait($Text)
        }
        Write-Output "Set text on '$Name'"
    }

    'gettext' {
        if (-not $Name) { throw "-Name is required for gettext." }
        $win = Get-AppWindowElement
        $el = Find-ElementByName -Root $win -ElemName $Name
        $valPatternObj = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valPatternObj)) {
            Write-Output $valPatternObj.Current.Value
        } else {
            Write-Output $el.Current.Name
        }
    }

    'select' {
        if (-not $Name) { throw "-Name is required for select." }
        # Filter bar segments (MainWindow.xaml) are RadioButtons: select one by
        # its text - a period ("Aujourd'hui", "Poste prec.", "Hier", "Semaine",
        # "Mois", "3 mois", "Dates..."), a sector ("Tous", "Couscous",
        # "Pate courte", "Pate longue") or a poste ("Matin", "Après-midi", "Nuit").
        # "Tous" exists in both the sector and the poste groups: the first one
        # (sector) is selected.
        $win = Get-AppWindowElement
        Add-UiaAssemblies
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
        $radio = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if (-not $radio) { throw "No filter-bar button named '$Name'." }
        $radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Write-Output "Selected '$Name'"
    }

    'close' {
        $win = Get-AppWindowElement
        Add-UiaAssemblies
        $winPatternObj = $null
        if ($win.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$winPatternObj)) {
            $winPatternObj.Close()
        } else {
            $p = Get-AppProcess
            if ($p) { Stop-Process -Id $p.Id -Force }
        }
        Write-Output "Close requested."
    }

    'kill' {
        $p = Get-AppProcess
        if ($p) { Stop-Process -Id $p.Id -Force; Write-Output "Killed PID $($p.Id)" }
        else { Write-Output "Not running." }
    }
}
