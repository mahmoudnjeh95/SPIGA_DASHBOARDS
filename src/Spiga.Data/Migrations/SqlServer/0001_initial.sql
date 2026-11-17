-- SPIGA Suivi Production, application database, version 1 (SQL Server).
-- Keep in step with Migrations/Sqlite/0001_initial.sql. Runs in the
-- application database (spiga_suivi by default), never in "stats".

CREATE TABLE dbo.Roles (
    Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Code        NVARCHAR(60)  NOT NULL UNIQUE,
    Name        NVARCHAR(60)  NOT NULL,
    Description NVARCHAR(400) NOT NULL DEFAULT '',
    IsSystem    BIT           NOT NULL DEFAULT 0,
    CreatedAt   DATETIME2(0)  NOT NULL,
    UpdatedAt   DATETIME2(0)  NOT NULL
);
GO

CREATE TABLE dbo.RolePermissions (
    RoleId  INT          NOT NULL REFERENCES dbo.Roles(Id) ON DELETE CASCADE,
    Module  VARCHAR(40)  NOT NULL,
    Actions INT          NOT NULL,
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, Module)
);
GO

CREATE TABLE dbo.Users (
    Id                 INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Login              NVARCHAR(40)  NOT NULL UNIQUE,
    DisplayName        NVARCHAR(100) NOT NULL,
    RoleId             INT           NOT NULL REFERENCES dbo.Roles(Id),
    PasswordHash       VARCHAR(200)  NOT NULL,
    MustChangePassword BIT           NOT NULL DEFAULT 1,
    IsActive           BIT           NOT NULL DEFAULT 1,
    FailedAttempts     INT           NOT NULL DEFAULT 0,
    LockedUntil        DATETIME2(0)  NULL,
    ScopeSectors       NVARCHAR(200) NOT NULL DEFAULT '',
    ScopeTeam          NVARCHAR(50)  NOT NULL DEFAULT '',
    ScopeMachines      NVARCHAR(400) NOT NULL DEFAULT '',
    Theme              VARCHAR(10)   NOT NULL DEFAULT 'light',
    LastLoginAt        DATETIME2(0)  NULL,
    PasswordChangedAt  DATETIME2(0)  NULL,
    CreatedAt          DATETIME2(0)  NOT NULL,
    UpdatedAt          DATETIME2(0)  NOT NULL
);
GO

CREATE TABLE dbo.AppSettings (
    [Key]     VARCHAR(100)  NOT NULL PRIMARY KEY,
    Value     NVARCHAR(MAX) NOT NULL,
    UpdatedAt DATETIME2(0)  NOT NULL,
    UpdatedBy NVARCHAR(40)  NOT NULL
);
GO

CREATE TABLE dbo.AuditLog (
    Id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    At          DATETIME2(0)  NOT NULL,
    UserLogin   NVARCHAR(40)  NOT NULL,
    Module      VARCHAR(40)   NOT NULL,
    Action      NVARCHAR(80)  NOT NULL,
    Target      NVARCHAR(200) NOT NULL DEFAULT '',
    Details     NVARCHAR(MAX) NOT NULL DEFAULT '',
    OldValue    NVARCHAR(MAX) NULL,
    NewValue    NVARCHAR(MAX) NULL,
    Workstation NVARCHAR(100) NOT NULL DEFAULT ''
);
CREATE INDEX IX_AuditLog_At ON dbo.AuditLog (At);
GO

CREATE TABLE dbo.WasteReasons (
    Id        INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Kind      VARCHAR(10)   NOT NULL,
    Label     NVARCHAR(100) NOT NULL,
    IsActive  BIT           NOT NULL DEFAULT 1,
    SortOrder INT           NOT NULL DEFAULT 0
);
GO

CREATE TABLE dbo.WasteEntries (
    Id             BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Kind           VARCHAR(10)    NOT NULL,
    ProductionDate DATETIME2(0)   NOT NULL,
    ShiftId        TINYINT        NOT NULL,
    Team           NVARCHAR(50)   NOT NULL DEFAULT '',
    Sector         VARCHAR(20)    NOT NULL,
    MachineId      VARCHAR(10)    NULL,
    Quantity       DECIMAL(12,3)  NOT NULL,
    Unit           VARCHAR(5)     NOT NULL,
    Reason         NVARCHAR(100)  NOT NULL,
    Comment        NVARCHAR(500)  NOT NULL DEFAULT '',
    Status         VARCHAR(12)    NOT NULL,
    CreatedBy      NVARCHAR(40)   NOT NULL,
    CreatedAt      DATETIME2(0)   NOT NULL,
    UpdatedBy      NVARCHAR(40)   NULL,
    UpdatedAt      DATETIME2(0)   NULL,
    ValidatedBy    NVARCHAR(40)   NULL,
    ValidatedAt    DATETIME2(0)   NULL,
    IsDeleted      BIT            NOT NULL DEFAULT 0,
    DeletedBy      NVARCHAR(40)   NULL,
    DeletedAt      DATETIME2(0)   NULL
);
CREATE INDEX IX_WasteEntries_Date ON dbo.WasteEntries (ProductionDate, Kind);
GO
