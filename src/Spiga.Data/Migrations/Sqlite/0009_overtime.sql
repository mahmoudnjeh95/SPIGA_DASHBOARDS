-- Version 9: heures supplémentaires. OvertimeDecisions: who the chef de
-- production forbade overtime (Interdiction, from a date, until a date or
-- until lifted) and who may go over the monthly limit (Derogation, one
-- month). The limit itself is a setting (pointage.heures-sup). The roles
-- that point the old postes (the production managers) get the new right.
-- Keep in step with Migrations/SqlServer/0009_overtime.sql.
CREATE TABLE OvertimeDecisions (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    EmployeeId INTEGER NOT NULL REFERENCES StaffEmployees(Id),
    Kind       TEXT    NOT NULL,
    FromDate   TEXT    NOT NULL,
    ToDate     TEXT    NULL,
    Reason     TEXT    NOT NULL DEFAULT '',
    CreatedBy  TEXT    NOT NULL,
    CreatedAt  TEXT    NOT NULL,
    LiftedBy   TEXT    NULL,
    LiftedAt   TEXT    NULL
);
CREATE INDEX IX_OvertimeDecisions_Employee ON OvertimeDecisions (EmployeeId);

INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT RoleId, 'pointage:heures-sup' FROM RoleGrants WHERE Permission = 'pointage:anciens';
