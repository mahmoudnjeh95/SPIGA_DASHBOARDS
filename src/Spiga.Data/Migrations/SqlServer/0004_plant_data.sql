-- Version 4: what the application writes moves out of the production
-- database "stats", which is now only read (live machine data and the
-- reference tables IT installs with stiavelli_compat.sql). Planning of the
-- stops, sticky note, machine display names and the settings shared by the
-- PCs live here. Rows already in "stats" are copied once at the first start
-- of this version (LegacyImports records it).
-- Keep in step with Migrations/Sqlite/0004_plant_data.sql.

CREATE TABLE dbo.PlanStopQualifications (
    Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    MachineId   NVARCHAR(40)   NOT NULL,
    StopStart   DATETIME2(0)   NOT NULL,
    StopEnd     DATETIME2(0)   NOT NULL,
    ShiftDate   DATE           NOT NULL,
    ShiftId     INT            NOT NULL,
    Team        NVARCHAR(50)   NOT NULL,
    CauseCode   VARCHAR(20)    NOT NULL,
    Component   NVARCHAR(100)  NULL,
    Technician  NVARCHAR(100)  NULL,
    Comment     NVARCHAR(1000) NULL,
    IsManual    BIT            NOT NULL,
    CreatedAt   DATETIME2(0)   NOT NULL,
    ExpectedEnd DATETIME2(0)   NULL
);
CREATE INDEX IX_PlanStopQualifications_Start ON dbo.PlanStopQualifications (StopStart, MachineId);
GO

CREATE TABLE dbo.PlanMaintenancePlans (
    Id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    MachineId       NVARCHAR(40)  NOT NULL,
    Task            NVARCHAR(100) NOT NULL,
    Kind            INT           NOT NULL,
    IntervalDays    INT           NOT NULL,
    IntervalBags    BIGINT        NOT NULL,
    DurationMinutes INT           NOT NULL,
    LastDoneAt      DATETIME2(0)  NULL,
    IsActive        BIT           NOT NULL
);
GO

CREATE TABLE dbo.PlanMaintenanceEvents (
    Id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    PlanId          INT            NULL,
    MachineId       NVARCHAR(40)   NOT NULL,
    Task            NVARCHAR(100)  NOT NULL,
    PlannedStart    DATETIME2(0)   NOT NULL,
    DurationMinutes INT            NOT NULL,
    Status          INT            NOT NULL,
    DoneAt          DATETIME2(0)   NULL,
    Technician      NVARCHAR(100)  NULL,
    Comment         NVARCHAR(1000) NULL,
    Kind            INT            NOT NULL CONSTRAINT DF_PlanMaintenanceEvents_Kind DEFAULT 0
);
CREATE INDEX IX_PlanMaintenanceEvents_Start ON dbo.PlanMaintenanceEvents (PlannedStart);
GO

-- OrderNo unique: two PCs can no longer give the same number.
CREATE TABLE dbo.PlanProductionOrders (
    Id                INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    OrderNo           NVARCHAR(30)   NOT NULL CONSTRAINT UQ_PlanProductionOrders_OrderNo UNIQUE,
    MachineId         NVARCHAR(40)   NOT NULL,
    Product           NVARCHAR(100)  NOT NULL,
    TargetWeightGrams FLOAT          NOT NULL,
    QuantityBags      BIGINT         NOT NULL,
    PlannedStart      DATETIME2(0)   NOT NULL,
    PlannedEnd        DATETIME2(0)   NOT NULL,
    Status            INT            NOT NULL,
    Comment           NVARCHAR(1000) NULL
);
GO

-- Nominal rates, TRS targets, teams...: one row, versioned.
CREATE TABLE dbo.AppSharedSettings (
    Id        INT           NOT NULL PRIMARY KEY CHECK (Id = 1),
    Version   INT           NOT NULL,
    Json      NVARCHAR(MAX) NOT NULL,
    UpdatedAt DATETIME2(0)  NOT NULL,
    UpdatedBy NVARCHAR(100) NOT NULL
);
GO

-- The dashboard's sticky note, with who wrote it.
CREATE TABLE dbo.Notes (
    NoteId        INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Content       NVARCHAR(2000) NOT NULL,
    CreatedBy     NVARCHAR(40)   NOT NULL,
    CreatedByName NVARCHAR(100)  NOT NULL DEFAULT '',
    Workstation   NVARCHAR(100)  NOT NULL DEFAULT '',
    CreatedAt     DATETIME2(0)   NOT NULL,
    IsActive      BIT            NOT NULL
);
GO

-- Names shown for the machines (Paramètres), over stats.Machines.
CREATE TABLE dbo.MachineDisplayNames (
    MachineId NVARCHAR(40)  NOT NULL PRIMARY KEY,
    Name      NVARCHAR(100) NOT NULL,
    UpdatedAt DATETIME2(0)  NOT NULL,
    UpdatedBy NVARCHAR(40)  NOT NULL
);
GO

-- What was copied from "stats" (once per table).
CREATE TABLE dbo.LegacyImports (
    Name       VARCHAR(60)  NOT NULL PRIMARY KEY,
    ImportedAt DATETIME2(0) NOT NULL,
    CopiedRows INT          NOT NULL
);
