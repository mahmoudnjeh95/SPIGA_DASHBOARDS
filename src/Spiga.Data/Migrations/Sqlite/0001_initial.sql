-- SPIGA Suivi Production, application database, version 1 (SQLite).
-- Keep in step with Migrations/SqlServer/0001_initial.sql.

CREATE TABLE Roles (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Code        TEXT    NOT NULL UNIQUE,
    Name        TEXT    NOT NULL,
    Description TEXT    NOT NULL DEFAULT '',
    IsSystem    INTEGER NOT NULL DEFAULT 0,
    IsActive    INTEGER NOT NULL DEFAULT 1,
    CreatedAt   TEXT    NOT NULL,
    UpdatedAt   TEXT    NOT NULL
);

-- One row per permission ("module:name", Spiga.Core PermissionCatalog).
CREATE TABLE RoleGrants (
    RoleId     INTEGER NOT NULL REFERENCES Roles(Id) ON DELETE CASCADE,
    Permission TEXT    NOT NULL,
    PRIMARY KEY (RoleId, Permission)
);

-- Delegated administration: roles the holders of RoleId may give.
CREATE TABLE RoleAssignables (
    RoleId           INTEGER NOT NULL REFERENCES Roles(Id) ON DELETE CASCADE,
    AssignableRoleId INTEGER NOT NULL REFERENCES Roles(Id) ON DELETE CASCADE,
    PRIMARY KEY (RoleId, AssignableRoleId)
);

CREATE TABLE Users (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    Login              TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    DisplayName        TEXT    NOT NULL,
    PasswordHash       TEXT    NOT NULL,
    MustChangePassword INTEGER NOT NULL DEFAULT 1,
    IsActive           INTEGER NOT NULL DEFAULT 1,
    FailedAttempts     INTEGER NOT NULL DEFAULT 0,
    LockedUntil        TEXT    NULL,
    Theme              TEXT    NOT NULL DEFAULT 'light',
    LastLoginAt        TEXT    NULL,
    PasswordChangedAt  TEXT    NULL,
    CreatedAt          TEXT    NOT NULL,
    UpdatedAt          TEXT    NOT NULL
);

-- A role given to a user over a scope (empty = no restriction), possibly
-- for a period. A user holds one or more.
CREATE TABLE UserRoles (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId        INTEGER NOT NULL REFERENCES Users(Id) ON DELETE CASCADE,
    RoleId        INTEGER NOT NULL REFERENCES Roles(Id),
    ScopeSectors  TEXT    NOT NULL DEFAULT '',
    ScopeTeam     TEXT    NOT NULL DEFAULT '',
    ScopeMachines TEXT    NOT NULL DEFAULT '',
    ValidFrom     TEXT    NULL,
    ValidTo       TEXT    NULL
);
CREATE INDEX IX_UserRoles_User ON UserRoles (UserId);

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
