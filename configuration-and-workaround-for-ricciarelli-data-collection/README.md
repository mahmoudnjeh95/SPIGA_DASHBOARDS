# Ricciarelli data collection — configuration and workaround

The two Ricciarelli CPM checkweighers of the **Pâte** sector are not on StatsViewer.
Two collector modes, run by scheduled tasks on the server (192.168.2.29), read them and
write the same `produzione` / `lotto` rows StatsViewer writes for the other lines, so both
weighers appear on the dashboard like every other line.

| Line | Panel | Panel software | How we read it | Guide |
|---|---|---|---|---|
| **RIC1** | 192.168.2.53 (Windows XP, ASEM) | `HMI_Ricciarelli_CP` | TCP 4660 telegram (the panel's own data server) | [RIC1-telegram.md](RIC1-telegram.md) |
| **RIC2** | 192.168.2.54 (Windows 10, ASEM) | `ControlTerm` V. 800r12 | **Workaround:** reads ControlTerm's message log over the network | [RIC2-controlterm-log.md](RIC2-controlterm-log.md) |

The PC01 collector (Stiavelli PC01, 192.168.2.35) is a different collector, documented in
[`tools/pc01-bridge/README.md`](../tools/pc01-bridge/README.md).

## Nothing runs on the panels

Both collectors run **on the server only**. Nothing is installed or changed on the
panels; the Ricciarelli software, the weigher head and the serial ports are untouched.

```
RIC1  .53  HMI_Ricciarelli_CP ──TCP 4660 telegram──┐
                                                   ├─► server task (every minute)
RIC2  .54  ControlTerm ──writes── MessageLog.txt ──┘    ricciarelli_collector.ps1
            (read over the "Ricciareli" share)                 │
                                                               ▼
                                         SQL Server .\SQLEXPRESS, database "stats"
                                         produzione + lotto  ──►  dashboard (Pâte sector)
```

## Files

| File | Role |
|---|---|
| [`tools/pc01-bridge/ricciarelli_collector.ps1`](../tools/pc01-bridge/ricciarelli_collector.ps1) | The collector (both modes: `-Source Telegram` for RIC1, `-Source Log` for RIC2) |
| [`tools/pc01-bridge/install_ricciarelli_collector.ps1`](../tools/pc01-bridge/install_ricciarelli_collector.ps1) | Deploys the collector and registers both scheduled tasks |
| `tools/server-setup/SpigaServerSetup` | Run by the setup .exe in **Serveur** mode: checks the tasks and offers to repair them |

The scripts live in `tools/pc01-bridge` (next to the PC01 collector) because the installer
and SpigaServerSetup ship them from there.

## On the server

| What | Where |
|---|---|
| Deployed script | `C:\ProgramData\SpigaRicciarelliCollector\ricciarelli_collector.ps1` |
| Scheduled tasks | `SPIGA - Ricciarelli RIC1 Collector`, `SPIGA - Ricciarelli RIC2 Collector` (Administrateur, S4U, highest privileges, at boot + every minute) |
| Logs | `C:\StatsLog\Ricciarelli_RIC1_collector.log`, `C:\StatsLog\Ricciarelli_RIC2_collector.log` |
| State (production / lot tracking) | `C:\StatsLog\Ricciarelli_RIC1_state.json`, `C:\StatsLog\Ricciarelli_RIC2_state.json` |
| RIC2 panel account (encrypted) | `C:\ProgramData\SpigaRicciarelliCollector\Ricciarelli_RIC2.cred` |

## Install or reinstall (both lines)

From an **elevated** PowerShell in `tools\pc01-bridge`:

```powershell
# First install, or when the RIC2 panel password changed:
powershell -NoProfile -ExecutionPolicy Bypass -File install_ricciarelli_collector.ps1 -Ric2User spiga -Ric2Password '<panel password>'

# Reinstall / update the script only (the stored RIC2 account is kept):
powershell -NoProfile -ExecutionPolicy Bypass -File install_ricciarelli_collector.ps1

# Remove both tasks (deployed script, account and logs are left in place):
powershell -NoProfile -ExecutionPolicy Bypass -File install_ricciarelli_collector.ps1 -Uninstall
```

The setup .exe in **Serveur** mode does the same check-and-repair through SpigaServerSetup.

## Check that it works

```powershell
Get-ScheduledTask 'SPIGA - Ricciarelli*' | Get-ScheduledTaskInfo | Select TaskName, LastRunTime, LastTaskResult
Get-Content C:\StatsLog\Ricciarelli_RIC1_collector.log -Tail 3
Get-Content C:\StatsLog\Ricciarelli_RIC2_collector.log -Tail 3
```

Healthy: a block every minute ending in `produzione=INSERT lotto=...`. A `[WARN]` or
`[ERROR]` line says what is wrong (see each guide's troubleshooting table).

## Common to both modes

- **Dry run is the default.** Without `-Write` the collector prints what it would write and
  touches no table (a dry run keeps its own `..._state.dryrun.json`). The tasks run with `-Write`.
- **One row per minute.** Like StatsViewer, each tick inserts a `produzione` snapshot; the
  dashboard reads "running" from the pack count rising between snapshots, and a stop once
  it stays flat for the stop threshold (Paramètres, default 5 min).
- **IDs.** `numero_linea` = `RIC1` / `RIC2`; `codice_articolo` = 8 zeros + the article
  name; `ID_produzione` = line (4) + `codice_articolo` (20) + start `dd.MM.yyyy-HH:mm:ss`.
- **Times in the state file** are local `yyyy-MM-ddTHH:mm:ss` text, to the second. (A
  `[DateTime]` round-tripped through Windows PowerShell's JSON comes back in UTC, which
  once shifted RIC1's IDs one hour early; lot rows are found by their start time, so the
  state and SQL must agree to the second.)
- **Dashboard side.** The `Machines` table has rows `RIC1` / `RIC2` (sector `Pate`, type
  `RIC`, IPs .53 / .54). Nominal cadence and TRS targets are set in Paramètres.
