-- SPIGA_DASHBOARDS - target production schema (SQL Server)
-- Mirrors the fields exposed by the Stiavelli Production Supervisor UI
-- (lot/batch stats per node) plus tables the dashboard itself needs
-- (status history, notes). The SQLite dev database in this folder
-- uses the same shape so the dashboard code can be pointed at either.

CREATE TABLE Machines (
    MachineId    VARCHAR(10)   NOT NULL PRIMARY KEY,   -- e.g. CC01, PC02, PL03
    MachineName  NVARCHAR(50)  NOT NULL,
    MachineType  VARCHAR(5)    NOT NULL,                -- CC / PC / PL
    Sector       VARCHAR(20)   NOT NULL,                -- Couscous / Pate courte / Pate longue (dashboard slide grouping)
    IPAddress    VARCHAR(15)   NOT NULL,
    IsActive     BIT           NOT NULL DEFAULT 1
);

CREATE TABLE Products (
    ProductId             INT IDENTITY(1,1) PRIMARY KEY,
    ProductName           NVARCHAR(100) NOT NULL,
    TargetWeightGrams     DECIMAL(10,2) NOT NULL,
    PackagingSize         NVARCHAR(20)  NOT NULL,
    NominalRatePerMinute  DECIMAL(10,2) NOT NULL DEFAULT 0  -- ideal cycle rate (bags/min), used as the Performance input for TRS
);

CREATE TABLE Employees (
    EmployeeId INT IDENTITY(1,1) PRIMARY KEY,
    FullName   NVARCHAR(100) NOT NULL,
    Role       NVARCHAR(30)  NOT NULL                   -- Operateur / Chef d'equipe / Manager
);

CREATE TABLE Shifts (
    ShiftId   INT         NOT NULL PRIMARY KEY,
    ShiftName NVARCHAR(20) NOT NULL,
    StartHour TINYINT     NOT NULL,
    EndHour   TINYINT     NOT NULL
);

CREATE TABLE ProductionLots (
    LotId               INT IDENTITY(1,1) PRIMARY KEY,
    MachineId           VARCHAR(10)   NOT NULL REFERENCES Machines(MachineId),
    ProductId           INT           NOT NULL REFERENCES Products(ProductId),
    LotNumber           INT           NOT NULL,
    ShiftId             INT           NOT NULL REFERENCES Shifts(ShiftId),
    EmployeeId           INT           NOT NULL REFERENCES Employees(EmployeeId),
    StartTime            DATETIME2     NOT NULL,
    EndTime               DATETIME2     NOT NULL,
    TargetNetWeight       DECIMAL(10,2) NOT NULL,        -- Poids net fixe
    MinWeight             DECIMAL(10,2) NOT NULL,        -- Poids minimum enregistre
    MaxWeight             DECIMAL(10,2) NOT NULL,        -- Poids maximum enregistre
    TotalBags             INT           NOT NULL,        -- Nombre total de sacs
    BagsUnderTolerance     INT           NOT NULL,        -- Sacs sous tolerance
    BagsInTolerance        INT           NOT NULL,        -- Sacs en tolerance
    BagsOverTolerance      INT           NOT NULL,        -- Sacs au-dessus de la tolerance
    BagsIndeterminate      INT           NOT NULL,        -- Sacs indetermines
    StdDev                 DECIMAL(10,4) NOT NULL,        -- Deviation standard
    AvgWeight               DECIMAL(10,2) NOT NULL,        -- Moyenne reelle
    CorrectedAvgWeight      DECIMAL(10,2) NOT NULL,        -- Moyenne corrigee
    MetalRejects             INT           NOT NULL,        -- Rejete par les detecteurs de metaux
    IsConform                 BIT           NOT NULL         -- Lot conforme
);

CREATE TABLE MachineStatusLog (
    LogId        BIGINT IDENTITY(1,1) PRIMARY KEY,
    MachineId    VARCHAR(10) NOT NULL REFERENCES Machines(MachineId),
    Timestamp    DATETIME2   NOT NULL,
    IsConnected  BIT         NOT NULL,
    BeltRunning  BIT         NOT NULL,                    -- Statut des bandes: MARCHE/ARRET
    AlarmActive  BIT         NOT NULL
);

CREATE TABLE Notes (
    NoteId              INT IDENTITY(1,1) PRIMARY KEY,
    Content              NVARCHAR(500) NOT NULL,
    CreatedByEmployeeId  INT           NOT NULL REFERENCES Employees(EmployeeId),
    CreatedAt             DATETIME2     NOT NULL,
    IsActive               BIT           NOT NULL DEFAULT 1
);

CREATE INDEX IX_ProductionLots_Machine_Start ON ProductionLots(MachineId, StartTime);
CREATE INDEX IX_MachineStatusLog_Machine_Timestamp ON MachineStatusLog(MachineId, Timestamp);
