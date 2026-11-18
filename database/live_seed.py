"""
Keeps the SQLite dev database (spiga_dashboards.db) alive: every CADENCE
seconds it appends one Stiavelli snapshot per line, continuing the line's
latest production in `produzione` (cumulative counters, running average,
min/max), plus `lotto` rows, as if the check-weighers were writing. The
dashboard then shows connected lines, moving counters, stops and
changeovers in real time instead of a frozen copy.

Same lines, articles, speeds and weights as generate_seed_data.py
--synthetic, which builds the history first. Start-up fills the gap since
the last snapshot (the PC was off, the script was not running), up to
--max-gap hours; beyond that, regenerate the history.

Run from the repo root (Ctrl+C stops it):
    python database/live_seed.py [--cadence 60] [--seed N] [--while-pid PID [PID ...]]
--while-pid: stop when all these processes have exited (run-dev.ps1 passes the app's).
"""
import argparse
import importlib.util
import os
import random
import sqlite3
import sys
import time
from datetime import datetime, timedelta

HERE = os.path.dirname(os.path.abspath(__file__))
DB_PATH = os.path.join(HERE, "spiga_dashboards.db")

# One definition of the plant: the generator's constants.
sys.dont_write_bytecode = True  # no __pycache__ beside the database
_spec = importlib.util.spec_from_file_location("seed", os.path.join(HERE, "generate_seed_data.py"))
seed = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(seed)
TS = seed.TS_FORMAT
INSERT = (
    "INSERT INTO produzione (ID_produzione, numero_linea, tempo_inizio, tempo_fine, terminata, codice_articolo,"
    " operatore, turno, linea_produzione, peso_netto_impostato, peso_max_registrato, peso_min_registrato,"
    " totale_confezioni, confezioni_sotto_toll, confezioni_ok, confezioni_sopra_toll, confezioni_indeterminate,"
    " media_totale, deviazione_std, espulsi_metal) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
)


class Line:
    """One check-weigher: its current production and what it is doing."""

    def __init__(self, line, rnd, row):
        self.line, self.rnd = line, rnd
        self.articles, self.efficiency = seed.SYNTH_LINES[line]
        self.running, self.stop_left, self.idle_until = True, 0, None
        self.lot_bags, self.lot_sum = 0, 0.0
        if row is None or row["terminata"]:
            # No production running: the next one starts now.
            self.last = datetime.fromisoformat(row["tempo_fine"]) if row else datetime.now()
            self._new_production(self.last, self._next_article(row))
            return
        self.prod_id = row["ID_produzione"]
        self.prod_start = datetime.fromisoformat(row["tempo_inizio"])
        self.code = row["codice_articolo"]
        self.target = int(row["peso_netto_impostato"])
        self.last = datetime.fromisoformat(row["tempo_fine"])
        self.c = dict(tot=row["totale_confezioni"], sotto=row["confezioni_sotto_toll"], ok=row["confezioni_ok"],
                      sopra=row["confezioni_sopra_toll"], indet=row["confezioni_indeterminate"],
                      metal=row["espulsi_metal"] or 0)
        self.weight_sum = (row["media_totale"] or 0) * self.c["tot"]
        self.pmin, self.pmax = row["peso_min_registrato"], row["peso_max_registrato"]
        self.bias = (row["media_totale"] - self.target) if row["media_totale"] else rnd.uniform(-1.5, 6.0)
        # A run lasts 20 to 72 h, like the history.
        self.prod_end = self.prod_start + timedelta(hours=rnd.randint(20, 72))
        if self.prod_end <= self.last:
            self.prod_end = self.last + timedelta(minutes=rnd.randint(10, 120))

    def _next_article(self, row):
        if row is None:
            return self.articles[0]
        code, weight = row["codice_articolo"], row["peso_netto_impostato"]
        for i, (c, _, w) in enumerate(self.articles):
            if seed.synth_code(c) == code and w == weight:
                return self.articles[(i + 1) % len(self.articles)]
        return self.articles[0]

    def _new_production(self, at, article):
        code, _, target = article
        self.code, self.target = seed.synth_code(code), target
        self.prod_start = at.replace(microsecond=0)
        self.prod_id = f"{self.line}{self.code}{self.prod_start:%d.%m.%Y-%H:%M:%S}"
        self.prod_end = self.prod_start + timedelta(hours=self.rnd.randint(20, 72))
        self.c = dict(tot=0, sotto=0, ok=0, sopra=0, indet=0, metal=0)
        self.weight_sum, self.pmin, self.pmax = 0.0, None, None
        self.bias = self.rnd.uniform(-1.5, 6.0)
        self.running, self.stop_left = True, 0

    def step(self, t, minutes):
        """Snapshot at t (minutes since the previous one); returns (rows, lots)."""
        rnd = self.rnd
        rows, lots = [], []
        if self.idle_until:                      # changeover: nothing written
            if t < self.idle_until:
                return rows, lots
            self.idle_until = None
            article = next(a for a in self.articles if (seed.synth_code(a[0]), a[2]) != (self.code, self.target)) \
                if len(self.articles) > 1 else self.articles[0]
            self._new_production(t, article)
        nominal = seed.SYNTH_NOMINAL.get(self.target, 30)
        # Stops: same odds per 5 min as the history, scaled to the cadence.
        if self.stop_left > 0:
            self.stop_left -= minutes
            self.running = self.stop_left <= 0
        elif rnd.random() < (0.012 if 6 <= t.hour < 22 else 0.02) * minutes / 5:
            self.stop_left = rnd.choice([1, 1, 2, 3, 6, 12]) * 5
            self.running = False
        bags = int(nominal * self.efficiency * rnd.uniform(0.85, 1.08) * minutes) if self.running else 0
        if bags:
            under, over = int(bags * rnd.uniform(0.002, 0.012)), int(bags * rnd.uniform(0.002, 0.010))
            indet = int(bags * rnd.uniform(0, 0.001))
            c = self.c
            c["sotto"] += under; c["sopra"] += over; c["indet"] += indet
            c["ok"] += bags - under - over - indet; c["tot"] += bags
            c["metal"] += 1 if rnd.random() < 0.01 * minutes / 5 else 0
            avg = self.target + self.bias + rnd.gauss(0, 0.8)
            self.weight_sum += avg * bags
            self.lot_bags += bags
            self.lot_sum += avg * bags
            self.pmin = min(self.pmin or self.target, self.target * rnd.uniform(0.965, 0.985))
            self.pmax = max(self.pmax or self.target, self.target * rnd.uniform(1.02, 1.04))
        done = t >= self.prod_end
        if self.lot_bags and (self.lot_bags >= nominal * 15 or not self.running or done):
            lots.append((self.prod_id, t.strftime(TS), self.lot_bags, round(self.lot_sum / self.lot_bags, 2)))
            self.lot_bags, self.lot_sum = 0, 0.0
        c = self.c
        media = round(self.weight_sum / c["tot"], 1) if c["tot"] else 0
        rows.append((self.prod_id, self.line, self.prod_start.strftime(TS), t.strftime(TS), 1 if done else 0,
                     self.code, "", 0, self.line, self.target, self.pmax, self.pmin, c["tot"], c["sotto"], c["ok"],
                     c["sopra"], c["indet"], media, round(rnd.uniform(1.5, 3.5), 2), c["metal"]))
        if done:                                 # changeover: 20 to 60 min
            self.idle_until = t + timedelta(minutes=rnd.randint(20, 60))
        return rows, lots


def latest_rows(conn):
    conn.row_factory = sqlite3.Row
    out = {}
    for line in seed.SYNTH_LINES:
        out[line] = conn.execute(
            "SELECT * FROM produzione WHERE numero_linea = ? ORDER BY tempo_fine DESC, progressivo DESC LIMIT 1",
            (line,)).fetchone()
    conn.row_factory = None
    return out


def write(conn, rows, lots):
    for attempt in range(10):                    # the app may be reading
        try:
            with conn:
                conn.executemany(INSERT, rows)
                if lots:
                    conn.executemany("INSERT INTO lotto VALUES (?,?,?,?)", lots)
            return
        except sqlite3.OperationalError as e:
            if "locked" not in str(e) or attempt == 9:
                raise
            time.sleep(0.5)


def alive(pids):
    return pids is None or any(alive_one(p) for p in pids)


def alive_one(pid):
    if os.name == "nt":
        import ctypes
        h = ctypes.windll.kernel32.OpenProcess(0x1000 | 0x00100000, False, pid)  # QUERY_LIMITED | SYNCHRONIZE
        if not h:
            return False
        try:
            return ctypes.windll.kernel32.WaitForSingleObject(h, 0) == 0x102     # WAIT_TIMEOUT = still running
        finally:
            ctypes.windll.kernel32.CloseHandle(h)
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cadence", type=int, default=60, metavar="SEC", help="seconds between snapshots (default 60)")
    ap.add_argument("--max-gap", type=float, default=48, metavar="H", help="longest gap filled at start-up (hours)")
    ap.add_argument("--seed", type=int, default=None, help="random seed (default: different each run)")
    ap.add_argument("--while-pid", type=int, nargs="+", default=None, metavar="PID", help="stop when these processes have all exited")
    args = ap.parse_args()
    if not os.path.exists(DB_PATH):
        raise SystemExit(f"{DB_PATH} not found: run generate_seed_data.py --synthetic 30 first")

    rnd = random.Random(args.seed)
    conn = sqlite3.connect(DB_PATH, timeout=30)
    lines = {name: Line(name, rnd, row) for name, row in latest_rows(conn).items()}
    step = timedelta(seconds=args.cadence)
    now = datetime.now().replace(microsecond=0)

    # Catch up: the snapshots that would have been written while nobody ran this.
    oldest = min(l.last for l in lines.values())
    if now - oldest > timedelta(hours=args.max_gap):
        raise SystemExit(f"Last snapshot {oldest:%d/%m %H:%M} is more than {args.max_gap} h old: "
                         "regenerate the history (generate_seed_data.py --synthetic 30).")
    filled = 0
    for line in lines.values():
        t, rows, lots = line.last, [], []
        catch_step = timedelta(minutes=5) if now - t > timedelta(hours=2) else step
        while t + catch_step <= now:
            t += catch_step
            r, lo = line.step(t, catch_step.total_seconds() / 60)
            rows += r; lots += lo
        write(conn, rows, lots)
        filled += len(rows)
    print(f"{DB_PATH}: caught up {filled} snapshots to {now:%H:%M:%S}; "
          f"live every {args.cadence} s (Ctrl+C to stop)", flush=True)

    minutes = args.cadence / 60
    next_tick = time.monotonic() + args.cadence
    try:
        while alive(args.while_pid):
            time.sleep(max(0.0, min(1.0, next_tick - time.monotonic())))
            if time.monotonic() < next_tick:
                continue
            next_tick += args.cadence
            t = datetime.now().replace(microsecond=0)
            rows, lots = [], []
            for line in lines.values():
                r, lo = line.step(t, minutes)
                rows += r; lots += lo
            write(conn, rows, lots)
            running = sum(1 for l in lines.values() if l.running and not l.idle_until)
            print(f"{t:%H:%M:%S}  {len(rows)} snapshots, {len(lots)} lots, {running}/{len(lines)} lines running",
                  flush=True)
    except KeyboardInterrupt:
        pass
    conn.close()
    print("live seed stopped")


if __name__ == "__main__":
    main()
