# RIC2 (192.168.2.54) — workaround: ControlTerm's message log

## Why a workaround

RIC2's panel runs Ricciarelli's newer **ControlTerm** (V. 800r12, Windows 10), not the
HMI_Ricciarelli_CP of RIC1. ControlTerm has **no TCP data server**: port 4660 is closed
("refused"), and it only hands its statistics to a data concentrator on **serial COM2**
(1200 baud, 7N2), when the concentrator calls it by its control number.

Getting COM2 onto the network would need hardware (a serial-to-Ethernet converter) or a
virtual COM driver and a service on the panel. Instead, the collector uses something
ControlTerm already does: it **logs every message it receives from the weigher head
(COM3)**, with a timestamp, to

```
D:\Ricciarelli\Log\MessageLog.txt      (shared as \\192.168.2.54\Ricciareli\Ricciarelli\Log)
```

The collector on the server reads the new lines of that log every minute and counts the
packs exactly the way ControlTerm does. **Nothing is installed or changed on the panel.**

## Panel prerequisites

| What | Value | Note |
|---|---|---|
| Network port | Plant cable in the **WAN** port (192.168.2.54) | **LAN-1** is the machine's internal network (129.80.251.1/18, metal detector); the server cannot reach it |
| Windows account | `spiga` (password kept by the plant) | Used read-only to open the share |
| Share | `Ricciareli` = root of `D:` | Must stay shared |
| ControlTerm log | `D:\Ricciarelli\Log\MessageLog.txt` | Written by ControlTerm itself; rotated to `MessageLog.bak` every hour |

**Do not** connect anything to COM2 that could send byte `0x12`: two of them make
ControlTerm **reset all its statistics**. COM3 (weigher head) must stay ControlTerm's.

## Install

From an **elevated** PowerShell in `tools\pc01-bridge`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install_ricciarelli_collector.ps1 -Ric2User spiga -Ric2Password '<panel password>'
```

This deploys the script, stores the account in
`C:\ProgramData\SpigaRicciarelliCollector\Ricciarelli_RIC2.cred` (Windows DPAPI, machine
scope; the file is readable by Administrators and SYSTEM only), and registers the task.

**First run of a new installation:** the first tick replays up to the last two hours of the
log. If test packs were weighed before the real production, do the first write run by hand
with `-StartAt` so they are left out (elevated):

```powershell
$s = 'C:\ProgramData\SpigaRicciarelliCollector\ricciarelli_collector.ps1'
& $s -Ip 192.168.2.54 -Line RIC2 -Source Log -Once -Write -StartAt '2026-11-25 14:25'
```

## Scheduled task

`SPIGA - Ricciarelli RIC2 Collector` runs, every minute:

```
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden ^
  -File "C:\ProgramData\SpigaRicciarelliCollector\ricciarelli_collector.ps1" -Once -Write -Ip 192.168.2.54 -Line RIC2 -Source Log
```

## Parameters (Log mode)

| Parameter | Default | Meaning |
|---|---|---|
| `-Source Log` | (`Telegram`) | Selects this mode |
| `-Ip` / `-Line` | — | `192.168.2.54` / `RIC2` |
| `-LogShare` | `\\<Ip>\Ricciareli\Ricciarelli\Log` | Folder holding `MessageLog.txt` |
| `-CredFile` | `<script folder>\Ricciarelli_<Line>.cred` | Stored panel account |
| `-SetCredential -CredUser u -CredPassword p` | — | Store or replace the panel account, then exit |
| `-IdleHours` | `4` | No pack for this long ⇒ the next pack starts a new production |
| `-Nominal` `-Tare` `-MinOk` `-MaxOk` `-LotSize` | `400` `3` `390` `430` `100` | Recipe used until ControlTerm sends one (see below) |
| `-StartAt` | — | First run only: ignore packs before this local time |
| `-Write` | off | Without it: dry run, own state file, nothing written |

## Manual test (dry run, nothing written)

```powershell
& 'C:\ProgramData\SpigaRicciarelliCollector\ricciarelli_collector.ps1' -Ip 192.168.2.54 -Line RIC2 -Source Log -Once
```

Expected (one block per run):

```
+60 packs (panel clock +150 s)  art='400G' since=2026-11-25T14:25:43 tot=523 ok=509 under=12 over=2 belowTU1=0 mean=409,3 std=7,35 lot#6 9/100
DRYRUN completed lots: #5 n=100 mean=409,0 std=8,43
```

## What the log looks like and how it is decoded

Each entry is a timestamp line and a message line (UTF-16 text, blank line between entries):

```
25/11/2026 14:25:00 ==> Protocol ==>
RECEIVED code: 0x42, lenght: 10, bytes: 0x42-0x31-0x30-0x34-0x37-0x30-0x33-0x3E-0x3D-0x1B
```

Each data byte carries one hexadecimal digit in its low 4 bits (`0x31` → 1, `0x3E` → E).
Only two message codes are used; the many others are routine polling:

| Code | Length | Content | Sent |
|---|---|---|---|
| **0x42** | 10 | One weighed pack: net weight (4 digits, in 1/divs g), speed (3), checksum, end | Every pack |
| **0x41** | 482 | Recipe codes 1–119, 4 digits each: c1 nominal, c2 tare, c3 min, c4 max, c43 lot size, c78 decimals, c110 model | When ControlTerm starts (and on recipe change) |

Worked example (0x42 above): `1 0 4 7` = 0x1047 = 4167 → ÷10 (model 1) = 416 g → −3 g tare =
**413 g** → between 390 and 430 ⇒ **OK**.

Counting, as ControlTerm's `handle0x42`:
- g ≤ min ⇒ **under**; g ≥ max ⇒ **over**; otherwise **OK**, added to the current lot.
- A lot is complete after c43 OK packs (100), then the lot number goes up.
- Mean and std are over the OK packs; "below TU1" uses the legal TU1 (nominal − tolerable
  negative error, e.g. 400 g → 388 g).

The recipe (0x41) is only sent when ControlTerm starts, so the collector keeps the last one
in its state file and starts from the `-Nominal…-LotSize` seed until it sees one.

## Production, lots and time

- **One production per recipe.** A new one starts when the recipe changes or after
  `-IdleHours` without a pack. Its article is `<nominal>G` (e.g. `400G`).
- **Lot rows** are written per lot number: complete lots with `completo = 1`, the current
  one updated each minute.
- **Panel clock.** .54's clock runs about 2.5 minutes slow. The log is written live, so the
  collector measures the offset each tick (now − newest entry) and shifts pack times by it
  (`panel clock +150 s` in the log).
- **Hourly rotation.** ControlTerm moves `MessageLog.txt` to `MessageLog.bak` every hour.
  The collector recognizes the file by its first line, finishes the old one from `.bak`,
  then continues in the new one (`log rotated; read the rest of MessageLog.bak`).

State file: `C:\StatsLog\Ricciarelli_RIC2_state.json` (log position, recipe, counters).
Deleting it restarts the counting from the current log (use `-StartAt` on that run).

## Troubleshooting

| Log line | Cause | Fix |
|---|---|---|
| `cannot connect \\192.168.2.54\Ricciareli as spiga (Windows error 1326)` | Wrong user or password (password changed on the panel) | Re-store it: `install_ricciarelli_collector.ps1 -Ric2User spiga -Ric2Password '<new>'` (elevated) |
| `cannot connect ... (Windows error 53)` / `(error 67)` | Panel off, cable in LAN-1 instead of WAN, or share removed | Ping .54; plant cable in **WAN**; check the `Ricciareli` share on D: |
| `no access to ... and no credential file` | Account never stored | Run the installer with `-Ric2User` / `-Ric2Password` |
| `Accès refusé` on `Test-Path` / `Open` | Old script: the share was touched before logging in (S4U logon) | Fixed 25/11/2026; redeploy the current script |
| `+0 packs` every minute | Line stopped, or ControlTerm closed / log not written | Check the panel; `MessageLog.txt` should grow every second while ControlTerm runs |
| `log rotated and the previous file is gone` | Collector down for over an hour | Packs in the gap are missing; counting continues |
| `no recipe seen yet: using seed` | No 0x41 since the state was created | Fine if the seed matches the panel recipe; otherwise restart ControlTerm between productions (it resends the recipe) or set the seed parameters |
| `panel clock off by N min` | Panel clock badly wrong | Times are still corrected; set the panel clock when convenient |

## Limits of this workaround

- It relies on ControlTerm's debug log. A Ricciarelli software update could change or turn
  it off; RIC2 would then show `+0 packs` while producing.
- The metal-detector reject count is not in these messages (`espulsi_metal` = 0).
- Observed min / max weights are not written (`peso_min/max_registrato` = 0), as for RIC1.
