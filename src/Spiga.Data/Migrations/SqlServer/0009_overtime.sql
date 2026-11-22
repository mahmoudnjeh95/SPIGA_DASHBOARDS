-- Version 9: heures supplémentaires (see the SQLite script for the details).
CREATE TABLE dbo.OvertimeDecisions (
    Id         BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EmployeeId INT           NOT NULL REFERENCES dbo.StaffEmployees(Id),
    Kind       VARCHAR(16)   NOT NULL,
    FromDate   DATETIME2(0)  NOT NULL,
    ToDate     DATETIME2(0)  NULL,
    Reason     NVARCHAR(200) NOT NULL DEFAULT '',
    CreatedBy  NVARCHAR(40)  NOT NULL,
    CreatedAt  DATETIME2(0)  NOT NULL,
    LiftedBy   NVARCHAR(40)  NULL,
    LiftedAt   DATETIME2(0)  NULL
);
CREATE INDEX IX_OvertimeDecisions_Employee ON dbo.OvertimeDecisions (EmployeeId);
GO

INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'pointage:heures-sup' FROM dbo.RoleGrants g
WHERE g.Permission = 'pointage:anciens'
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = g.RoleId AND x.Permission = 'pointage:heures-sup');
