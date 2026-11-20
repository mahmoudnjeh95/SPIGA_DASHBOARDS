-- Version 7: pointage of the packaging service.
-- * StaffEmployees / StaffPostings: the employees, and where each works from
--   a date on (team, function, machine or ligne). A transfer adds a posting.
-- * StaffLines: the lignes (sets of machines) of the chefs de ligne.
-- * AttendanceSheets / AttendanceLines: one sheet per team and poste, one
--   row per person (present, absent with a code, renfort, replacement).
-- * AttendanceRevisions: every save of every sheet, with what changed.
-- Keep in step with Migrations/SqlServer/0007_pointage.sql.
CREATE TABLE StaffLines (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    Name     TEXT    NOT NULL,
    Machines TEXT    NOT NULL DEFAULT '',
    IsActive INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE StaffEmployees (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Matricule TEXT    NOT NULL DEFAULT '',
    FullName  TEXT    NOT NULL,
    IsActive  INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE StaffPostings (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    EmployeeId INTEGER NOT NULL REFERENCES StaffEmployees(Id),
    ValidFrom  TEXT    NOT NULL,
    Team       TEXT    NOT NULL,
    Function   TEXT    NOT NULL,
    MachineId  TEXT    NULL,
    LineId     INTEGER NULL,
    CreatedBy  TEXT    NOT NULL,
    CreatedAt  TEXT    NOT NULL
);
CREATE UNIQUE INDEX UX_StaffPostings ON StaffPostings (EmployeeId, ValidFrom);

CREATE TABLE AttendanceSheets (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    ProductionDate TEXT    NOT NULL,
    ShiftId        INTEGER NOT NULL,
    Team           TEXT    NOT NULL,
    Status         TEXT    NOT NULL,
    Version        INTEGER NOT NULL DEFAULT 1,
    CreatedBy      TEXT    NOT NULL,
    CreatedAt      TEXT    NOT NULL,
    UpdatedBy      TEXT    NULL,
    UpdatedAt      TEXT    NULL,
    ValidatedBy    TEXT    NULL,
    ValidatedAt    TEXT    NULL
);
CREATE UNIQUE INDEX UX_AttendanceSheets ON AttendanceSheets (ProductionDate, ShiftId, Team);

CREATE TABLE AttendanceLines (
    Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    SheetId            INTEGER NOT NULL REFERENCES AttendanceSheets(Id),
    EmployeeId         INTEGER NOT NULL,
    FullName           TEXT    NOT NULL,
    Function           TEXT    NOT NULL,
    HomeTeam           TEXT    NOT NULL,
    IsRenfort          INTEGER NOT NULL DEFAULT 0,
    Status             TEXT    NOT NULL,
    Absence            TEXT    NULL,
    EntryAt            TEXT    NULL,
    ExitAt             TEXT    NULL,
    MachineId          TEXT    NULL,
    LineId             INTEGER NULL,
    ReplacesEmployeeId INTEGER NULL,
    ActingFunction     TEXT    NULL,
    ActingMachineId    TEXT    NULL,
    ActingLineId       INTEGER NULL,
    ActingFrom         TEXT    NULL
);
CREATE INDEX IX_AttendanceLines_Sheet ON AttendanceLines (SheetId);
CREATE INDEX IX_AttendanceLines_Employee ON AttendanceLines (EmployeeId);

CREATE TABLE AttendanceRevisions (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    SheetId   INTEGER NOT NULL,
    Version   INTEGER NOT NULL,
    At        TEXT    NOT NULL,
    UserLogin TEXT    NOT NULL,
    Action    TEXT    NOT NULL,
    Changes   TEXT    NOT NULL DEFAULT '',
    Note      TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IX_AttendanceRevisions_Sheet ON AttendanceRevisions (SheetId, Version);

-- The roles made from the templates get the new rights of their template.
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT r.Id, p.Permission FROM Roles r
JOIN (SELECT 'chef-equipe' AS Code, 'pointage:voir' AS Permission
      UNION ALL SELECT 'chef-equipe', 'pointage:saisir'
      UNION ALL SELECT 'chef-equipe', 'pointage:corriger'
      UNION ALL SELECT 'resp-production', 'pointage:voir'
      UNION ALL SELECT 'resp-production', 'pointage:saisir'
      UNION ALL SELECT 'resp-production', 'pointage:corriger'
      UNION ALL SELECT 'resp-production', 'pointage:anciens'
      UNION ALL SELECT 'resp-production', 'referentiels:saisir'
      UNION ALL SELECT 'resp-production', 'rh-nominatif:voir'
      UNION ALL SELECT 'resp-production', 'rh-nominatif:polyvalence'
      UNION ALL SELECT 'resp-rh', 'rh-nominatif:polyvalence'
      UNION ALL SELECT 'resp-rh', 'pointage:voir'
      UNION ALL SELECT 'resp-rh', 'referentiels:voir') p ON p.Code = r.Code;
