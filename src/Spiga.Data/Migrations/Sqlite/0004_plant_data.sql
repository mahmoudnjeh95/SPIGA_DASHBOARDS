-- Version 4: what the application writes moves out of the production
-- database "stats", which is now only read (live machine data and the
-- reference tables IT installs with stiavelli_compat.sql). Planning of the
-- stops, sticky note, machine display names and the settings shared by the
-- PCs live here. Rows already in "stats" are copied once at the first start
-- of this version (LegacyImports records it).
-- Keep in step with Migrations/SqlServer/0004_plant_data.sql.

CREATE TABLE PlanStopQualifications (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId   TEXT    NOT NULL,
    StopStart   TEXT    NOT NULL,
    StopEnd     TEXT    NOT NULL,
    ShiftDate   TEXT    NOT NULL,
    ShiftId     INTEGER NOT NULL,
    Team        TEXT    NOT NULL,
    CauseCode   TEXT    NOT NULL,
    Component   TEXT    NULL,
    Technician  TEXT    NULL,
    Comment     TEXT    NULL,
    IsManual    INTEGER NOT NULL,
    CreatedAt   TEXT    NOT NULL,
    ExpectedEnd TEXT    NULL
);
CREATE INDEX IX_PlanStopQualifications_Start ON PlanStopQualifications (StopStart, MachineId);

CREATE TABLE PlanMaintenancePlans (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    MachineId       TEXT    NOT NULL,
    Task            TEXT    NOT NULL,
    Kind            INTEGER NOT NULL,
    IntervalDays    INTEGER NOT NULL,
    IntervalBags    INTEGER NOT NULL,
    DurationMinutes INTEGER NOT NULL,
    LastDoneAt      TEXT    NULL,
    IsActive        INTEGER NOT NULL
);

CREATE TABLE PlanMaintenanceEvents (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    PlanId          INTEGER NULL,
    MachineId       TEXT    NOT NULL,
    Task            TEXT    NOT NULL,
    PlannedStart    TEXT    NOT NULL,
    DurationMinutes INTEGER NOT NULL,
    Status          INTEGER NOT NULL,
    DoneAt          TEXT    NULL,
    Technician      TEXT    NULL,
    Comment         TEXT    NULL,
    Kind            INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_PlanMaintenanceEvents_Start ON PlanMaintenanceEvents (PlannedStart);

-- OrderNo unique: two PCs can no longer give the same number.
CREATE TABLE PlanProductionOrders (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    OrderNo           TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    MachineId         TEXT    NOT NULL,
    Product           TEXT    NOT NULL,
    TargetWeightGrams REAL    NOT NULL,
    QuantityBags      INTEGER NOT NULL,
    PlannedStart      TEXT    NOT NULL,
    PlannedEnd        TEXT    NOT NULL,
    Status            INTEGER NOT NULL,
    Comment           TEXT    NULL
);

-- Nominal rates, TRS targets, teams...: one row, versioned.
CREATE TABLE AppSharedSettings (
    Id        INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
    Version   INTEGER NOT NULL,
    Json      TEXT    NOT NULL,
    UpdatedAt TEXT    NOT NULL,
    UpdatedBy TEXT    NOT NULL
);

-- The dashboard's sticky note, with who wrote it.
CREATE TABLE Notes (
    NoteId        INTEGER PRIMARY KEY AUTOINCREMENT,
    Content       TEXT    NOT NULL,
    CreatedBy     TEXT    NOT NULL,
    CreatedByName TEXT    NOT NULL DEFAULT '',
    Workstation   TEXT    NOT NULL DEFAULT '',
    CreatedAt     TEXT    NOT NULL,
    IsActive      INTEGER NOT NULL
);

-- Names shown for the machines (Paramètres), over stats.Machines.
CREATE TABLE MachineDisplayNames (
    MachineId TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
    Name      TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UpdatedBy TEXT NOT NULL
);

-- What was copied from "stats" (once per table).
CREATE TABLE LegacyImports (
    Name       TEXT    NOT NULL PRIMARY KEY,
    ImportedAt TEXT    NOT NULL,
    CopiedRows INTEGER NOT NULL
);
