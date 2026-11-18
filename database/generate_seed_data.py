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
    python database/generate_seed_data.py [--csv mahmoud_db.csv] [--no-shift] [--densify N] [--fill-month]
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


def fill_current_month(conn):
    """The export only holds a few distinct days, so the current month is mostly
    empty after the shift. Clone the newest block of days (whole productions,
    with fresh ID_produzione) backwards in block-sized steps until the 1st of
    the month is covered."""
    block_days = 3
    now = datetime.now()
    month_start = now.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
    block_start = (now - timedelta(days=block_days - 1)).replace(hour=0, minute=0, second=0, microsecond=0)
    cols = [r[1] for r in conn.execute("PRAGMA table_info(produzione)")]
    src = conn.execute(
        f"SELECT {','.join(cols)} FROM produzione WHERE tempo_fine >= ? ORDER BY progressivo",
        (block_start.strftime(TS_FORMAT),),
    ).fetchall()
    next_id = conn.execute("SELECT MAX(progressivo) FROM produzione").fetchone()[0] + 1
    idx = {c: i for i, c in enumerate(cols)}

    step = 1
    while block_start - timedelta(days=block_days * (step - 1)) > month_start:
        delta = timedelta(days=block_days * step)
        batch = []
        for row in src:
            row = list(row)
            for c in ("tempo_inizio", "tempo_fine"):
                if row[idx[c]]:
                    shifted = datetime.strptime(row[idx[c]], TS_FORMAT) - delta
                    row[idx[c]] = shifted.strftime(TS_FORMAT)
            if row[idx["tempo_fine"]] < month_start.strftime(TS_FORMAT):
                continue
            row[idx["ID_produzione"]] = f"{row[idx['ID_produzione']]}-d{step}"
            row[idx["progressivo"]] = next_id
            next_id += 1
            batch.append(row)
        conn.executemany(f"INSERT INTO produzione VALUES ({','.join('?' * len(cols))})", batch)
        step += 1


def build(csv_path, shift_to_now, densify_factor, fill_month=False):
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

    if fill_month:
        fill_current_month(conn)

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


# --- Synthetic data (--synthetic DAYS) -------------------------------------
# The CSV export only covers a few lines and its weights fail the app's
# sanity checks. This mode generates every machine instead: realistic
# productions per line (two articles / weights each), stops, changeovers, a
# target weight changed mid-production now and then (Stiavelli does that
# without opening a new ID_produzione), sane running averages and a lotto
# table with real lot averages.

# Per line: articles (code, name, target g) and actual speed / nominal cadence.
# The weight is the target, not part of the name: an article runs in several weights.
SYNTH_LINES = {
    "CC01": ([("COUSCOUSFIN", "Couscous fin", 1000), ("COUSCOUSFIN", "Couscous fin", 500)], 0.90),
    "CC02": ([("COUSCOUSMOYEN", "Couscous moyen", 1000), ("COUSCOUSMOYEN", "Couscous moyen", 500)], 0.88),
    "CC03": ([("COUSCOUSGROS", "Couscous gros", 1000), ("COUSCOUSFIN", "Couscous fin", 500)], 0.84),
    "CC04": ([("COUSCOUSMOYEN", "Couscous moyen", 500), ("COUSCOUSFIN", "Couscous fin", 1000)], 0.78),
    "PC01": ([("PENNE", "Penne", 500), ("COQUILLETTES", "Coquillettes", 400)], 0.86),
    "PC02": ([("FUSILLI", "Fusilli", 500), ("PENNE", "Penne", 400)], 0.90),
    "PC03": ([("MACARONI", "Macaroni", 500), ("FUSILLI", "Fusilli", 400)], 0.82),
    "PL01": ([("SPAGHETTI", "Spaghetti", 500), ("SPAGHETTI", "Spaghetti", 1000)], 0.88),
    "PL02": ([("TAGLIATELLE", "Tagliatelle", 500), ("SPAGHETTI", "Spaghetti", 1000)], 0.85),
    "PL03": ([("LINGUINE", "Linguine", 500), ("SPAGHETTI", "Spaghetti", 500)], 0.75),
}

# Same defaults as the app's Parametres cadence table (bags/min per weight).
SYNTH_NOMINAL = {400: 50, 500: 45, 1000: 20}


def synth_code(code):
    # Stiavelli article codes are CHAR(20), zero-padded on the left.
    return code.rjust(20, "0")[:20]


def build_synthetic(days, cadence_minutes, seed):
    import random
    rnd = random.Random(seed)

    if os.path.exists(DB_PATH):
        os.remove(DB_PATH)
    conn = sqlite3.connect(DB_PATH)
    conn.executescript(DDL)
    conn.executescript("""
CREATE TABLE lotto (
    ID_produzione   TEXT,
    ora_fine        TEXT,
    dimensione      INTEGER,
    media_effettiva REAL
);
CREATE INDEX IX_lotto_ora_fine ON lotto(ora_fine);
""")
    conn.executemany("INSERT INTO Machines VALUES (?,?,?,?,?,1)", MACHINES)
    conn.executemany("INSERT INTO Shifts VALUES (?,?,?,?)", SHIFTS)

    products = {}
    for articles, _ in SYNTH_LINES.values():
        for code, name, weight in articles:
            products[synth_code(code)] = (name, weight)
    conn.executemany(
        "INSERT INTO Products (Code, ProductName, TargetWeightGrams) VALUES (?,?,?)",
        [(code, name, weight) for code, (name, weight) in products.items()],
    )

    step = timedelta(minutes=cadence_minutes)
    now = datetime.now().replace(second=0, microsecond=0)
    now -= timedelta(minutes=now.minute % cadence_minutes)
    start = (now - timedelta(days=days)).replace(hour=0, minute=0)

    snapshots, lots = [], []
    for line, (articles, efficiency) in SYNTH_LINES.items():
        bias = rnd.uniform(-1.5, 6.0)          # line's habitual over/under weight (g)
        t = start + timedelta(seconds=rnd.randint(0, 59))
        article_index = 0
        while t < now:
            # One production = one article run of 1 to 3 days.
            code, _, target = articles[article_index % len(articles)]
            article_index += 1
            prod_start = t
            prod_end = min(now, t + timedelta(hours=rnd.randint(20, 72)))
            prod_id = f"{line}{synth_code(code)}{prod_start:%d.%m.%Y-%H:%M:%S}"
            # Now and then the operator changes the set weight mid-run.
            change_at = prod_start + (prod_end - prod_start) * rnd.uniform(0.4, 0.8) if rnd.random() < 0.25 else None
            other_weight = next((w for _, _, w in articles if w != target), target)
            counters = dict(tot=0, sotto=0, ok=0, sopra=0, indet=0, metal=0)
            weight_sum, lot_bags, lot_sum, lot_target = 0.0, 0, 0.0, target
            running, stop_left = True, 0
            pmin, pmax = None, None
            while t < prod_end:
                t += step
                current = other_weight if change_at and t >= change_at else target
                nominal = SYNTH_NOMINAL.get(current, 30)
                # Stops: short micro-stops, longer breakdowns, quieter nights.
                if stop_left > 0:
                    stop_left -= 1
                    running = stop_left == 0
                # About 3 to 5 stops of 5 min or more per line and day.
                elif rnd.random() < (0.012 if 6 <= t.hour < 22 else 0.02):
                    stop_left = rnd.choice([1, 1, 2, 3, 6, 12])
                    running = False
                # Stiavelli closes the running lot when the set weight changes.
                if lot_bags and current != lot_target:
                    lots.append((prod_id, t.strftime(TS_FORMAT), lot_bags, round(lot_sum / lot_bags, 2)))
                    lot_bags, lot_sum = 0, 0.0
                lot_target = current
                bags = 0
                if running:
                    bags = max(0, int(nominal * efficiency * rnd.uniform(0.85, 1.08) * cadence_minutes))
                if bags:
                    under = int(bags * rnd.uniform(0.002, 0.012))
                    over = int(bags * rnd.uniform(0.002, 0.010))
                    indet = int(bags * rnd.uniform(0, 0.001))
                    counters["sotto"] += under
                    counters["sopra"] += over
                    counters["indet"] += indet
                    counters["ok"] += bags - under - over - indet
                    counters["tot"] += bags
                    counters["metal"] += 1 if rnd.random() < 0.01 else 0
                    avg = current + bias + rnd.gauss(0, 0.8)
                    weight_sum += avg * bags
                    lot_bags += bags
                    lot_sum += avg * bags
                    pmin = min(pmin or current, current * rnd.uniform(0.965, 0.985))
                    pmax = max(pmax or current, current * rnd.uniform(1.02, 1.04))
                # A lot is closed every ~15 min of production.
                if lot_bags and (lot_bags >= nominal * 15 or not running):
                    lots.append((prod_id, t.strftime(TS_FORMAT), lot_bags, round(lot_sum / lot_bags, 2)))
                    lot_bags, lot_sum = 0, 0.0
                media = round(weight_sum / counters["tot"], 1) if counters["tot"] else 0
                snapshots.append((
                    prod_id, line, prod_start.strftime(TS_FORMAT), t.strftime(TS_FORMAT),
                    1 if t >= prod_end and prod_end < now else 0, synth_code(code), "", 0, line,
                    current, pmax, pmin, counters["tot"], counters["sotto"], counters["ok"],
                    counters["sopra"], counters["indet"], media, round(rnd.uniform(1.5, 3.5), 2), counters["metal"],
                ))
            # Changeover: 20 to 60 min without snapshot growth.
            t += timedelta(minutes=rnd.randint(20, 60))

    snapshots.sort(key=lambda r: (r[3], r[1]))
    conn.executemany(
        "INSERT INTO produzione (ID_produzione, numero_linea, tempo_inizio, tempo_fine, terminata, codice_articolo,"
        " operatore, turno, linea_produzione, peso_netto_impostato, peso_max_registrato, peso_min_registrato,"
        " totale_confezioni, confezioni_sotto_toll, confezioni_ok, confezioni_sopra_toll, confezioni_indeterminate,"
        " media_totale, deviazione_std, espulsi_metal) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        snapshots,
    )
    conn.executemany("INSERT INTO lotto VALUES (?,?,?,?)", lots)
    conn.execute(
        "INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive) VALUES (?,?,?,1)",
        ("Base de dev : donnees synthetiques pour toutes les machines.", 1, datetime.now().strftime(TS_FORMAT)),
    )
    conn.commit()

    span = conn.execute("SELECT MIN(tempo_fine), MAX(tempo_fine) FROM produzione").fetchone()
    print(f"{DB_PATH}: {len(snapshots)} snapshots, {len(lots)} lots, {span[0]} -> {span[1]}")
    for machine_id, count, weights in conn.execute(
        "SELECT numero_linea, COUNT(*), GROUP_CONCAT(DISTINCT peso_netto_impostato) FROM produzione GROUP BY 1 ORDER BY 1"
    ):
        print(f"  {machine_id}: {count} snapshots, weights {weights}")
    conn.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--csv", default=DEFAULT_CSV, help="produzione export (';'-separated)")
    parser.add_argument("--no-shift", action="store_true", help="keep the original timestamps")
    parser.add_argument("--densify", type=int, default=1, metavar="N",
                        help="interpolate N snapshots per original interval (simulates a faster cadence)")
    parser.add_argument("--fill-month", action="store_true",
                        help="clone the newest days back to the 1st of the current month")
    parser.add_argument("--synthetic", type=int, default=0, metavar="DAYS",
                        help="generate DAYS of synthetic data for every machine instead of reading the CSV")
    parser.add_argument("--cadence", type=int, default=5, metavar="MIN",
                        help="snapshot interval of --synthetic, in minutes (default 5)")
    parser.add_argument("--seed", type=int, default=42, help="random seed of --synthetic")
    args = parser.parse_args()
    if args.synthetic > 0:
        build_synthetic(args.synthetic, args.cadence, args.seed)
        raise SystemExit
    build(os.path.abspath(args.csv), shift_to_now=not args.no_shift, densify_factor=args.densify,
          fill_month=args.fill_month)
