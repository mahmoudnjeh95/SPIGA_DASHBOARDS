-- Version 12: permutations of the pointage (see the SQLite script for the details).
CREATE TABLE dbo.AttendancePermutations (
    Id             BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ProductionDate DATETIME2(0)  NOT NULL,
    EmployeeA      INT           NOT NULL REFERENCES dbo.StaffEmployees(Id),
    NameA          NVARCHAR(80)  NOT NULL,
    TeamA          NVARCHAR(50)  NOT NULL,
    ShiftA         TINYINT       NOT NULL,
    EmployeeB      INT           NOT NULL REFERENCES dbo.StaffEmployees(Id),
    NameB          NVARCHAR(80)  NOT NULL,
    TeamB          NVARCHAR(50)  NOT NULL,
    ShiftB         TINYINT       NOT NULL,
    CreatedBy      NVARCHAR(40)  NOT NULL,
    CreatedAt      DATETIME2(0)  NOT NULL,
    CancelledBy    NVARCHAR(40)  NULL,
    CancelledAt    DATETIME2(0)  NULL
);
CREATE INDEX IX_AttendancePermutations_Date ON dbo.AttendancePermutations (ProductionDate);
GO

INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'vue-ensemble:regles' FROM dbo.RoleGrants g
WHERE g.Permission = 'pointage:heures-sup'
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = g.RoleId AND x.Permission = 'vue-ensemble:regles');
