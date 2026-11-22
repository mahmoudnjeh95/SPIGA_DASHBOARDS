-- Version 11: the hour (or day) buckets of a finished day, per machine, as
-- read from the production database "stats" (before the nominal rates and
-- empty bag weights of Paramètres, applied on reading). Read back instead of
-- the slow query while their Fingerprint still matches: snapshots and lots
-- of the day, planned stops and breaks, product table. Source: which
-- production database (hash of its server and name). Day: yyyymmdd. Size:
-- 0 hour, 1 day. Data: the rows, JSON, gzip. Only a cache: can be emptied.
-- Keep in step with Migrations/SqlServer/0011_production_days.sql.
CREATE TABLE ProductionDayCache (
    Source      TEXT    NOT NULL,
    MachineId   TEXT    NOT NULL,
    Day         INTEGER NOT NULL,
    Size        INTEGER NOT NULL,
    Weights     INTEGER NOT NULL,
    Fingerprint TEXT    NOT NULL,
    Data        BLOB    NOT NULL,
    SavedAt     TEXT    NOT NULL,
    PRIMARY KEY (Source, Day, Size, Weights, MachineId)
);
