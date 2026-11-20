-- Version 7: pointage of the packaging service (see the SQLite script for the details).
CREATE TABLE dbo.StaffLines (
    Id       INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name     NVARCHAR(40)  NOT NULL,
    Machines NVARCHAR(400) NOT NULL DEFAULT '',
    IsActive BIT           NOT NULL DEFAULT 1
);
GO

CREATE TABLE dbo.StaffEmployees (
    Id        INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Matricule NVARCHAR(20) NOT NULL DEFAULT '',
    FullName  NVARCHAR(80) NOT NULL,
    IsActive  BIT          NOT NULL DEFAULT 1
);
GO

CREATE TABLE dbo.StaffPostings (
    Id         BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EmployeeId INT          NOT NULL REFERENCES dbo.StaffEmployees(Id),
    ValidFrom  DATETIME2(0) NOT NULL,
    Team       NVARCHAR(50) NOT NULL,
    [Function] VARCHAR(24)  NOT NULL,
    MachineId  VARCHAR(10)  NULL,
    LineId     INT          NULL,
    CreatedBy  NVARCHAR(40) NOT NULL,
    CreatedAt  DATETIME2(0) NOT NULL
);
CREATE UNIQUE INDEX UX_StaffPostings ON dbo.StaffPostings (EmployeeId, ValidFrom);
GO

CREATE TABLE dbo.AttendanceSheets (
    Id             BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ProductionDate DATETIME2(0) NOT NULL,
    ShiftId        TINYINT      NOT NULL,
    Team           NVARCHAR(50) NOT NULL,
    Status         VARCHAR(12)  NOT NULL,
    Version        INT          NOT NULL DEFAULT 1,
    CreatedBy      NVARCHAR(40) NOT NULL,
    CreatedAt      DATETIME2(0) NOT NULL,
    UpdatedBy      NVARCHAR(40) NULL,
    UpdatedAt      DATETIME2(0) NULL,
    ValidatedBy    NVARCHAR(40) NULL,
    ValidatedAt    DATETIME2(0) NULL
);
CREATE UNIQUE INDEX UX_AttendanceSheets ON dbo.AttendanceSheets (ProductionDate, ShiftId, Team);
GO

CREATE TABLE dbo.AttendanceLines (
    Id                 BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    SheetId            BIGINT       NOT NULL REFERENCES dbo.AttendanceSheets(Id),
    EmployeeId         INT          NOT NULL,
    FullName           NVARCHAR(80) NOT NULL,
    [Function]         VARCHAR(24)  NOT NULL,
    HomeTeam           NVARCHAR(50) NOT NULL,
    IsRenfort          BIT          NOT NULL DEFAULT 0,
    Status             VARCHAR(12)  NOT NULL,
    Absence            VARCHAR(4)   NULL,
    EntryAt            DATETIME2(0) NULL,
    ExitAt             DATETIME2(0) NULL,
    MachineId          VARCHAR(10)  NULL,
    LineId             INT          NULL,
    ReplacesEmployeeId INT          NULL,
    ActingFunction     VARCHAR(24)  NULL,
    ActingMachineId    VARCHAR(10)  NULL,
    ActingLineId       INT          NULL,
    ActingFrom         DATETIME2(0) NULL
);
CREATE INDEX IX_AttendanceLines_Sheet ON dbo.AttendanceLines (SheetId);
CREATE INDEX IX_AttendanceLines_Employee ON dbo.AttendanceLines (EmployeeId);
GO

CREATE TABLE dbo.AttendanceRevisions (
    Id        BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    SheetId   BIGINT         NOT NULL,
    Version   INT            NOT NULL,
    At        DATETIME2(0)   NOT NULL,
    UserLogin NVARCHAR(40)   NOT NULL,
    Action    NVARCHAR(60)   NOT NULL,
    Changes   NVARCHAR(MAX)  NOT NULL DEFAULT '',
    Note      NVARCHAR(300)  NOT NULL DEFAULT ''
);
CREATE INDEX IX_AttendanceRevisions_Sheet ON dbo.AttendanceRevisions (SheetId, Version);
GO

INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT r.Id, p.Permission FROM dbo.Roles r
JOIN (VALUES ('chef-equipe', 'pointage:voir'), ('chef-equipe', 'pointage:saisir'), ('chef-equipe', 'pointage:corriger'),
             ('resp-production', 'pointage:voir'), ('resp-production', 'pointage:saisir'), ('resp-production', 'pointage:corriger'),
             ('resp-production', 'pointage:anciens'), ('resp-production', 'referentiels:saisir'),
             ('resp-production', 'rh-nominatif:voir'), ('resp-production', 'rh-nominatif:polyvalence'),
             ('resp-rh', 'rh-nominatif:polyvalence'), ('resp-rh', 'pointage:voir'), ('resp-rh', 'referentiels:voir')) p(Code, Permission)
  ON p.Code = r.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = r.Id AND x.Permission = p.Permission);
GO
