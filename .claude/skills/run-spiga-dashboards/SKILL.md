---
name: run-spiga-dashboards
description: Build, run, screenshot, and drive SpigaDashboards.App (the WPF kiosk dashboard). Use when asked to start/launch/run the app, build it, take a screenshot of the dashboard, or click/select/type into its UI (period selector, Historique, Parametres, theme toggle, sticky note).
---

SpigaDashboards.App is a native WPF (.NET 8, `net8.0-windows`) desktop
kiosk app - a single `Window` that auto-rotates through dashboard
slides. This is a real Windows GUI app, not headless/web: build it
with `dotnet`, launch the `.exe` directly, and drive/screenshot it
with the UI-Automation PowerShell driver at
`.claude/skills/run-spiga-dashboards/driver.ps1` (no xvfb/tmux needed
on Windows - the window renders on the real desktop).

All paths below are relative to the repo root (`D:\Spiga`).

## Prerequisites

.NET 8 SDK (verified: `dotnet --version` -> `8.0.418`). Nothing else -
no `apt-get`/xvfb equivalents needed; this runs directly on Windows.

## Build

```powershell
dotnet build SpigaDashboards.sln -c Debug
```

Produces `src\SpigaDashboards.App\bin\Debug\net8.0-windows\SpigaDashboards.App.exe`.
Expect NU1701 warnings (OpenTK/SkiaSharp.Views.WPF resolved against
.NET Framework instead of net8.0-windows7.0) and one CS0618 obsolete
warning in `PdfSlideViewModel.cs` - both harmless, build still succeeds
with 0 errors.

The app needs `database\spiga_dashboards.db` (SQLite dev DB) to run -
`DatabasePathResolver` walks up from the exe's directory looking for
`database\spiga_dashboards.db`, so it's found automatically as long as
the built exe stays under the repo tree. It's already checked into
this repo; if it's ever missing, regenerate it with
`database\generate_seed_data.py`.

## Run (agent path)

The driver is a **stateless** PowerShell script - every invocation is
a fresh process that re-locates the running app by process name via
UI Automation (`System.Windows.Automation`). There's no REPL/session
to keep alive; just call it once per action.

```powershell
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action build
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action launch
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action status
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action screenshot -OutFile C:\tmp\shots\main.png
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action click -Name Historique
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action screenshot -OutFile C:\tmp\shots\history.png
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action click -Name Fermer
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action select -Name "Semaine"
powershell -File .claude\skills\run-spiga-dashboards\driver.ps1 -Action gettext -Name NoteTextBox
```

| Action | What it does |
|---|---|
| `build` | `dotnet build SpigaDashboards.sln -c Debug` |
| `launch` | Starts the built exe (no-op + prints PID if already running) |
| `status` | Prints PID, title, responsiveness, main-window rect |
| `screenshot -OutFile <path>` | Restores+foregrounds the main window, saves a PNG crop of it |
| `listelements` | Dumps every named UI Automation element (Name + ControlType) - use this first when you don't know a control's exact name |
| `click -Name <text>` | Invokes a Button by its displayed text (e.g. `Historique`, `Parametres`, `Fermer`, `Annuler`, `Mode clair`/`Mode sombre`) |
| `select -Name <button>` | Selects a filter-bar button: period (`Aujourd'hui` / `Poste prec.` / `Hier` / `Semaine` / `Mois` / `3 mois` / `Dates...`), sector (`Couscous`...) or poste (`Matin`...) |
| `settext -Name <name> -Text <value>` | Sets a text control's value (e.g. `NoteTextBox`) |
| `gettext -Name <name>` | Reads a text control's current value |
| `close` | `WindowPattern.Close()` on the main window (graceful) |
| `kill` | `Stop-Process` on the app (last resort) |

Screenshots verified against real rendered output for: the main
dashboard (machine-pair and annual-summary slides), the Historique
modal (with real query results), and the Parametres modal (equipe
roster + rotation settings), in both dark and light theme.

## Run (human path)

Double-click the built exe, or `dotnet run --project src\SpigaDashboards.App`
from the repo root. `WindowState="Maximized"` at startup; close via the
window's own `X` button. No meaningful difference from the agent path
other than not being scriptable.

## Gotchas

- **This is a real, shared Windows desktop, not an isolated container.**
  If a person is at the machine, they can click the same live window
  your driver is scripting, concurrently. While testing this driver, a
  "Fermer" click that should have returned to the clean main dashboard
  instead screenshotted a freshly-reopened Historique dialog with reset
  filters - the actual user had clicked `Historique` again in the few
  hundred ms between the driver's click and its next screenshot. Don't
  assume the UI state between two driver calls is exactly what your
  last action produced - re-check with `status`/`screenshot` before
  chaining actions that depend on prior state.
- **`FindFirst`/`FindAll` from the true Desktop root can match unrelated
  shell UI**, not just this app. Searching `AutomationElement.RootElement`
  for `Name == "Historique"` returned 3 hits even though only one (or
  zero) real dialog existed - the extras were other top-level elements
  (e.g. taskbar/Task-View entries) that happened to share the name and
  only supported `InvokePattern`, not `WindowPattern`. Always scope
  searches under the app's own window element first (`Get-AppWindowElement`
  in the driver does this) rather than searching from the root.
- **The period, sector and poste filters are RadioButtons** in the filter bar
  under the top bar (no ComboBox any more): `select` uses their
  `SelectionItemPattern`. The machine list and the "Dates..." pickers are
  WPF Popups, i.e. separate windows: a window capture (PrintWindow) doesn't
  show them, and their checkboxes aren't under the main window element.
- **Two buttons are both literally named "Enregistrer"**: the sticky-note
  save button in `MainWindow.xaml` and the Parametres dialog's save
  button in `SettingsWindow.xaml`. Neither has an `x:Name`/`AutomationId`
  to disambiguate. `click -Name Enregistrer` will hit whichever
  `FindFirst` visits first - if both are visible (Parametres open over
  the main window) and you need a specific one, close the other context
  first or use `listelements` to confirm only one instance is live.
- **Plain `TextBox`/`Edit` controls (like the sticky note) expose an
  empty UIA `Name`** - their text isn't in `Name`, and `Name`-only
  lookups fail silently (`listelements` even filters them out since it
  skips blank names). Their `x:Name` in XAML *does* show up as
  `AutomationId` though (confirmed: `NoteTextBox` has `Name=''` but
  `AutomationId='NoteTextBox'`). `Find-ElementByName` in the driver
  matches on Name **or** AutomationId for exactly this reason - use the
  XAML `x:Name` for text controls, the visible label for buttons.
- **`AutomationElement.SetFocus()` throws `"Operation is not valid due
  to the current state of the object"`** if the target window isn't
  foreground (e.g. right after another action left a different window
  on top, or the app window is behind your terminal). The driver's
  `select` action calls `Set-Foreground` (restore + `SetForegroundWindow`)
  before `SetFocus()` for this reason; do the same in any new action
  that needs real keyboard focus rather than just `InvokePattern`.

## Troubleshooting

- **`Top-level window 'SPIGA Dashboards' not found`**: app isn't
  running (or hasn't finished showing its window yet right after
  `launch`). Run `-Action launch` and wait, or check `-Action status`.
- **`No element named/AutomationId 'X' found under window ...`**: the
  control either isn't currently visible (e.g. it's inside a dialog
  you haven't opened yet) or its text doesn't match exactly (French
  labels, accents/apostrophes matter - e.g. `"Aujourd'hui"`). Run
  `-Action listelements` to see exactly what's currently in the tree.
