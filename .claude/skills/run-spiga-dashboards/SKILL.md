---
name: run-spiga-dashboards
description: Build, run, screenshot, and drive SpigaDashboards.App (the WPF kiosk dashboard). Use when asked to start/launch/run the app, build it, take a screenshot of the dashboard, or click/select/type into its UI (period selector, Historique, Parametres, theme toggle, sticky note).
model: haiku
---

SpigaDashboards.App is a native WPF (.NET 8) kiosk window on the real Windows
desktop. Drive it with the stateless UI-Automation driver
`.claude\skills\run-spiga-dashboards\driver.ps1` (one action per invocation;
it re-finds the app by process name each time). Paths are relative to the repo root.

## Build and run
- `-Action build` = `dotnet build SpigaDashboards.sln -c Debug`. NU1701/CS0618
  warnings are harmless. Build only if code changed since the last build.
- Needs `database\spiga_dashboards.db` (found by walking up from the exe;
  regenerate with `database\generate_seed_data.py` if missing).
- `-Action launch` passes `--kiosk`: the classic dashboard window opens directly
  (no-op if already running). The new sign-in/Razor screens are not reachable
  this way: run `src\Spiga.Web` and use a browser instead.

## Keep it cheap
- Chain actions in ONE PowerShell call; the chain stops at the first failure:
  `$d='.claude\skills\run-spiga-dashboards\driver.ps1'; & $d -Action click -Name Historique; & $d -Action select -Name Semaine; & $d -Action screenshot -OutFile C:\tmp\shots\h.png`
- Check state with `status` / `gettext` (text) first. Take a screenshot only when
  the user wants to see the screen or text can't answer.
- Use `listelements` only after a `click`/`select` fails with "not found".

## Actions
| Action | What it does |
|---|---|
| `build` | `dotnet build SpigaDashboards.sln -c Debug` |
| `launch` | Starts the built exe (no-op + prints PID if already running) |
| `status` | PID, title, responsiveness, main-window rect |
| `screenshot -OutFile <path>` | Foregrounds the main window, saves a PNG of it |
| `listelements` | Every named UIA element (Name + ControlType) |
| `click -Name <text>` | Invokes a button by its text (`Historique`, `Parametres`, `Fermer`, `Annuler`, `Mode clair`/`Mode sombre`) |
| `select -Name <button>` | Filter-bar radio: period (`Aujourd'hui` / `Poste prec.` / `Hier` / `Semaine` / `Mois` / `3 mois` / `Dates...`), sector (`Couscous`...) or poste (`Matin`...) |
| `settext -Name <name> -Text <value>` | Sets a text control (e.g. `NoteTextBox`) |
| `gettext -Name <name>` | Reads a text control |
| `close` | Graceful close of the main window |
| `kill` | `Stop-Process` (last resort) |

## Gotchas
- Shared live desktop: a person may click the same window between your calls.
  Re-check with `status`/`screenshot` before chaining on assumed state.
- The driver searches under the app's own window, never from the desktop root
  (taskbar items share names like "Historique"). Keep it that way in new actions.
- Filters are RadioButtons (`select`). The machine list and "Dates..." pickers
  are Popups (separate windows): not in screenshots, not under the main window.
- Two buttons are named `Enregistrer` (sticky note, Parametres). Close the other
  context first, or check with `listelements` that only one is live.
- Text boxes have an empty UIA Name: address them by their XAML `x:Name`
  (AutomationId), e.g. `NoteTextBox`. Buttons go by their visible label.
- Real keyboard focus needs the window in the foreground first (`select` does it);
  otherwise `SetFocus()` throws "Operation is not valid due to the current state".
- "Top-level window 'SPIGA Dashboards' not found": app not running yet, so `launch`
  and retry. "No element named …": not visible yet, or the text differs (accents,
  apostrophes like `Aujourd'hui`).
