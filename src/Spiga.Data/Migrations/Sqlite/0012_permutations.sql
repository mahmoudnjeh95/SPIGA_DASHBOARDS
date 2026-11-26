-- Version 12: permutations of the pointage. Two people of two teams swap
-- their postes for one production day (A works B's poste, B works A's);
-- each sheet applies it when it opens. Cancelled ones are kept (who, when).
-- The rules of « À traiter » and of the permutation are settings
-- (vue-ensemble.a-traiter, pointage.permutation), changed with the new right
-- vue-ensemble:regles, given to the roles that manage overtime (the
-- production managers).
-- Keep in step with Migrations/SqlServer/0012_permutations.sql.
CREATE TABLE AttendancePermutations (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    ProductionDate TEXT    NOT NULL,
    EmployeeA      INTEGER NOT NULL REFERENCES StaffEmployees(Id),
    NameA          TEXT    NOT NULL,
    TeamA          TEXT    NOT NULL,
    ShiftA         INTEGER NOT NULL,
    EmployeeB      INTEGER NOT NULL REFERENCES StaffEmployees(Id),
    NameB          TEXT    NOT NULL,
    TeamB          TEXT    NOT NULL,
    ShiftB         INTEGER NOT NULL,
    CreatedBy      TEXT    NOT NULL,
    CreatedAt      TEXT    NOT NULL,
    CancelledBy    TEXT    NULL,
    CancelledAt    TEXT    NULL
);
CREATE INDEX IX_AttendancePermutations_Date ON AttendancePermutations (ProductionDate);

INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT RoleId, 'vue-ensemble:regles' FROM RoleGrants WHERE Permission = 'pointage:heures-sup';
