"""
Generates a SQLite dev database (spiga_dashboards.db) with a rolling
31-day window of realistic mock production data, ending "today", matching
the schema in schema.sql (SQL Server DDL). Used to develop the dashboard
while real DB access to the Stiavelli Production Supervisor database is
pending.

Run: python generate_seed_data.py
"""
import sqlite3
import random
from datetime import datetime, timedelta

random.seed(42)

DB_PATH = "spiga_dashboards.db"
# Rolling 31-day window ending "today" so Day/Week/Month views in the app
# (which compare against DateTime.Now) have real data out of the box.
END_DATE = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
NUM_DAYS = 31
START_DATE = END_DATE - timedelta(days=NUM_DAYS - 1)

# ---------------------------------------------------------------------------
# Reference data
# ---------------------------------------------------------------------------

# Real roster (IPs confirmed by client). CC=couscous, PC=pate courte, PL=pate longue.
MACHINES = [
    # id,    name,            type, sector,        ip
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

# Spiga = wheat spike -> semolina / flour / couscous / pasta producer
# NominalRatePerMinute is the midpoint of gen_lot()'s per-weight-band rate
# range below, so seeded Performance% lands close to (but not exactly) 100%.
PRODUCTS = [
    # name,                 target_g, size,   nominal_rate
    ("Semoule Fine", 1000.0, "1kg", 25.0),
    ("Semoule Fine", 5000.0, "5kg", 11.0),
    ("Semoule Grosse", 1000.0, "1kg", 25.0),
    ("Farine Superieure", 1000.0, "1kg", 25.0),
    ("Farine Complete", 1000.0, "1kg", 25.0),
    ("Couscous Moyen", 1000.0, "1kg", 25.0),
    ("Couscous Fin", 500.0, "500g", 33.0),
    ("Pates Spaghetti", 500.0, "500g", 33.0),
    ("Pates Coquillettes", 500.0, "500g", 33.0),
    ("Orge Perle", 1000.0, "1kg", 25.0),
]

# Poste hours per the client spec (06-14 / 14-22 / 22-06). EndHour is the
# real wall-clock hour (Nuit ends at 6, not "30") -- the day loop below
# detects EndHour <= StartHour and rolls the shift end to the next day.
SHIFTS = [
    (1, "Matin", 6, 14),
    (2, "Soir", 14, 22),
    (3, "Nuit", 22, 6),
]

FIRST_NAMES_M = [
    "Mohamed", "Ahmed", "Ali", "Karim", "Sami", "Walid", "Nizar", "Anis",
    "Fares", "Hatem", "Slim", "Bilel", "Aymen", "Rami", "Youssef", "Amine",
    "Khalil", "Chokri", "Mehdi", "Wassim", "Nabil", "Tarek", "Riadh",
    "Marwan", "Zied", "Hedi", "Moez", "Seif", "Oussama",
]
FIRST_NAMES_F = [
    "Amira", "Sana", "Rim", "Ines", "Nour", "Sabrine", "Emna", "Wafa",
    "Salma", "Yasmine", "Manel", "Asma", "Mouna", "Dorra", "Hela", "Rania",
    "Syrine", "Amel", "Fatma", "Meriem", "Nadia", "Olfa", "Sonia", "Imen",
]
LAST_NAMES = [
    "Ben Ali", "Trabelsi", "Gharbi", "Jlassi", "Bouazizi", "Chaabane",
    "Mansour", "Hammami", "Khelifi", "Sassi", "Belhaj", "Fendri", "Zouari",
    "Kacem", "Ayari", "Ben Salah", "Cherif", "Bouzid", "Nasri", "Rekik",
    "Dridi", "Bahri", "Ben Amor", "Guesmi", "Toumi", "Slama", "Baccouche",
    "Marzouki", "Chtioui", "Abidi",
]

def make_employees(n=18):
    names = set()
    employees = []
    while len(employees) < n:
        if random.random() < 0.6:
            first = random.choice(FIRST_NAMES_M)
        else:
            first = random.choice(FIRST_NAMES_F)
        last = random.choice(LAST_NAMES)
        full = f"{first} {last}"
        if full in names:
            continue
        names.add(full)
        employees.append(full)
    roles = []
    for i, name in enumerate(employees):
        if i == 0:
            roles.append((name, "Manager"))
        elif i in (1, 2, 3):
            roles.append((name, "Chef d'equipe"))
        else:
            roles.append((name, "Operateur"))
    return roles

EMPLOYEES = make_employees()

# ---------------------------------------------------------------------------
# Schema (SQLite-flavoured mirror of schema.sql)
# ---------------------------------------------------------------------------

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
    ProductName          TEXT NOT NULL,
    TargetWeightGrams    REAL NOT NULL,
    PackagingSize        TEXT NOT NULL,
    NominalRatePerMinute REAL NOT NULL DEFAULT 0
);

CREATE TABLE Employees (
    EmployeeId INTEGER PRIMARY KEY AUTOINCREMENT,
    FullName   TEXT NOT NULL,
    Role       TEXT NOT NULL
);

CREATE TABLE Shifts (
    ShiftId   INTEGER PRIMARY KEY,
    ShiftName TEXT NOT NULL,
    StartHour INTEGER NOT NULL,
    EndHour   INTEGER NOT NULL
);

CREATE TABLE ProductionLots (
    LotId              INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId          TEXT NOT NULL REFERENCES Machines(MachineId),
    ProductId          INTEGER NOT NULL REFERENCES Products(ProductId),
    LotNumber          INTEGER NOT NULL,
    ShiftId            INTEGER NOT NULL REFERENCES Shifts(ShiftId),
    EmployeeId         INTEGER NOT NULL REFERENCES Employees(EmployeeId),
    StartTime          TEXT NOT NULL,
    EndTime            TEXT NOT NULL,
    TargetNetWeight    REAL NOT NULL,
    MinWeight          REAL NOT NULL,
    MaxWeight          REAL NOT NULL,
    TotalBags          INTEGER NOT NULL,
    BagsUnderTolerance INTEGER NOT NULL,
    BagsInTolerance    INTEGER NOT NULL,
    BagsOverTolerance  INTEGER NOT NULL,
    BagsIndeterminate  INTEGER NOT NULL,
    StdDev             REAL NOT NULL,
    AvgWeight          REAL NOT NULL,
    CorrectedAvgWeight REAL NOT NULL,
    MetalRejects       INTEGER NOT NULL,
    IsConform          INTEGER NOT NULL
);

CREATE TABLE MachineStatusLog (
    LogId       INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId   TEXT NOT NULL REFERENCES Machines(MachineId),
    Timestamp   TEXT NOT NULL,
    IsConnected INTEGER NOT NULL,
    BeltRunning INTEGER NOT NULL,
    AlarmActive INTEGER NOT NULL
);

CREATE TABLE Notes (
    NoteId             INTEGER PRIMARY KEY AUTOINCREMENT,
    Content            TEXT NOT NULL,
    CreatedByEmployeeId INTEGER NOT NULL REFERENCES Employees(EmployeeId),
    CreatedAt          TEXT NOT NULL,
    IsActive           INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IX_ProductionLots_Machine_Start ON ProductionLots(MachineId, StartTime);
CREATE INDEX IX_MachineStatusLog_Machine_Timestamp ON MachineStatusLog(MachineId, Timestamp);
"""

# ---------------------------------------------------------------------------
# Data generation
# ---------------------------------------------------------------------------

def gen_lot(machine_id, product, shift_start, shift_end, lot_no):
    """One production lot within [shift_start, shift_end)."""
    product_name, target_weight, _size = product
    max_span = (shift_end - shift_start).total_seconds() / 60
    duration_min = random.randint(45, max(60, int(max_span // 2)))
    duration_min = min(duration_min, int(max_span))
    start = shift_start
    end = start + timedelta(minutes=duration_min)

    # smaller packages -> faster nominal rate (bags/min)
    if target_weight <= 500:
        rate = random.uniform(28, 38)
    elif target_weight <= 1000:
        rate = random.uniform(20, 30)
    else:
        rate = random.uniform(8, 14)

    total_bags = max(1, int(duration_min * rate))

    in_pct = random.uniform(0.90, 0.98)
    under_pct = random.uniform(0.005, (1 - in_pct) * 0.5)
    over_pct = random.uniform(0.005, (1 - in_pct) * 0.5)
    indet_pct = max(0.0, 1 - in_pct - under_pct - over_pct)

    bags_in = int(total_bags * in_pct)
    bags_under = int(total_bags * under_pct)
    bags_over = int(total_bags * over_pct)
    bags_indet = total_bags - bags_in - bags_under - bags_over

    std_dev = round(random.uniform(1.5, 6.5), 4)
    avg_weight = round(target_weight + random.uniform(-3, 3), 2)
    corrected_avg = round(avg_weight + random.uniform(-0.5, 0.5), 2)
    min_weight = round(target_weight - random.uniform(5, 15), 2)
    max_weight = round(target_weight + random.uniform(5, 15), 2)
    metal_rejects = random.choices([0, 1, 2, 3, 4], weights=[60, 20, 10, 6, 4])[0]
    is_conform = 1 if (bags_over + bags_under) / total_bags < 0.08 else 0

    return {
        "MachineId": machine_id,
        "ProductName": product_name,
        "TargetWeight": target_weight,
        "LotNumber": lot_no,
        "StartTime": start,
        "EndTime": end,
        "MinWeight": min_weight,
        "MaxWeight": max_weight,
        "TotalBags": total_bags,
        "BagsUnder": bags_under,
        "BagsIn": bags_in,
        "BagsOver": bags_over,
        "BagsIndet": bags_indet,
        "StdDev": std_dev,
        "AvgWeight": avg_weight,
        "CorrectedAvg": corrected_avg,
        "MetalRejects": metal_rejects,
        "IsConform": is_conform,
    }, end


def build():
    conn = sqlite3.connect(DB_PATH)
    cur = conn.cursor()
    cur.executescript(DDL)

    cur.executemany(
        "INSERT INTO Machines VALUES (?,?,?,?,?,1)", MACHINES
    )
    cur.executemany(
        "INSERT INTO Products (ProductName, TargetWeightGrams, PackagingSize, NominalRatePerMinute) VALUES (?,?,?,?)",
        PRODUCTS,
    )
    cur.executemany(
        "INSERT INTO Shifts VALUES (?,?,?,?)", SHIFTS
    )
    cur.executemany(
        "INSERT INTO Employees (FullName, Role) VALUES (?,?)", EMPLOYEES
    )
    conn.commit()

    employee_ids = [row[0] for row in cur.execute("SELECT EmployeeId FROM Employees")]
    product_rows = list(cur.execute("SELECT ProductId, ProductName, TargetWeightGrams, PackagingSize FROM Products"))

    lot_counter = {m[0]: 0 for m in MACHINES}
    lots_inserted = 0
    status_inserted = 0

    for day_offset in range(NUM_DAYS):
        date = START_DATE + timedelta(days=day_offset)
        is_sunday = date.weekday() == 6  # Monday=0 ... Sunday=6

        for machine_id, _name, _type, _sector, _ip in MACHINES:
            for shift_id, _shift_name, start_h, end_h in SHIFTS:
                shift_start = date + timedelta(hours=start_h)
                # Nuit (22h-06h) crosses midnight: EndHour <= StartHour means "next day".
                end_day_offset = 1 if end_h <= start_h else 0
                shift_end = date + timedelta(days=end_day_offset, hours=end_h)

                active_prob = 0.25 if is_sunday else 0.90
                active = random.random() < active_prob

                # status log: one snapshot per hour of the shift regardless
                cursor_time = shift_start
                while cursor_time < shift_end:
                    connected = 1 if random.random() < (0.97 if active else 0.6) else 0
                    belt_running = 1 if (active and connected and random.random() < 0.92) else 0
                    alarm = 1 if random.random() < 0.03 else 0
                    cur.execute(
                        "INSERT INTO MachineStatusLog (MachineId, Timestamp, IsConnected, BeltRunning, AlarmActive) "
                        "VALUES (?,?,?,?,?)",
                        (machine_id, cursor_time.isoformat(sep=" "), connected, belt_running, alarm),
                    )
                    status_inserted += 1
                    cursor_time += timedelta(hours=1)

                if not active:
                    continue

                n_lots = random.randint(1, 3)
                cursor_time = shift_start
                for _ in range(n_lots):
                    remaining = (shift_end - cursor_time).total_seconds() / 60
                    if remaining < 45:
                        break
                    product = random.choice(product_rows)
                    product_id, product_name, target_weight, _size = product
                    lot_counter[machine_id] += 1
                    lot, end_time = gen_lot(
                        machine_id,
                        (product_name, target_weight, _size),
                        cursor_time,
                        shift_end,
                        lot_counter[machine_id],
                    )
                    employee_id = random.choice(employee_ids)
                    cur.execute(
                        """INSERT INTO ProductionLots
                        (MachineId, ProductId, LotNumber, ShiftId, EmployeeId, StartTime, EndTime,
                         TargetNetWeight, MinWeight, MaxWeight, TotalBags, BagsUnderTolerance,
                         BagsInTolerance, BagsOverTolerance, BagsIndeterminate, StdDev, AvgWeight,
                         CorrectedAvgWeight, MetalRejects, IsConform)
                        VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                        (
                            machine_id, product_id, lot["LotNumber"], shift_id, employee_id,
                            lot["StartTime"].isoformat(sep=" "), lot["EndTime"].isoformat(sep=" "),
                            lot["TargetWeight"], lot["MinWeight"], lot["MaxWeight"], lot["TotalBags"],
                            lot["BagsUnder"], lot["BagsIn"], lot["BagsOver"], lot["BagsIndet"],
                            lot["StdDev"], lot["AvgWeight"], lot["CorrectedAvg"], lot["MetalRejects"],
                            lot["IsConform"],
                        ),
                    )
                    lots_inserted += 1
                    # small changeover gap before next lot
                    cursor_time = end_time + timedelta(minutes=random.randint(5, 20))

    manager_id = employee_ids[0]
    cur.execute(
        "INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive) VALUES (?,?,?,1)",
        (
            "Objectif du mois: reduire les rejets metal detector de 10%. "
            "Merci de respecter les procedures de nettoyage entre les lots.",
            manager_id,
            START_DATE.replace(hour=8, minute=0).isoformat(sep=" "),
        ),
    )

    conn.commit()
    conn.close()

    print(f"Machines: {len(MACHINES)}")
    print(f"Products: {len(PRODUCTS)}")
    print(f"Employees: {len(EMPLOYEES)}")
    print(f"ProductionLots: {lots_inserted}")
    print(f"MachineStatusLog: {status_inserted}")
    print(f"DB written to: {DB_PATH}")


if __name__ == "__main__":
    build()
