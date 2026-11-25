# RIC1 (192.168.2.53) — TCP 4660 telegram

RIC1's panel runs Ricciarelli's **HMI_Ricciarelli_CP** (.NET 4, Windows XP). Besides its
screen, it runs a small TCP server for a data concentrator: any client that connects and
sends a few bytes gets back one fixed-width ASCII telegram with the weigher's statistics,
ended by CR. The collector polls it once a minute.

## Panel prerequisites

| Setting | Where | Value |
|---|---|---|
| Telegram server port | `_AppConfig\AppConfig.xml`, `porta_tcp` | `4660` |
| Network | panel LAN | 192.168.2.53, reachable from the server |

Nothing else is needed on the panel. Port 4661 only sends a heartbeat byte; it is not used.

## Scheduled task

`SPIGA - Ricciarelli RIC1 Collector` runs, every minute:

```
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden ^
  -File "C:\ProgramData\SpigaRicciarelliCollector\ricciarelli_collector.ps1" -Once -Write -Ip 192.168.2.53 -Line RIC1
```

## Parameters

| Parameter | Default | Meaning |
|---|---|---|
| `-Ip` | `192.168.2.53` | Panel address |
| `-Port` | `4660` | Telegram server port (`porta_tcp`) |
| `-Line` | `RIC1` | `numero_linea` written to the database (4 characters) |
| `-WeightScale` | `10` | Telegram deviation units per gram (0.1 g) |
| `-Server` / `-Database` | `.\SQLEXPRESS` / `stats` | Target database (Windows authentication) |
| `-LogDir` | `C:\StatsLog` | Log and state folder |
| `-Write` | off | Without it: dry run, nothing written |
| `-Once` / `-Loop` | `-Once` | One poll and exit (the task), or poll forever every `-IntervalSec` |

## Manual test

```powershell
# What the panel sends right now, nothing written:
powershell -File C:\ProgramData\SpigaRicciarelliCollector\ricciarelli_collector.ps1 -Ip 192.168.2.53 -Line RIC1 -Once
```

Expected:

```
DRYRUN produzione: art='106GR' tot=49854 ok=49649 under=18 over=187 sottoTU1=0 nominal=250 mean=254,3 std=2,37 sane=True
DRYRUN lotto:      lot#=49 lotMean=254,3 lotStd=2,5 TU1=241 minAvg=249
```

`sane=True` means under + OK + over = total.

## Telegram map (offset:width, verified by decompiling the HMI and live checks)

| Offset:width | Field |
|---|---|
| 0:8 | under (−) total |
| 8:8 | OK total |
| 16:8 | over (+) total |
| 24:8 | total (= under + OK + over) |
| 35:5 | packs below TU1 |
| 40:10 | Σ squared deviations (std) |
| 50:10 | Σ deviations from nominal (mean); sign at 61 (1 = negative) |
| 82:4 | nominal net weight (g) |
| 88:2 | lot std (÷10 → g) |
| 90:4 + 95:1 | lot mean (g + tenths) |
| 96:4 | minimum average allowed (g) |
| 106:4 | lot number |
| 118:4 | TU1 threshold (g) |
| 126:40 | recipe / article name (space padded) |

mean = nominal + sign × (Σdev / total) / 10 ; std = √(Σdev² / total − (Σdev / total)²) / 10

## Production and lot tracking

The telegram is **cumulative** with no start time, so the collector keeps
`C:\StatsLog\Ricciarelli_RIC1_state.json`:

- **New production** when the total drops (counters reset on the panel) or the article changes.
- **New lot** when the lot number changes; the lot's `lotto` row is found again by
  `ID_produzione` + `ora_inizio`.

## Troubleshooting

| Log line | Cause | Fix |
|---|---|---|
| `panel 192.168.2.53:4660 unreachable (connect timeout)` | Panel off, cable, or network | Ping .53; check the panel is on and on the plant LAN |
| `unreachable (... refused)` | HMI program closed, or `porta_tcp` changed | Restart HMI_Ricciarelli_CP; check `porta_tcp` = 4660 |
| `SANITY FAIL (under+ok+over != total)` | Telegram read mid-update | Ignored; the next tick retries |
| `idle (no production)` | Counters at 0 | Normal after a reset, until packs are weighed |
| Two lot rows for the same lot number | State and SQL start times differ (fixed 25/11/2026) | Keep the row with the latest `ora_fine` |

Notes: the panel's clock runs about 6 days fast; it is not used (rows are stamped with the
server time). The panel serves one client at a time; do not point another poller at 4660.
