/* =============================================================================
   SPIGA_DASHBOARDS - Stiavelli compatibility layer
   Target DB: stats  (SQL Server / SQLEXPRESS)

   Adds what the dashboard needs next to the real Stiavelli tables WITHOUT
   touching the Stiavelli data:
     - Real, writable tables for what the app writes or the Stiavelli DB
       doesn't store: Machines, Shifts, Employees, Products, Notes
       (+ MachineStatusLog, kept for older installs / the optional collector)
     - An index on dbo.produzione for the dashboard's time-range reads

   HOW dbo.produzione REALLY WORKS (checked against a 1000-row export):
     - One row is NOT a lot. Stiavelli writes a SNAPSHOT row every ~30 min for
       each line, for the production currently running on it (ID_produzione).
     - totale_confezioni / confezioni_* / espulsi_metal are CUMULATIVE since
       tempo_inizio of that production; a production can stay open for months.
     - tempo_fine = time the snapshot was written; tempo_inizio = production start.
     - media_totale / deviazione_std / peso_min/max = running stats of the whole
       production, not of the last 30 min.
     - operatore, turno, linea_produzione are empty / 0.
   The app turns consecutive snapshots of a production into per-interval
   deltas and sums them per hour/day itself (ProductionRepository), reading
   only the time range it needs -- see the index below. The snapshot cadence
   (30 min, or under 1 min) doesn't matter.

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
-- Real machine roster (IPs confirmed by the client). MachineId must equal
-- produzione.numero_linea. Full sync: inserts new, fixes changed IPs, and
-- removes any machine not in this list (display names edited in the app are kept).
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

/* ---------- Shifts (real table; reference only) -------------------------
   produzione.turno is always 0, so the app derives the poste from the time
   of day (Matin 06-14 / Soir 14-22 / Nuit 22-06). */
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
   produzione.operatore is blank on this site, so this usually stays empty.
   The app only needs it for the sticky note's CreatedByEmployeeId. */
IF OBJECT_ID('dbo.Employees','U') IS NULL
CREATE TABLE dbo.Employees (
    EmployeeId INT IDENTITY(1,1) PRIMARY KEY,
    Code       VARCHAR(3)    NOT NULL UNIQUE,   -- bridge to produzione.operatore
    FullName   NVARCHAR(100) NOT NULL,
    Role       NVARCHAR(30)  NOT NULL DEFAULT 'Operateur'
);
GO
INSERT INTO dbo.Employees (Code, FullName, Role)
SELECT src.Code, 'Operateur ' + src.Code, 'Operateur'
FROM (
    SELECT DISTINCT RTRIM(operatore) AS Code FROM dbo.produzione
    WHERE operatore IS NOT NULL AND RTRIM(operatore) <> ''
) src
WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.Code = src.Code);
GO

/* ---------- Products (real table) ---------------------------------------
   One row per article code seen in produzione (e.g. 00000000NOUVELLEPROD).
   ProductName defaults to the code without its zero padding (NOUVELLEPROD);
   rename it here if needed. NominalRatePerMinute (ideal bags/min) has no
   Stiavelli source: set it per article to get a TRS Performance figure
   (0 = no target -> Performance and TRS show 0). */
IF OBJECT_ID('dbo.Products','U') IS NULL
CREATE TABLE dbo.Products (
    ProductId            INT IDENTITY(1,1) PRIMARY KEY,
    Code                 VARCHAR(20)   NOT NULL UNIQUE,  -- RTRIM(produzione.codice_articolo)
    ProductName          NVARCHAR(100) NOT NULL,
    TargetWeightGrams    DECIMAL(10,2) NOT NULL DEFAULT 0,
    PackagingSize        NVARCHAR(20)  NOT NULL DEFAULT '',
    NominalRatePerMinute DECIMAL(10,2) NOT NULL DEFAULT 0
);
GO
INSERT INTO dbo.Products (Code, ProductName, TargetWeightGrams, PackagingSize, NominalRatePerMinute)
SELECT src.Code,
       COALESCE(NULLIF(SUBSTRING(src.Code, PATINDEX('%[^0]%', src.Code + 'x'), 20), ''), src.Code),
       src.TargetW, '', 0
FROM (
    SELECT RTRIM(codice_articolo) AS Code, MAX(CAST(peso_netto_impostato AS DECIMAL(10,2))) AS TargetW
    FROM dbo.produzione
    WHERE codice_articolo IS NOT NULL AND RTRIM(codice_articolo) <> ''
    GROUP BY RTRIM(codice_articolo)
) src
WHERE NOT EXISTS (SELECT 1 FROM dbo.Products p WHERE p.Code = src.Code);
GO

/* ---------- MachineStatusLog (legacy) -------------------------------------
   No longer read by the app: connected / belt-running / availability are now
   derived from produzione snapshots (see ProductionRepository). Kept so older
   installs and tools/status_collector.ps1 don't break. */
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

/* ---------- Index on produzione (read path of the dashboard) -------------
   Every dashboard query reads one line's snapshots over a time range
   (WHERE numero_linea = ... AND tempo_fine BETWEEN ...). Stiavelli's snapshot
   cadence can go under 1 minute (~26 000 rows/day for 9 lines, ~10 M/year):
   without this index each query would scan the whole table.
   It only adds an index -- no Stiavelli data or column is changed. The cost
   for Stiavelli's own INSERTs is negligible at a few rows per second.
   SQLEXPRESS builds it offline: Stiavelli's inserts wait while it's created
   (seconds to a few minutes on a large table) -- run it at a quiet moment.
   To remove it: DROP INDEX IX_produzione_linea_fine ON dbo.produzione; */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_produzione_linea_fine' AND object_id = OBJECT_ID('dbo.produzione'))
CREATE NONCLUSTERED INDEX IX_produzione_linea_fine
    ON dbo.produzione (numero_linea, tempo_fine)
    INCLUDE (ID_produzione, progressivo, tempo_inizio, codice_articolo, peso_netto_impostato,
             totale_confezioni, confezioni_sotto_toll, confezioni_ok, confezioni_sopra_toll,
             confezioni_indeterminate, media_totale, espulsi_metal);
GO

/* ---------- ProductionLots view (previous version) --------------------------
   No longer used: it ran LAG over each line's whole history on every query,
   which doesn't scale to sub-minute snapshots. The app now aggregates
   time-bounded ranges itself (ProductionRepository). */
IF OBJECT_ID('dbo.ProductionLots','V') IS NOT NULL
    DROP VIEW dbo.ProductionLots;
GO

/* ---------- Optional: dedicated SQL login for the dashboard ----------------
   Uncomment and run once (as an admin) to create the login entered in
   Parametres > Login SQL. Reads everything, writes only the Notes and
   Machines tables (sticky note + machine display names).

USE master;
CREATE LOGIN spiga_dashboard WITH PASSWORD = 'ChangeMe!2026', CHECK_POLICY = OFF;
GO
USE stats;
CREATE USER spiga_dashboard FOR LOGIN spiga_dashboard;
ALTER ROLE db_datareader ADD MEMBER spiga_dashboard;
GRANT INSERT, UPDATE ON dbo.Notes    TO spiga_dashboard;
GRANT UPDATE         ON dbo.Machines TO spiga_dashboard;
GO
*/
