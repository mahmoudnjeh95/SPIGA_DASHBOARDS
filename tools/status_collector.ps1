<#
  SpigaDashboards status collector (data-freshness mode)
  ------------------------------------------------------
  NO LONGER REQUIRED: the app now derives connected / belt running /
  availability directly from the produzione snapshots (ProductionLots view),
  and no longer reads MachineStatusLog. The scheduled task can be removed.
  Note: Stiavelli writes a snapshot every ~30 min, so the 15/5 min windows
  below always reported connected=0.

  Writes one MachineStatusLog row per active machine each run, so the live
  dashboard slides know which machines are connected / running. Also keeps the
  Products and Employees lookups in sync with any new article/operator codes in
  dbo.produzione. Stiavelli does NOT persist connect/belt/alarm to the `stats`
  DB, so this fills that gap.

  Connectivity is derived purely from how recently each line wrote production
  rows (no network probing):
    IsConnected = a produzione row for this line is newer than $ConnectedWindowMinutes
    BeltRunning = a produzione row for this line is newer than $ActiveWindowMinutes
    AlarmActive = 0  (no alarm source in the stats DB; placeholder)

  Tune the windows to how often Stiavelli writes rows during a run. An online
  but idle line (no new packs) will drop to disconnected once it goes quiet
  longer than $ConnectedWindowMinutes -- that is expected in this mode.

  Run once manually to test; install_collector.ps1 schedules it every minute.
#>
param(
    [string]$Server                 = ".\SQLEXPRESS",
    [string]$Database               = "stats",
    [int]   $ConnectedWindowMinutes = 15,   # wrote data this recently => connected
    [int]   $ActiveWindowMinutes    = 5,    # wrote data this recently => belt running
    [int]   $RetentionDays          = 90    # trim MachineStatusLog older than this
)

$ErrorActionPreference = "Stop"
$log = Join-Path $PSScriptRoot "status_collector.log"
function Write-Log($m) {
    try { Add-Content -Path $log -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $m) } catch {}
}

$sql = @"
SET NOCOUNT ON;

-- 1. One status snapshot per active machine, connectivity from production recency.
INSERT INTO dbo.MachineStatusLog (MachineId, Timestamp, IsConnected, BeltRunning, AlarmActive)
SELECT m.MachineId, SYSDATETIME(),
  CASE WHEN EXISTS (SELECT 1 FROM dbo.produzione p
        WHERE RTRIM(p.numero_linea) = m.MachineId
          AND COALESCE(p.tempo_fine, p.tempo_inizio) >= DATEADD(MINUTE, -$ConnectedWindowMinutes, SYSDATETIME()))
       THEN 1 ELSE 0 END AS IsConnected,
  CASE WHEN EXISTS (SELECT 1 FROM dbo.produzione p
        WHERE RTRIM(p.numero_linea) = m.MachineId
          AND COALESCE(p.tempo_fine, p.tempo_inizio) >= DATEADD(MINUTE, -$ActiveWindowMinutes, SYSDATETIME()))
       THEN 1 ELSE 0 END AS BeltRunning,
  0 AS AlarmActive
FROM dbo.Machines m
WHERE m.IsActive = 1;

-- 2. Sync Products with any new article codes seen in produzione.
INSERT INTO dbo.Products (Code, ProductName, TargetWeightGrams, PackagingSize, NominalRatePerMinute)
SELECT src.Code, COALESCE(NULLIF(RTRIM(c.Descrizione),''), src.Code), src.TargetW, '', 0
FROM (SELECT RTRIM(codice_articolo) AS Code, MAX(CAST(peso_netto_impostato AS DECIMAL(10,2))) AS TargetW
      FROM dbo.produzione WHERE codice_articolo IS NOT NULL AND RTRIM(codice_articolo) <> ''
      GROUP BY RTRIM(codice_articolo)) src
LEFT JOIN dbo.codici c ON src.Code LIKE '%' + RTRIM(c.Codice)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Products p WHERE p.Code = src.Code);

-- 3. Sync Employees with any new operator codes seen in produzione.
INSERT INTO dbo.Employees (Code, FullName, Role)
SELECT src.Code, COALESCE(NULLIF(RTRIM(o.Descrizione),''), 'Operateur ' + src.Code), 'Operateur'
FROM (SELECT DISTINCT RTRIM(operatore) AS Code FROM dbo.produzione
      WHERE operatore IS NOT NULL AND RTRIM(operatore) <> '') src
LEFT JOIN dbo.operatori o ON RTRIM(o.Codice) = src.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.Employees e WHERE e.Code = src.Code);

-- 4. Retention trim.
DELETE FROM dbo.MachineStatusLog WHERE Timestamp < DATEADD(DAY, -$RetentionDays, SYSDATETIME());

-- Report counts.
SELECT CONCAT('machines=', (SELECT COUNT(*) FROM dbo.Machines WHERE IsActive=1),
              ' connected=', (SELECT COUNT(*) FROM dbo.MachineStatusLog s
                              WHERE s.Timestamp >= DATEADD(SECOND,-5,SYSDATETIME()) AND s.IsConnected=1));
"@

try {
    $out = & sqlcmd -S $Server -d $Database -E -b -h -1 -W -Q $sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
    Write-Log ("OK - " + ($out | Where-Object { $_ -match "machines=" } | Select-Object -First 1))
}
catch {
    Write-Log ("ERROR: " + $_.Exception.Message)
    throw
}
