/* =============================================================================
   SPIGA_DASHBOARDS - Stiavelli compatibility layer  (approach A)
   Target DB: stats  (SQL Server / SQLEXPRESS)

   Presents the dashboard's expected schema (schema.sql) on top of the real
   Stiavelli tables (produzione, lotto, codici, linee, operatori) WITHOUT
   touching the Stiavelli data:
     - Real, writable tables for what the app writes or the Stiavelli DB
       doesn't store: Machines, Shifts, Employees, Products, MachineStatusLog, Notes
     - A read-only VIEW  ProductionLots  = produzione (+ lotto when present)
   Idempotent: safe to re-run.
   ============================================================================= */
USE stats;
GO

/* ---------- Machines (real table; app issues UPDATE Machines) ------------- */
IF OBJECT_ID('dbo.Machines','U') IS NULL
CREATE TABLE dbo.Machines (
    MachineId   VARCHAR(10)  NOT NULL PRIMARY KEY,
    MachineName NVARCHAR(50) NOT NULL,
    MachineType VARCHAR(5)   NOT NULL,
    Sector      VARCHAR(20)  NOT NULL,   -- Couscous / Pate courte / Pate longue
    IPAddress   VARCHAR(15)  NOT NULL,
    IsActive    BIT          NOT NULL DEFAULT 1
);
GO
-- Widen Sector on pre-existing installs (was VARCHAR(10)).
IF COL_LENGTH('dbo.Machines','Sector') < 20
    ALTER TABLE dbo.Machines ALTER COLUMN Sector VARCHAR(20) NOT NULL;
GO
-- Real machine roster (IPs confirmed by the client). Full sync: inserts new,
-- fixes changed IPs/names, and removes any machine not in this list.
-- NOTE: MachineType is derived from the ID prefix; Sector is the dashboard
-- slide grouping -- adjust the Sector values if the real grouping differs.
MERGE dbo.Machines AS t
USING (VALUES
    ('CC01','Controle CC01','CC','Couscous','192.168.2.30'),
    ('CC02','Controle CC02','CC','Couscous','192.168.2.31'),
    ('CC03','Controle CC03','CC','Couscous','192.168.2.38'),
    ('CC04','Controle CC04','CC','Couscous','192.168.2.39'),
    ('PC01','Peseuse PC01','PC','Pate courte','192.168.2.35'),
    ('PC02','Peseuse PC02','PC','Pate courte','192.168.2.36'),
    ('PC03','Peseuse PC03','PC','Pate courte','192.168.2.37'),
    ('PL01','Ligne PL01','PL','Pate longue','192.168.2.32'),
    ('PL02','Ligne PL02','PL','Pate longue','192.168.2.33'),
    ('PL03','Ligne PL03','PL','Pate longue','192.168.2.34')
) AS s(MachineId,MachineName,MachineType,Sector,IPAddress)
ON t.MachineId = s.MachineId
WHEN MATCHED THEN
    UPDATE SET IPAddress = s.IPAddress, MachineType = s.MachineType, Sector = s.Sector
WHEN NOT MATCHED BY TARGET THEN
    INSERT (MachineId,MachineName,MachineType,Sector,IPAddress,IsActive)
    VALUES (s.MachineId,s.MachineName,s.MachineType,s.Sector,s.IPAddress,1)
WHEN NOT MATCHED BY SOURCE THEN
    DELETE;
GO

/* ---------- Shifts (real table; no Stiavelli source) --------------------- */
IF OBJECT_ID('dbo.Shifts','U') IS NULL
CREATE TABLE dbo.Shifts (
    ShiftId   INT          NOT NULL PRIMARY KEY,
    ShiftName NVARCHAR(20) NOT NULL,
    StartHour TINYINT      NOT NULL,
    EndHour   TINYINT      NOT NULL
);
GO
MERGE dbo.Shifts AS t
USING (VALUES (1,'Matin',6,14),(2,'Soir',14,22),(3,'Nuit',22,6)) AS s(ShiftId,ShiftName,StartHour,EndHour)
ON t.ShiftId = s.ShiftId
WHEN NOT MATCHED THEN INSERT (ShiftId,ShiftName,StartHour,EndHour) VALUES (s.ShiftId,s.ShiftName,s.StartHour,s.EndHour);
GO

/* ---------- Employees (real table) --------------------------------------
   Maps Stiavelli operator CODE (produzione.operatore, char(3)) -> int id.
   Seeded from operatori if populated, else from codes seen in produzione. */
IF OBJECT_ID('dbo.Employees','U') IS NULL
CREATE TABLE dbo.Employees (
    EmployeeId INT IDENTITY(1,1) PRIMARY KEY,
    Code       VARCHAR(3)    NOT NULL UNIQUE,   -- bridge to produzione.operatore
    FullName   NVARCHAR(100) NOT NULL,
    Role       NVARCHAR(30)  NOT NULL DEFAULT 'Operateur'
);
GO
INSERT INTO dbo.Employees (Code, FullName, Role)
SELECT src.Code,
       COALESCE(NULLIF(RTRIM(o.Descrizione),''), 'Operateur ' + src.Code),
       'Operateur'
FROM (
    SELECT DISTINCT RTRIM(operatore) AS Code FROM dbo.produzione
    WHERE operatore IS NOT NULL AND RTRIM(operatore) <> ''
) src
LEFT JOIN dbo.operatori o ON RTRIM(o.Codice) = src.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.Code = src.Code);
GO

/* ---------- Products (real table) ---------------------------------------
   Bridges Stiavelli article code -> int ProductId. ProductName from codici.
   TargetWeightGrams taken from the net weight seen in produzione.
   NominalRatePerMinute has no Stiavelli source -> 0 (set per article later;
   KpiCalculator treats 0 as "no performance target"). */
IF OBJECT_ID('dbo.Products','U') IS NULL
CREATE TABLE dbo.Products (
    ProductId            INT IDENTITY(1,1) PRIMARY KEY,
    Code                 VARCHAR(20)   NOT NULL UNIQUE,  -- normalized article code
    ProductName          NVARCHAR(100) NOT NULL,
    TargetWeightGrams    DECIMAL(10,2) NOT NULL DEFAULT 0,
    PackagingSize        NVARCHAR(20)  NOT NULL DEFAULT '',
    NominalRatePerMinute DECIMAL(10,2) NOT NULL DEFAULT 0
);
GO
INSERT INTO dbo.Products (Code, ProductName, TargetWeightGrams, PackagingSize, NominalRatePerMinute)
SELECT src.Code,
       COALESCE(NULLIF(RTRIM(c.Descrizione),''), src.Code),
       src.TargetW,
       '', 0
FROM (
    SELECT RTRIM(codice_articolo) AS Code, MAX(CAST(peso_netto_impostato AS DECIMAL(10,2))) AS TargetW
    FROM dbo.produzione
    WHERE codice_articolo IS NOT NULL AND RTRIM(codice_articolo) <> ''
    GROUP BY RTRIM(codice_articolo)
) src
/* match codici.Codice as the trailing (non zero-padded) part of the article code */
LEFT JOIN dbo.codici c ON src.Code LIKE '%' + RTRIM(c.Codice)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Products p WHERE p.Code = src.Code);
GO

/* ---------- MachineStatusLog (real table; live status not in Stiavelli DB)
   Empty for now; a collector/the dashboard can write connect/belt/alarm here. */
IF OBJECT_ID('dbo.MachineStatusLog','U') IS NULL
CREATE TABLE dbo.MachineStatusLog (
    LogId       BIGINT IDENTITY(1,1) PRIMARY KEY,
    MachineId   VARCHAR(10) NOT NULL,
    Timestamp   DATETIME2   NOT NULL,
    IsConnected BIT         NOT NULL,
    BeltRunning BIT         NOT NULL,
    AlarmActive BIT         NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MachineStatusLog_Machine_Timestamp')
CREATE INDEX IX_MachineStatusLog_Machine_Timestamp ON dbo.MachineStatusLog(MachineId, Timestamp);
GO

/* ---------- Notes (real table; dashboard-only) --------------------------- */
IF OBJECT_ID('dbo.Notes','U') IS NULL
CREATE TABLE dbo.Notes (
    NoteId              INT IDENTITY(1,1) PRIMARY KEY,
    Content             NVARCHAR(500) NOT NULL,
    CreatedByEmployeeId INT           NOT NULL,
    CreatedAt           DATETIME2     NOT NULL,
    IsActive            BIT           NOT NULL DEFAULT 1
);
GO

/* ---------- ProductionLots (VIEW) = produzione (+ lotto when present) -----
   Column names/types match what ProductionRepository selects & ProductionLot
   expects. lotto is joined on ID_produzione; when absent, CorrectedAvgWeight
   and IsConform fall back to values derived from produzione. */
CREATE OR ALTER VIEW dbo.ProductionLots AS
SELECT
    CAST(p.progressivo AS INT)                      AS LotId,
    RTRIM(p.numero_linea)                           AS MachineId,
    pr.ProductId                                    AS ProductId,
    CAST(COALESCE(l.progressivo_lotto, 0) AS INT)   AS LotNumber,
    CAST(p.turno AS INT)                            AS ShiftId,
    CAST(COALESCE(e.EmployeeId, 0) AS INT)          AS EmployeeId,
    p.tempo_inizio                                  AS StartTime,
    COALESCE(p.tempo_fine, p.tempo_inizio)          AS EndTime,
    CAST(p.peso_netto_impostato AS DECIMAL(10,2))   AS TargetNetWeight,
    CAST(p.peso_min_registrato  AS DECIMAL(10,2))   AS MinWeight,
    CAST(p.peso_max_registrato  AS DECIMAL(10,2))   AS MaxWeight,
    p.totale_confezioni                             AS TotalBags,
    p.confezioni_sotto_toll                         AS BagsUnderTolerance,
    p.confezioni_ok                                 AS BagsInTolerance,
    p.confezioni_sopra_toll                         AS BagsOverTolerance,
    p.confezioni_indeterminate                      AS BagsIndeterminate,
    CAST(p.deviazione_std AS DECIMAL(10,4))         AS StdDev,
    CAST(p.media_totale   AS DECIMAL(10,2))         AS AvgWeight,
    CAST(COALESCE(l.media_corretta, p.media_totale) AS DECIMAL(10,2)) AS CorrectedAvgWeight,
    p.espulsi_metal                                 AS MetalRejects,
    CAST(COALESCE(l.lotto_conforme,
         CASE WHEN p.totale_confezioni > 0
                   AND CAST(p.confezioni_sotto_toll + p.confezioni_sopra_toll AS FLOAT)
                       / p.totale_confezioni < 0.08
              THEN 1 ELSE 0 END) AS BIT)            AS IsConform
FROM dbo.produzione p
LEFT JOIN dbo.lotto     l  ON l.ID_produzione = p.ID_produzione
LEFT JOIN dbo.Products  pr ON pr.Code = RTRIM(p.codice_articolo)
LEFT JOIN dbo.Employees e  ON e.Code  = RTRIM(p.operatore)
WHERE p.numero_linea IS NOT NULL AND RTRIM(p.numero_linea) <> '';
GO
