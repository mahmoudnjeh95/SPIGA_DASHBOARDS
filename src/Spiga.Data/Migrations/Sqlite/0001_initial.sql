-- SPIGA Suivi Production, application database, version 1 (SQLite).
-- Keep in step with Migrations/SqlServer/0001_initial.sql.

CREATE TABLE Roles (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Code        TEXT    NOT NULL UNIQUE,
    Name        TEXT    NOT NULL,
    Description TEXT    NOT NULL DEFAULT '',
    IsSystem    INTEGER NOT NULL DEFAULT 0,
    CreatedAt   TEXT    NOT NULL,
    UpdatedAt   TEXT    NOT NULL
);

CREATE TABLE RolePermissions (
    RoleId  INTEGER NOT NULL REFERENCES Roles(Id) ON DELETE CASCADE,
    Module  TEXT    NOT NULL,
    Actions INTEGER NOT NULL,
    PRIMARY KEY (RoleId, Module)
);

CREATE TABLE Users (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    Login              TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    DisplayName        TEXT    NOT NULL,
    RoleId             INTEGER NOT NULL REFERENCES Roles(Id),
    PasswordHash       TEXT    NOT NULL,
    MustChangePassword INTEGER NOT NULL DEFAULT 1,
    IsActive           INTEGER NOT NULL DEFAULT 1,
    FailedAttempts     INTEGER NOT NULL DEFAULT 0,
    LockedUntil        TEXT    NULL,
    ScopeSectors       TEXT    NOT NULL DEFAULT '',
    ScopeTeam          TEXT    NOT NULL DEFAULT '',
    ScopeMachines      TEXT    NOT NULL DEFAULT '',
    Theme              TEXT    NOT NULL DEFAULT 'light',
    LastLoginAt        TEXT    NULL,
    PasswordChangedAt  TEXT    NULL,
    CreatedAt          TEXT    NOT NULL,
    UpdatedAt          TEXT    NOT NULL
);

CREATE TABLE AppSettings (
    Key       TEXT NOT NULL PRIMARY KEY,
    Value     TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    UpdatedBy TEXT NOT NULL
);

CREATE TABLE AuditLog (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    At          TEXT NOT NULL,
    UserLogin   TEXT NOT NULL,
    Module      TEXT NOT NULL,
    Action      TEXT NOT NULL,
    Target      TEXT NOT NULL DEFAULT '',
    Details     TEXT NOT NULL DEFAULT '',
    OldValue    TEXT NULL,
    NewValue    TEXT NULL,
    Workstation TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IX_AuditLog_At ON AuditLog (At);

CREATE TABLE WasteReasons (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind      TEXT    NOT NULL,
    Label     TEXT    NOT NULL,
    IsActive  INTEGER NOT NULL DEFAULT 1,
    SortOrder INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE WasteEntries (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind           TEXT    NOT NULL,
    ProductionDate TEXT    NOT NULL,
    ShiftId        INTEGER NOT NULL,
    Team           TEXT    NOT NULL DEFAULT '',
    Sector         TEXT    NOT NULL,
    MachineId      TEXT    NULL,
    Quantity       REAL    NOT NULL,
    Unit           TEXT    NOT NULL,
    Reason         TEXT    NOT NULL,
    Comment        TEXT    NOT NULL DEFAULT '',
    Status         TEXT    NOT NULL,
    CreatedBy      TEXT    NOT NULL,
    CreatedAt      TEXT    NOT NULL,
    UpdatedBy      TEXT    NULL,
    UpdatedAt      TEXT    NULL,
    ValidatedBy    TEXT    NULL,
    ValidatedAt    TEXT    NULL,
    IsDeleted      INTEGER NOT NULL DEFAULT 0,
    DeletedBy      TEXT    NULL,
    DeletedAt      TEXT    NULL
);
CREATE INDEX IX_WasteEntries_Date ON WasteEntries (ProductionDate, Kind);
