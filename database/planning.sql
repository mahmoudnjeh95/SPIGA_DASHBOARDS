-- Tables of the Planification window (pannes, maintenance, production
-- orders), in the SPIGA Dashboards database (stats), beside the app's own
-- tables (Machines, Notes). The app creates them itself the first time the
-- window opens (PlanningRepository.EnsureSchemaAsync, same statements); run
-- this script once only if its SQL login is not allowed to CREATE TABLE.
-- Idempotent. The Stiavelli tables (produzione, lotto, ...) are not touched.

IF OBJECT_ID('dbo.PlanStopQualifications') IS NULL
CREATE TABLE dbo.PlanStopQualifications (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    MachineId VARCHAR(10) NOT NULL,
    StopStart DATETIME2(0) NOT NULL,
    StopEnd DATETIME2(0) NOT NULL,
    ShiftDate DATE NOT NULL,
    ShiftId INT NOT NULL,
    Team NVARCHAR(50) NOT NULL,
    CauseCode VARCHAR(20) NOT NULL,
    Component NVARCHAR(100) NULL,
    Technician NVARCHAR(100) NULL,
    Comment NVARCHAR(1000) NULL,
    IsManual BIT NOT NULL,
    CreatedAt DATETIME2(0) NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PlanStopQualifications_Start')
CREATE INDEX IX_PlanStopQualifications_Start ON dbo.PlanStopQualifications (StopStart, MachineId);
GO
IF OBJECT_ID('dbo.PlanMaintenancePlans') IS NULL
CREATE TABLE dbo.PlanMaintenancePlans (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    MachineId VARCHAR(10) NOT NULL,
    Task NVARCHAR(100) NOT NULL,
    Kind INT NOT NULL,                -- 0 calendar (IntervalDays), 1 usage (IntervalBags)
    IntervalDays INT NOT NULL,
    IntervalBags BIGINT NOT NULL,
    DurationMinutes INT NOT NULL,
    LastDoneAt DATETIME2(0) NULL,
    IsActive BIT NOT NULL);
GO
IF OBJECT_ID('dbo.PlanMaintenanceEvents') IS NULL
CREATE TABLE dbo.PlanMaintenanceEvents (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    PlanId INT NULL,
    MachineId VARCHAR(10) NOT NULL,
    Task NVARCHAR(100) NOT NULL,
    PlannedStart DATETIME2(0) NOT NULL,
    DurationMinutes INT NOT NULL,
    Status INT NOT NULL,              -- 0 planned, 1 done, 2 cancelled
    DoneAt DATETIME2(0) NULL,
    Technician NVARCHAR(100) NULL,
    Comment NVARCHAR(1000) NULL);
GO
IF OBJECT_ID('dbo.PlanProductionOrders') IS NULL
CREATE TABLE dbo.PlanProductionOrders (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    OrderNo NVARCHAR(30) NOT NULL,
    MachineId VARCHAR(10) NOT NULL,
    Product NVARCHAR(100) NOT NULL,
    TargetWeightGrams FLOAT NOT NULL,
    QuantityBags BIGINT NOT NULL,
    PlannedStart DATETIME2(0) NOT NULL,
    PlannedEnd DATETIME2(0) NOT NULL,
    Status INT NOT NULL,              -- 0 planned, 1 done, 2 cancelled
    Comment NVARCHAR(1000) NULL);
GO
