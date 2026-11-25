# PC01 collector — reads the weigher directly, feeds the dashboard

**Live since 16/11/2026.** PC01 ("STV C PC", 192.168.2.35) was reflashed with newer
WinMachLite firmware whose port-5001 supervisor can be **read** but not **driven**:
`GETRECIPESLIST` returns `SOR__EOR` (empty), `RESETPRODCMD_` resets the socket. So
StatsViewer can connect and read PC01 but can **never start/record a production** on it,
and PC01's local production carries an all-zeros code StatsViewer won't persist. (Full
root-cause + firmware handoff: `C:\PC01_backup_20261116_SOLVED\CARDUCCI_HANDOFF.md`.)

This collector bypasses the broken parts: it reads PC01's **working** read commands
(`PRODREQ`/`LOTTOREQ`) directly, assigns a real production code, and writes the same
`produzione`/`lotto` rows StatsViewer writes for the other 9 lines — so PC01 appears on
the dashboard normally.

## How it runs
- **Scheduled task `SPIGA - PC01 Collector`** (Administrateur / S4U / Highest), modeled on
  `SpigaStatusCollector`: **BootTrigger + repeat every 1 minute**, hidden, one `-Once`
  poll per tick. Survives reboot, no login needed.
- Deployed script: `C:\ProgramData\SpigaPC01Collector\pc01_collector.ps1` (source of truth
  in this folder; `install_pc01_collector.ps1` copies it there and registers the task).
- **60-second cadence, insert-per-tick** — identical to StatsViewer (the dashboard reads
  "belt running" from the bag-count delta between snapshots, so a single updated row would
  always read as stopped). ~1438 rows/day, same as every other line.
- **Single 5001 client:** PC01 answers only one client, so StatsViewer must not poll it.
  The installer points StatsViewer's PC01 node at `127.0.0.1` (dead) so it never reclaims
  the port on restart. The 9 working lines are untouched.
- **Log:** `C:\StatsLog\PC01_collector.log`.

## Data mapping (verified 16/11/2026)
`PRODREQ → SOP_<id>_<start>_<netW>_<minW>_<maxW>_<tot>_<under>_<ok>_<over>_<std>_<avg>_…_EOP`
→ `produzione` (totale_confezioni, confezioni_ok/sotto/sopra, media_totale, peso_min/max,
peso_netto_impostato, tempo_inizio/fine). Sanity enforced each tick: `under+ok+over = tot`.
`codice_articolo` = `00000000PC01PROD    ` (assigned — the weigher reports zeros).
DB decimals are `decimal(6,1)`, clamped. Empty/placeholder lots are skipped.

`LOTTOREQ → SOL_…` (last finished lot, 50 bags ≈ 50 s on PC01) → one `lotto` row per lot,
keyed by `ID_produzione` + `ora_inizio`. SOL field 0 only toggles 0/1 on PC01, so
`progressivo_lotto` = field 3 (the running lot counter). Each tick reads the lot a second
time ~25 s later so fewer lots are missed (fixed 19/11/2026: keying on field 0 overwrote
every lot and left the Poids moyen chart empty).

## Installed by the setup .exe (since 1.5.28)
`SpigaDashboards-<version>-Setup.exe` asks **Poste personnel** or **Serveur** on its first
page. *Poste personnel* installs the app only (no admin prompt, no task). *Serveur* also runs
`tools/server-setup/SpigaServerSetup` elevated (one UAC prompt), which carries these two
scripts and checks the bridge: task present, enabled, ran in the last 5 min without error,
and the deployed script identical to this version's (line endings ignored). Healthy →
nothing changes. Otherwise it lists the problems and asks before running
`install_pc01_collector.ps1`. Uninstalling the app leaves the bridge running.
- Silent server install: `Setup.exe /quiet InstallRole=server` (the Yes/No prompt still shows)
- Check only, no change: `SpigaServerSetup.exe -check` (exit 0 = healthy, 1 = action needed)
- Log of each server-setup run: `C:\ProgramData\SpigaPC01Collector\server-setup.log`

## Operate
- Status:      `Get-ScheduledTask 'SPIGA - PC01 Collector' | Get-ScheduledTaskInfo`
- Run once:    `powershell -File C:\ProgramData\SpigaPC01Collector\pc01_collector.ps1 -Once`
- Dry-run:     add `-DryRun` (parses + prints, no DB writes)
- Reinstall:   `powershell -File install_pc01_collector.ps1`  (needs elevation for the task)
- **Remove / restore StatsViewer on PC01** (e.g. after Stiavelli fixes the firmware):
  `powershell -File install_pc01_collector.ps1 -Uninstall`  → deletes the task and restores
  PC01's StatsViewer IP to 192.168.2.35. Then re-enable PC01 in StatsViewer.

## When Stiavelli fixes the firmware
Run `-Uninstall`, re-enable PC01 in StatsViewer, and StatsViewer resumes normally. Keep
this collector only until then.
