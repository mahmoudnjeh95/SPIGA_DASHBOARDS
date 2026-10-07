"""
Builds the SQLite dev database (spiga_dashboards.db) from a real export of
the Stiavelli `stats.dbo.produzione` table (mahmoud_db.csv at the repo root,
';'-separated, as exported from SSMS).

The dev DB mirrors what database/stiavelli_compat.sql builds on SQL Server:
  - produzione : the raw Stiavelli snapshot rows (cumulative counters)
  - Machines, Products, Employees, Shifts, Notes, MachineStatusLog
The app turns snapshots into per-hour production itself (ProductionRepository).

By default every timestamp is shifted so the newest snapshot lands "now"
(minus a few minutes): the dashboard's Day/Week/Month views and the
"connected" status (which compare against DateTime.Now) then show data out
of the box. Pass --no-shift to keep the original 2024 dates.

The export was taken with Stiavelli writing a snapshot every 30 min. That
cadence is configurable down to under a minute; --densify N splits every
interval of up to 2 h into N linearly interpolated snapshots (e.g. 60 ->
one every 30 s) to exercise the app at that rate.

Run (from the repo root or this folder):
    python database/generate_seed_data.py [--csv mahmoud_db.csv] [--no-shift] [--densify N]
"""
import argparse
import csv
import os
import sqlite3
from datetime import datetime, timedelta

HERE = os.path.dirname(os.path.abspath(__file__))
DB_PATH = os.path.join(HERE, "spiga_dashboards.db")
DEFAULT_CSV = os.path.join(HERE, "..", "mahmoud_db.csv")

# Real roster (IPs confirmed by client). MachineId == produzione.numero_linea.
MACHINES = [
    ("CC01", "Controle CC01", "CC", "Couscous",    "192.168.2.30"),
    ("CC02", "Controle CC02", "CC", "Couscous",    "192.168.2.31"),
    ("CC03", "Controle CC03", "CC", "Couscous",    "192.168.2.38"),
    ("CC04", "Controle CC04", "CC", "Couscous",    "192.168.2.39"),
    ("PC01", "Peseuse PC01",  "PC", "Pate courte", "192.168.2.35"),
    ("PC02", "Peseuse PC02",  "PC", "Pate courte", "192.168.2.36"),
    ("PC03", "Peseuse PC03",  "PC", "Pate courte", "192.168.2.37"),
    ("PL01", "Ligne PL01",    "PL", "Pate longue", "192.168.2.32"),
    ("PL02", "Ligne PL02",    "PL", "Pate longue", "192.168.2.33"),
    ("PL03", "Ligne PL03",    "PL", "Pate longue", "192.168.2.34"),
]

SHIFTS = [(1, "Matin", 6, 14), (2, "Soir", 14, 22), (3, "Nuit", 22, 6)]

DDL = """
CREATE TABLE Machines (
    MachineId   TEXT PRIMARY KEY,
    MachineName TEXT NOT NULL,
    MachineType TEXT NOT NULL,
    Sector      TEXT NOT NULL,
    IPAddress   TEXT NOT NULL,
    IsActive    INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE Products (
    ProductId            INTEGER PRIMARY KEY AUTOINCREMENT,
    Code                 TEXT NOT NULL UNIQUE,
    ProductName          TEXT NOT NULL,
    TargetWeightGrams    REAL NOT NULL DEFAULT 0,
    PackagingSize        TEXT NOT NULL DEFAULT '',
    NominalRatePerMinute REAL NOT NULL DEFAULT 0
);

CREATE TABLE Employees (
    EmployeeId INTEGER PRIMARY KEY AUTOINCREMENT,
    Code       TEXT NOT NULL UNIQUE,
    FullName   TEXT NOT NULL,
    Role       TEXT NOT NULL DEFAULT 'Operateur'
);

CREATE TABLE Shifts (
    ShiftId   INTEGER PRIMARY KEY,
    ShiftName TEXT NOT NULL,
    StartHour INTEGER NOT NULL,
    EndHour   INTEGER NOT NULL
);

CREATE TABLE MachineStatusLog (
    LogId       INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId   TEXT NOT NULL,
    Timestamp   TEXT NOT NULL,
    IsConnected INTEGER NOT NULL,
    BeltRunning INTEGER NOT NULL,
    AlarmActive INTEGER NOT NULL
);

CREATE TABLE Notes (
    NoteId              INTEGER PRIMARY KEY AUTOINCREMENT,
    Content             TEXT NOT NULL,
    CreatedByEmployeeId INTEGER NOT NULL,
    CreatedAt           TEXT NOT NULL,
    IsActive            INTEGER NOT NULL DEFAULT 1
);

-- Same columns as stats.dbo.produzione (timestamps as 'yyyy-MM-dd HH:mm:ss').
CREATE TABLE produzione (
    progressivo              INTEGER PRIMARY KEY,
    ID_produzione            TEXT,
    numero_linea             TEXT,
    tempo_inizio             TEXT,
    tempo_fine               TEXT,
    terminata                INTEGER,
    codice_articolo          TEXT,
    operatore                TEXT,
    turno                    INTEGER,
    linea_produzione         TEXT,
    peso_netto_impostato     INTEGER,
    peso_max_registrato      REAL,
    peso_min_registrato      REAL,
    totale_confezioni        INTEGER,
    confezioni_sotto_toll    INTEGER,
    confezioni_ok            INTEGER,
    confezioni_sopra_toll    INTEGER,
    confezioni_indeterminate INTEGER,
    media_totale             REAL,
    deviazione_std           REAL,
    espulsi_metal            INTEGER
);

-- Same index as stiavelli_compat.sql: the app reads one line's time range at a time.
CREATE INDEX IX_produzione_linea_fine ON produzione(numero_linea, tempo_fine);
"""

TS_FORMAT = "%Y-%m-%d %H:%M:%S"


def parse_ts(value):
    value = (value or "").strip()
    if not value or value.upper() == "NULL":
        return None
    return datetime.strptime(value[:19], TS_FORMAT)


def as_int(value):
    value = (value or "").strip()
    return None if value == "" or value.upper() == "NULL" else int(value)


def as_float(value):
    value = (value or "").strip()
    return None if value == "" or value.upper() == "NULL" else float(value)


COUNTERS = ["confezioni_sotto_toll", "confezioni_ok", "confezioni_sopra_toll",
            "confezioni_indeterminate", "espulsi_metal"]


def densify(rows, factor):
    """Splits each interval (<= 2 h) between two snapshots of the same
    production into `factor` snapshots, interpolating the cumulative counters,
    then renumbers progressivo in time order."""
    by_production = {}
    for r in rows:
        by_production.setdefault((r["numero_linea"], r["ID_produzione"]), []).append(r)

    out = []
    for snaps in by_production.values():
        snaps.sort(key=lambda r: int(r["progressivo"]))
        out.append(snaps[0])
        for a, b in zip(snaps, snaps[1:]):
            ta, tb = parse_ts(a["tempo_fine"]), parse_ts(b["tempo_fine"])
            if ta and tb and timedelta(0) < tb - ta <= timedelta(hours=2):
                for k in range(1, factor):
                    f = k / factor
                    mid = dict(b)
                    mid["tempo_fine"] = (ta + (tb - ta) * f).strftime(TS_FORMAT)
                    for c in COUNTERS:
                        mid[c] = str(round(int(a[c]) + (int(b[c]) - int(a[c])) * f))
                    # Keep Stiavelli's invariant: total = sum of the categories.
                    mid["totale_confezioni"] = str(sum(int(mid[c]) for c in COUNTERS[:4]))
                    out.append(mid)
            out.append(b)

    out.sort(key=lambda r: (r["tempo_fine"], r["numero_linea"], int(r["progressivo"])))
    for i, r in enumerate(out, start=1):
        r["progressivo"] = str(i)
    return out


def build(csv_path, shift_to_now, densify_factor):
    with open(csv_path, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f, delimiter=";"))
    if not rows:
        raise SystemExit(f"No rows in {csv_path}")
    if densify_factor > 1:
        rows = densify(rows, densify_factor)

    shift = timedelta(0)
    if shift_to_now:
        latest = max(parse_ts(r["tempo_fine"]) for r in rows if parse_ts(r["tempo_fine"]))
        # Newest snapshot = now. The app's automatic "connected" window is 2.5x
        # the snapshot interval (min 2 min), so with --densify the lines turn
        # "disconnected" a couple of minutes after generation: regenerate, or set
        # ConnectedWindowMinutes in settings.json while testing.
        target = datetime.now().replace(second=0, microsecond=0)
        shift = target - latest

    def ts(value):
        parsed = parse_ts(value)
        return None if parsed is None else (parsed + shift).strftime(TS_FORMAT)

    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
    conn = sqlite3.connect(DB_PATH)
    conn.executescript(DDL)

    conn.executemany("INSERT INTO Machines VALUES (?,?,?,?,?,1)", MACHINES)
    conn.executemany("INSERT INTO Shifts VALUES (?,?,?,?)", SHIFTS)

    conn.executemany(
        "INSERT INTO produzione VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        [
            (
                as_int(r["progressivo"]), r["ID_produzione"], r["numero_linea"],
                ts(r["tempo_inizio"]), ts(r["tempo_fine"]), as_int(r["terminata"]),
                r["codice_articolo"], r["operatore"], as_int(r["turno"]), r["linea_produzione"],
                as_int(r["peso_netto_impostato"]), as_float(r["peso_max_registrato"]),
                as_float(r["peso_min_registrato"]), as_int(r["totale_confezioni"]),
                as_int(r["confezioni_sotto_toll"]), as_int(r["confezioni_ok"]),
                as_int(r["confezioni_sopra_toll"]), as_int(r["confezioni_indeterminate"]),
                as_float(r["media_totale"]), as_float(r["deviazione_std"]), as_int(r["espulsi_metal"]),
            )
            for r in rows
        ],
    )

    # Same seeding rule as stiavelli_compat.sql: name = code without zero padding.
    conn.execute(
        """INSERT INTO Products (Code, ProductName, TargetWeightGrams)
           SELECT TRIM(codice_articolo),
                  COALESCE(NULLIF(LTRIM(TRIM(codice_articolo), '0'), ''), TRIM(codice_articolo)),
                  MAX(peso_netto_impostato)
           FROM produzione
           WHERE TRIM(IFNULL(codice_articolo, '')) <> ''
           GROUP BY TRIM(codice_articolo)"""
    )

    conn.execute(
        "INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive) VALUES (?,?,?,1)",
        ("Base de dev construite a partir de mahmoud_db.csv (export reel de produzione).", 1,
         datetime.now().strftime(TS_FORMAT)),
    )

    conn.commit()

    count = conn.execute("SELECT COUNT(*) FROM produzione").fetchone()[0]
    span = conn.execute("SELECT MIN(tempo_fine), MAX(tempo_fine) FROM produzione").fetchone()
    print(f"{DB_PATH}: {count} snapshots, {span[0]} -> {span[1]} (shift {shift})")
    for machine_id, snapshots in conn.execute(
        """SELECT p.numero_linea, COUNT(*) FROM produzione p
           JOIN Machines m ON m.MachineId = p.numero_linea GROUP BY p.numero_linea ORDER BY 1"""
    ):
        print(f"  {machine_id}: {snapshots} snapshots")
    conn.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--csv", default=DEFAULT_CSV, help="produzione export (';'-separated)")
    parser.add_argument("--no-shift", action="store_true", help="keep the original timestamps")
    parser.add_argument("--densify", type=int, default=1, metavar="N",
                        help="interpolate N snapshots per original interval (simulates a faster cadence)")
    args = parser.parse_args()
    build(os.path.abspath(args.csv), shift_to_now=not args.no_shift, densify_factor=args.densify)
