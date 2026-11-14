"""
Demo database for SPIGA Dashboards: a believable plant, up to this minute.

Builds the SQLite dev database with every line producing since the app's
DataStartDate (20/10/2026), then stages "today" so each screen has something
to show (dashboard, Arrêts, Planification, TV):

  - 1-minute snapshots over the last 26 h (5 min before), products, weights
    and cadences matching the app's default Parametres table;
  - stops with their causes (PlanStopQualifications), a few left to qualify;
  - right now: PL02 down on a qualified mechanical panne with an expected
    return, PC03 freshly stopped with no cause yet (red pop-up), CC04 on a
    planned cleaning stop;
  - production orders, preventive maintenance rules and slots on the planning
    board; a note for the yellow banner.

    python database/seed_demo_data.py            # build, timestamps end "now"
    python database/seed_demo_data.py --live     # then keep the lines alive:
                                                 # one snapshot a minute

--live must keep running while the app is shown (the lines turn
"disconnected" a few minutes after the last snapshot). In it PC03 restarts
after 3 minutes (the REPRISE bar) and PL02 at its expected return.

The app reads this file when Parametres > Base de données is on SQLite
(settings.json "Provider": 0). --out writes somewhere else, e.g. beside a
copy of the .exe.
"""
import argparse
import os
import random
import sqlite3
import time
from datetime import datetime, timedelta

from generate_seed_data import DDL, MACHINES, SHIFTS, TS_FORMAT, synth_code

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.join(HERE, "spiga_dashboards.db")

DATA_START = datetime(2026, 10, 20)          # AppSettings.DataStartDate

# Per line: articles (code, name, target g), nominal cadence (bags/min) and
# sachet vide per weight, as in AppSettings.NominalRates; then how fast the
# line really runs (share of nominal), its stop rate per hour and its usual
# over-weight in grams.
LINES = {
    "CC01": dict(articles=[("COUSCOUSFIN", "Couscous fin", 1000), ("COUSCOUSFIN", "Couscous fin", 800)],
                 rate=60, tare={1000: 5, 800: 4}, speed=0.95, stops=0.20, bias=2.1),
    "CC02": dict(articles=[("COUSCOUSMOYEN", "Couscous moyen", 1000), ("COUSCOUSMOYEN", "Couscous moyen", 500)],
                 rate=60, tare={}, speed=0.93, stops=0.24, bias=1.2),
    "CC03": dict(articles=[("COUSCOUSGROS", "Couscous gros", 1000)],
                 rate=50, tare={1000: 6}, speed=0.96, stops=0.18, bias=3.4),
    "CC04": dict(articles=[("COUSCOUSMOYEN", "Couscous moyen", 500), ("COUSCOUSFIN", "Couscous fin", 1000)],
                 rate=50, tare={500: 8, 1000: 6}, speed=0.90, stops=0.30, bias=0.6),
    "PC01": dict(articles=[("COQUILLETTES", "Coquillettes", 400)],
                 rate=100, tare={}, speed=0.94, stops=0.22, bias=0.9),
    "PC02": dict(articles=[("FUSILLI", "Fusilli", 500)],
                 rate=100, tare={}, speed=0.96, stops=0.16, bias=-0.4),
    "PC03": dict(articles=[("MACARONI", "Macaroni", 500), ("PENNE", "Penne", 500)],
                 rate=100, tare={}, speed=0.91, stops=0.28, bias=1.7),
    "PL01": dict(articles=[("SPAGHETTI", "Spaghetti", 400)],
                 rate=100, tare={400: 5}, speed=0.93, stops=0.22, bias=1.1),
    "PL02": dict(articles=[("TAGLIATELLE", "Tagliatelle", 400)],
                 rate=100, tare={}, speed=0.92, stops=0.26, bias=0.3),
    "PL03": dict(articles=[("LINGUINE", "Linguine", 500), ("SPAGHETTI", "Spaghetti", 500)],
                 rate=100, tare={}, speed=0.84, stops=0.42, bias=2.6),
}

# (code, weight in the draw, min minutes, max minutes) - codes of Models/Planning.cs.
CAUSES = [
    ("REGLAGE", 20, 6, 18), ("EMBALL", 15, 6, 16), ("MATIERE", 12, 6, 25), ("FORMAT", 9, 20, 45),
    ("MECA", 11, 15, 80), ("ELEC", 5, 12, 60), ("PNEU", 4, 10, 40), ("QUALITE", 7, 6, 15),
    ("NETT", 8, 10, 30), ("OPERATEUR", 3, 6, 15), ("PAUSE", 3, 6, 12),
]
PANNES = {"MECA", "ELEC", "PNEU"}
COMPONENTS = {
    "MECA": ["Mâchoires de soudure", "Convoyeur", "Chaîne de transfert", "Tapis", "Doseur / peseuse"],
    "ELEC": ["Capteur", "Moteur", "Automate"],
    "PNEU": ["Vérin", "Mâchoires de soudure"],
}
TECHNICIANS = ["S. Trabelsi", "M. Gharbi", "K. Jaziri", "A. Ben Salah"]
COMMENTS = {
    "MECA": ["Résistance de soudure remplacée", "Courroie retendue", "Roulement changé", "Guide film réaligné"],
    "ELEC": ["Cellule de détection remplacée", "Variateur réarmé", "Connecteur resserré"],
    "PNEU": ["Fuite d'air sur le vérin, joint changé", "Électrovanne remplacée"],
    "REGLAGE": ["Réglage de la dose", "Centrage du film", "Réglage température de soudure"],
    "EMBALL": ["Changement de bobine", "Attente cartons", "Film mal enroulé"],
    "MATIERE": ["Trémie vide, attente produit", "Attente séchoir"],
    "FORMAT": ["Passage 500 g vers 1 kg", "Changement d'article"],
    "QUALITE": ["Contrôle poids", "Prélèvement laboratoire"],
    "NETT": ["Nettoyage peseuse", "Nettoyage fin de lot"],
    "OPERATEUR": ["Opérateur appelé sur une autre ligne"],
    "PAUSE": ["Réunion d'équipe"],
}

# AppSettings.TeamRotationSettings defaults (8-day cycle from 01/11/2026).
TEAM_NAMES = ["Equipe D", "Equipe B", "Equipe C", "Equipe A"]
ROTATION_START = datetime(2026, 11, 1)
SITE_CYCLE = [(2, 0, 3), (1, 2, 0), (1, 2, 0), (3, 1, 2), (3, 1, 2), (0, 3, 1), (0, 3, 1), (2, 0, 3)]

PLAN_DDL = """
CREATE TABLE PlanStopQualifications (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId TEXT NOT NULL, StopStart TEXT NOT NULL, StopEnd TEXT NOT NULL,
    ShiftDate TEXT NOT NULL, ShiftId INTEGER NOT NULL, Team TEXT NOT NULL,
    CauseCode TEXT NOT NULL, Component TEXT NULL, Technician TEXT NULL, Comment TEXT NULL,
    IsManual INTEGER NOT NULL, CreatedAt TEXT NOT NULL, ExpectedEnd TEXT NULL);
CREATE INDEX IX_PlanStopQualifications_Start ON PlanStopQualifications (StopStart, MachineId);
CREATE TABLE PlanMaintenancePlans (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId TEXT NOT NULL, Task TEXT NOT NULL, Kind INTEGER NOT NULL,
    IntervalDays INTEGER NOT NULL, IntervalBags INTEGER NOT NULL, DurationMinutes INTEGER NOT NULL,
    LastDoneAt TEXT NULL, IsActive INTEGER NOT NULL);
CREATE TABLE PlanMaintenanceEvents (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PlanId INTEGER NULL, MachineId TEXT NOT NULL, Task TEXT NOT NULL,
    PlannedStart TEXT NOT NULL, DurationMinutes INTEGER NOT NULL, Status INTEGER NOT NULL,
    DoneAt TEXT NULL, Technician TEXT NULL, Comment TEXT NULL, Kind INTEGER NOT NULL DEFAULT 0);
CREATE TABLE PlanProductionOrders (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    OrderNo TEXT NOT NULL, MachineId TEXT NOT NULL, Product TEXT NOT NULL,
    TargetWeightGrams REAL NOT NULL, QuantityBags INTEGER NOT NULL,
    PlannedStart TEXT NOT NULL, PlannedEnd TEXT NOT NULL, Status INTEGER NOT NULL, Comment TEXT NULL);
CREATE TABLE lotto (
    ID_produzione   TEXT,
    ora_fine        TEXT,
    dimensione      INTEGER,
    media_effettiva REAL
);
CREATE INDEX IX_lotto_ora_fine ON lotto(ora_fine);
-- What --live needs to carry each line on (not read by the app).
CREATE TABLE DemoLiveState (
    MachineId TEXT PRIMARY KEY, BagsPerMinute REAL NOT NULL, WeightBias REAL NOT NULL,
    StoppedUntil TEXT NULL, WeightSum REAL NOT NULL, LotBags INTEGER NOT NULL, LotSum REAL NOT NULL);
"""

SNAPSHOT_SQL = (
    "INSERT INTO produzione (ID_produzione, numero_linea, tempo_inizio, tempo_fine, terminata, codice_articolo,"
    " operatore, turno, linea_produzione, peso_netto_impostato, peso_max_registrato, peso_min_registrato,"
    " totale_confezioni, confezioni_sotto_toll, confezioni_ok, confezioni_sopra_toll, confezioni_indeterminate,"
    " media_totale, deviazione_std, espulsi_metal) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
)
FOREVER = "9999-12-31 00:00:00"


def fmt(t):
    return t.strftime(TS_FORMAT)


def shift_of(t):
    return 1 if 6 <= t.hour < 14 else 2 if 14 <= t.hour < 22 else 3


def shift_date(t):
    return (t - timedelta(days=1) if t.hour < 6 else t).replace(hour=0, minute=0, second=0, microsecond=0)


def team_of(t):
    day = (shift_date(t) - ROTATION_START).days % len(SITE_CYCLE)
    return TEAM_NAMES[SITE_CYCLE[day][shift_of(t) - 1]]


def poste_pieces(start, end):
    """[start, end) cut at 06:00 / 14:00 / 22:00, as StopDetector does."""
    pieces, t = [], start
    while t < end:
        hour = 6 if t.hour < 6 else 14 if t.hour < 14 else 22 if t.hour < 22 else 30
        boundary = t.replace(hour=0, minute=0, second=0, microsecond=0) + timedelta(hours=hour)
        pieces.append((t, min(end, boundary)))
        t = boundary
    return pieces


def bags_of(rnd, cfg, minutes):
    return max(0, int(cfg["rate"] * cfg["speed"] * rnd.uniform(0.93, 1.05) * minutes))


def add_bags(rnd, counters, bags):
    under = int(bags * rnd.uniform(0.002, 0.008))
    over = int(bags * rnd.uniform(0.002, 0.007))
    indet = int(bags * rnd.uniform(0, 0.0008))
    counters["sotto"] += under
    counters["sopra"] += over
    counters["indet"] += indet
    counters["ok"] += bags - under - over - indet
    counters["tot"] += bags
    counters["metal"] += 1 if rnd.random() < 0.004 else 0


def staged_stops(now):
    """Today's scripted stops: line -> [(start, end or None, cause or None, extra)]."""
    def at(hour, minute):
        return now.replace(hour=hour, minute=minute)

    def ago(minutes):
        return now - timedelta(minutes=minutes)

    staged = {
        # Down right now, cause known, return announced on the TV.
        "PL02": [(ago(38), None, "MECA", dict(component="Mâchoires de soudure", technician="S. Trabelsi",
                                             comment="Résistance de soudure HS, remplacement en cours",
                                             expected=now + timedelta(minutes=25)))],
        # Just stopped, nobody has said why yet: the red pop-up.
        "PC03": [(ago(7), None, None, {})],
        # Planned cleaning: a slot of the calendar, out of the TRS.
        "CC04": [(ago(52), None, "SLOT", {})],
        # This morning, left for the chef d'équipe to qualify.
        "CC01": [(ago(190), ago(176), None, {})],
        "PL03": [(ago(240), ago(219), None, {})],
    }
    return {line: [s for s in stops if s[0] > now - timedelta(hours=20)] for line, stops in staged.items()}


def build(out_path, now, seed):
    rnd = random.Random(seed)
    if os.path.exists(out_path):
        os.remove(out_path)
    conn = sqlite3.connect(out_path)
    conn.executescript(DDL)
    conn.executescript(PLAN_DDL)
    conn.executemany("INSERT INTO Machines VALUES (?,?,?,?,?,1)", MACHINES)
    conn.executemany("INSERT INTO Shifts VALUES (?,?,?,?)", SHIFTS)
    conn.executemany("INSERT INTO Employees (Code, FullName, Role) VALUES (?,?,?)", [
        ("CE01", "Chef d'équipe A", "Chef d'equipe"), ("CE02", "Chef d'équipe B", "Chef d'equipe"),
        ("CE03", "Chef d'équipe C", "Chef d'equipe"), ("CE04", "Chef d'équipe D", "Chef d'equipe"),
    ])
    products = {}
    for cfg in LINES.values():
        for code, name, weight in cfg["articles"]:
            products[synth_code(code)] = (name, weight)
    conn.executemany("INSERT INTO Products (Code, ProductName, TargetWeightGrams) VALUES (?,?,?)",
                     [(code, name, weight) for code, (name, weight) in products.items()])

    fine_from = now - timedelta(hours=26)       # 1-minute snapshots from here
    staged = staged_stops(now)
    snapshots, lots, qualifications, live = [], [], [], []

    for line, cfg in LINES.items():
        # --- The line's stops: random history, then today's script. ---------
        scripted = staged.get(line, [])
        quiet_from = min([s[0] for s in scripted] + [now]) - timedelta(minutes=25)
        stops, t = [], DATA_START + timedelta(minutes=rnd.randint(30, 240))
        while t < quiet_from:
            if rnd.random() < 0.45:              # micro-stop, under the 5 min threshold
                minutes, cause = rnd.randint(1, 3), "MICRO"
            else:
                cause, _, lo, hi = rnd.choices(CAUSES, weights=[c[1] for c in CAUSES])[0]
                minutes = rnd.randint(lo, hi)
            fine = t >= fine_from
            step = 1 if fine else 5
            start = t.replace(second=0) - timedelta(minutes=t.minute % step)
            minutes = max(step, round(minutes / step) * step)
            end = start + timedelta(minutes=minutes)
            if end < quiet_from and (fine or cause != "MICRO"):
                stops.append((start, end, cause, {}))
            t = end + timedelta(minutes=rnd.expovariate(cfg["stops"] / 60) + 12)
        stops += scripted

        def stopped_at(a, b):
            return any(s[0] <= a and (s[1] is None or b <= s[1]) for s in stops)

        # --- Snapshots. -----------------------------------------------------
        t = DATA_START + timedelta(minutes=5)
        article_index = rnd.randint(0, 1)
        state = None
        while t < now:
            code, _, target = cfg["articles"][article_index % len(cfg["articles"])]
            article_index += 1
            prod_start = t
            prod_end = min(now, t + timedelta(hours=rnd.randint(30, 80)))
            # The run in progress reaches "now" (no changeover in the last hours).
            if now - prod_end < timedelta(hours=12):
                prod_end = now
            prod_id = f"{line}{synth_code(code)}{prod_start:%d.%m.%Y-%H:%M:%S}"
            tare = cfg["tare"].get(target, 0)
            counters = dict(tot=0, sotto=0, ok=0, sopra=0, indet=0, metal=0)
            weight_sum, lot_bags, lot_sum = 0.0, 0, 0.0
            pmin, pmax = target * 0.975, target * 1.03
            sigma = round(rnd.uniform(1.6, 3.2), 2)
            while t < prod_end:
                step = 1 if t >= fine_from else 5
                previous, t = t, t + timedelta(minutes=step)
                running = not stopped_at(previous, t)
                bags = bags_of(rnd, cfg, step) if running else 0
                if bags:
                    add_bags(rnd, counters, bags)
                    avg = target + tare + cfg["bias"] + rnd.gauss(0, 0.5)
                    weight_sum += avg * bags
                    lot_bags += bags
                    lot_sum += avg * bags
                    pmin = min(pmin, target * rnd.uniform(0.968, 0.985))
                    pmax = max(pmax, target * rnd.uniform(1.02, 1.038))
                if lot_bags and (lot_bags >= cfg["rate"] * 15 or not running):
                    lots.append((prod_id, fmt(t), lot_bags, round(lot_sum / lot_bags, 2)))
                    lot_bags, lot_sum = 0, 0.0
                media = round(weight_sum / counters["tot"], 1) if counters["tot"] else 0
                snapshots.append((
                    prod_id, line, fmt(prod_start), fmt(t), 1 if t >= prod_end and prod_end < now else 0,
                    synth_code(code), "", 0, line, target, round(pmax, 1), round(pmin, 1),
                    counters["tot"], counters["sotto"], counters["ok"], counters["sopra"], counters["indet"],
                    media, sigma, counters["metal"],
                ))
            state = (weight_sum, lot_bags, lot_sum)
            if t < now:                           # changeover between two runs
                stops.append((t, t + timedelta(minutes=30), "FORMAT", {}))

        ongoing = next((s for s in stops if s[1] is None), None)
        until = None
        if ongoing:
            until = {"PC03": fmt(now + timedelta(minutes=3)),
                     "PL02": fmt(now + timedelta(minutes=25))}.get(line, FOREVER)
        live.append((line, cfg["rate"] * cfg["speed"], cfg["bias"], until, *state))

        # --- Causes: every stop of 5 min or more, but the ones left open. ----
        for start, end, cause, extra in stops:
            end_or_now = end or now
            if cause in (None, "MICRO", "SLOT") or (end_or_now - start).total_seconds() < 300:
                continue
            if end and now - end > timedelta(hours=30) and rnd.random() < 0.06:
                continue                          # a few old ones never qualified
            component = extra.get("component") or (rnd.choice(COMPONENTS[cause]) if cause in PANNES else None)
            technician = extra.get("technician") or (rnd.choice(TECHNICIANS) if cause in PANNES else None)
            comment = extra.get("comment") or (rnd.choice(COMMENTS[cause]) if rnd.random() < 0.7 else None)
            expected = extra.get("expected")
            for piece_start, piece_end in poste_pieces(start, end_or_now):
                qualifications.append((
                    line, fmt(piece_start), fmt(piece_end), fmt(shift_date(piece_start)), shift_of(piece_start),
                    team_of(piece_start), cause, component, technician, comment, 0,
                    fmt(min(now, piece_end + timedelta(minutes=rnd.randint(2, 40)))),
                    fmt(expected) if expected else None,
                ))

    snapshots.sort(key=lambda r: (r[3], r[1]))
    conn.executemany(SNAPSHOT_SQL, snapshots)
    conn.executemany("INSERT INTO lotto VALUES (?,?,?,?)", lots)
    conn.executemany(
        "INSERT INTO PlanStopQualifications (MachineId, StopStart, StopEnd, ShiftDate, ShiftId, Team, CauseCode,"
        " Component, Technician, Comment, IsManual, CreatedAt, ExpectedEnd) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
        qualifications)
    conn.executemany("INSERT INTO DemoLiveState VALUES (?,?,?,?,?,?,?)", live)
    seed_planning(conn, rnd, now)
    conn.execute(
        "INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive) VALUES (?,?,?,1)",
        ("Objectif du jour : 310 t conditionnées  ·  Audit qualité jeudi 10 h  ·  Bravo à l'équipe B, meilleur TRS hier !",
         1, fmt(now)))
    conn.commit()

    span = conn.execute("SELECT MIN(tempo_fine), MAX(tempo_fine) FROM produzione").fetchone()
    print(f"{out_path}: {len(snapshots)} snapshots, {len(lots)} lots, {len(qualifications)} causes, {span[0]} -> {span[1]}")
    conn.close()


def seed_planning(conn, rnd, now):
    today = now.replace(hour=0, minute=0, second=0, microsecond=0)

    # Production orders: back to back on every line, from this week's Monday
    # (the board opens on the week) to 5 days ahead.
    order_no = 1140
    orders = []
    monday = today - timedelta(days=today.weekday())
    for line, cfg in LINES.items():
        t = monday + timedelta(hours=6) - timedelta(hours=rnd.choice([0, 8, 16]))
        index = rnd.randint(0, 1)
        while t < today + timedelta(days=5):
            _, name, weight = cfg["articles"][index % len(cfg["articles"])]
            index += 1
            hours = rnd.choice([16, 24, 32, 40])
            end = t + timedelta(hours=hours)
            quantity = int(cfg["rate"] * 60 * hours * 0.8 / 1000) * 1000
            order_no += 1
            orders.append((f"OF-26-{order_no}", line, name, weight, quantity, fmt(t), fmt(end),
                           1 if end < now else 0, None))
            t = end + timedelta(hours=rnd.choice([0, 0, 2]))
    conn.executemany(
        "INSERT INTO PlanProductionOrders (OrderNo, MachineId, Product, TargetWeightGrams, QuantityBags,"
        " PlannedStart, PlannedEnd, Status, Comment) VALUES (?,?,?,?,?,?,?,?,?)", orders)

    # Preventive maintenance: calendar and usage rules, some due this week.
    tasks = [("Graissage convoyeur", 0, 7, 0, 30), ("Contrôle mâchoires de soudure", 0, 14, 0, 45),
             ("Étalonnage peseuse", 0, 30, 0, 60), ("Remplacement téflon mâchoires", 1, 0, 1_500_000, 40)]
    plan_id = 0
    events = []
    for i, line in enumerate(LINES):
        for j, (task, kind, days, bags, minutes) in enumerate(tasks):
            if (i + j) % 2 and kind == 0 and days != 7:
                continue
            plan_id += 1
            last = today - timedelta(days=(i * 3 + j * 5) % max(days, 12) + 1, hours=-9)
            conn.execute(
                "INSERT INTO PlanMaintenancePlans (MachineId, Task, Kind, IntervalDays, IntervalBags, DurationMinutes,"
                " LastDoneAt, IsActive) VALUES (?,?,?,?,?,?,?,1)", (line, task, kind, days, bags, minutes, fmt(last)))
            if kind == 0:
                events.append((plan_id, line, task, fmt(last), minutes, 1, fmt(last + timedelta(minutes=minutes)),
                               rnd.choice(TECHNICIANS), None, 0))
                due = last + timedelta(days=days)
                if today <= due < today + timedelta(days=6):
                    events.append((plan_id, line, task, fmt(due), minutes, 0, None, None, None, 0))
    # One-off slots: a maintenance later today, the cleaning stop running now,
    # a line with no order tomorrow.
    events += [
        (None, "CC02", "Changement courroie doseur", fmt(now.replace(minute=0) + timedelta(hours=2)), 60, 0, None, None, None, 0),
        (None, "PC01", "Contrôle détecteur de métaux", fmt(today + timedelta(days=1, hours=8)), 45, 0, None, None, None, 0),
        (None, "CC04", "Nettoyage planifié", fmt(now - timedelta(minutes=52)), 120, 0, None, None, None, 1),
        (None, "PL03", "Pas de commande", fmt(today + timedelta(days=2, hours=6)), 480, 0, None, None, None, 1),
    ]
    conn.executemany(
        "INSERT INTO PlanMaintenanceEvents (PlanId, MachineId, Task, PlannedStart, DurationMinutes, Status, DoneAt,"
        " Technician, Comment, Kind) VALUES (?,?,?,?,?,?,?,?,?,?)", events)


def live(out_path, seed):
    """One snapshot per line every minute, carrying on from the last one."""
    rnd = random.Random(seed + 1)
    micro = {}                                    # line -> minutes of micro-stop left
    print("Lignes en production (Ctrl+C pour arrêter)...")
    while True:
        now = datetime.now().replace(second=0, microsecond=0)
        conn = sqlite3.connect(out_path, timeout=30)
        for line, rate, bias, until, weight_sum, lot_bags, lot_sum in conn.execute(
                "SELECT * FROM DemoLiveState").fetchall():
            row = conn.execute(
                "SELECT ID_produzione, tempo_inizio, tempo_fine, codice_articolo, peso_netto_impostato,"
                " peso_max_registrato, peso_min_registrato, totale_confezioni, confezioni_sotto_toll, confezioni_ok,"
                " confezioni_sopra_toll, confezioni_indeterminate, deviazione_std, espulsi_metal FROM produzione"
                " WHERE numero_linea = ? ORDER BY tempo_fine DESC, progressivo DESC LIMIT 1", (line,)).fetchone()
            last = datetime.strptime(row[2], TS_FORMAT)
            minutes = int((now - last).total_seconds() // 60)
            if minutes < 1:
                continue
            cfg = LINES[line]
            target = row[4]
            counters = dict(tot=row[7], sotto=row[8], ok=row[9], sopra=row[10], indet=row[11], metal=row[13])
            # A long pause of this script: catch up minute by minute (10 at most).
            for k in range(max(1, minutes - 10), minutes + 1):
                t = last + timedelta(minutes=k)
                if until and fmt(t) > until:
                    until = None
                if micro.get(line, 0) > 0:
                    micro[line] -= 1
                    running = False
                elif until:
                    running = False
                else:
                    running = True
                    if rnd.random() < 0.012:
                        micro[line] = rnd.randint(1, 2)
                bags = bags_of(rnd, cfg, 1) if running else 0
                if bags:
                    add_bags(rnd, counters, bags)
                    avg = target + cfg["tare"].get(target, 0) + bias + rnd.gauss(0, 0.5)
                    weight_sum += avg * bags
                    lot_bags += bags
                    lot_sum += avg * bags
                if lot_bags and (lot_bags >= cfg["rate"] * 15 or not running):
                    conn.execute("INSERT INTO lotto VALUES (?,?,?,?)", (row[0], fmt(t), lot_bags, round(lot_sum / lot_bags, 2)))
                    lot_bags, lot_sum = 0, 0.0
                media = round(weight_sum / counters["tot"], 1) if counters["tot"] else 0
                conn.execute(SNAPSHOT_SQL, (
                    row[0], line, row[1], fmt(t), 0, row[3], "", 0, line, target, row[5], row[6],
                    counters["tot"], counters["sotto"], counters["ok"], counters["sopra"], counters["indet"],
                    media, row[12], counters["metal"]))
            conn.execute(
                "UPDATE DemoLiveState SET StoppedUntil = ?, WeightSum = ?, LotBags = ?, LotSum = ? WHERE MachineId = ?",
                (until, weight_sum, lot_bags, lot_sum, line))
        conn.commit()
        conn.close()
        time.sleep(max(1, 61 - datetime.now().second))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default=DEFAULT_OUT, help="database file to write (default: this folder's dev DB)")
    parser.add_argument("--live", action="store_true", help="keep adding one snapshot a minute to an existing demo DB")
    parser.add_argument("--seed", type=int, default=7, help="random seed")
    args = parser.parse_args()
    if args.live:
        live(args.out, args.seed)
    else:
        build(args.out, datetime.now().replace(second=0, microsecond=0), args.seed)
