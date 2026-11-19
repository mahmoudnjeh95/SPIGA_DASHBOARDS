<#
  pc01_collector.ps1  --  read-only collector for the PC01 checkweigher (192.168.2.35).

  WHY: PC01 was reflashed with newer WinMachLite firmware whose port-5001 supervisor
  can be READ (GETSTATUS/PRODREQ/LOTTOREQ) but not driven (GETRECIPESLIST returns
  SOR__EOR empty; RESETPRODCMD_ resets). So StatsViewer can never START/record a
  production on PC01, and PC01's local production carries an all-zeros code that
  StatsViewer will not persist. This collector reads PC01 directly, assigns a real
  production code, and writes produzione/lotto rows itself -- the same rows StatsViewer
  writes for the other 9 lines -- so PC01 appears on the dashboard.

  PC01 serves only ONE 5001 client, so PC01 must be OUT of StatsViewer while this runs.

  Run modes:
    -Once            poll once, write (or dry-run), exit.
    -Loop            poll forever every -IntervalSec.
    -DryRun          parse + print, do NOT write to the DB.
  Writes are ON by default; pass -DryRun to disable.

  Protocol: tools/stiavelli-sdk/PROTOCOL.md (SOP/SOL field maps). Verified 16/11/2026:
  PRODREQ -> SOP_<id>_<start>_<netW>_<minW>_<maxW>_<tot>_<under>_<ok>_<over>_<std>_<avg>_<5x reserved>_EOP
  sanity: under+ok+over = tot.
#>
[CmdletBinding()]
param(
  [string]$Ip          = '192.168.2.35',
  [int]   $Port        = 5001,
  [string]$Line        = 'PC01',
  [string]$CodeName    = 'PC01PROD',                    # 8-char production name -> codice_articolo
  [string]$Server      = '.\SQLEXPRESS',
  [string]$Database    = 'stats',
  [int]   $IntervalSec = 60,
  [string]$LogDir      = 'C:\StatsLog',
  [switch]$Once,
  [switch]$Loop,
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if(-not (Test-Path $LogDir)){ New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$LogFile = Join-Path $LogDir 'PC01_collector.log'

function Log([string]$msg,[string]$lvl='INFO'){
  $line = "{0} [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $lvl, $msg
  Write-Host $line
  try { Add-Content -Path $LogFile -Value $line -Encoding UTF8 } catch {}
}

# ---- supervisor read ----
function Read-Node([switch]$LotOnly) {
  $c = New-Object Net.Sockets.TcpClient
  $ar = $c.BeginConnect($Ip,$Port,$null,$null)
  if(-not $ar.AsyncWaitHandle.WaitOne(2500)){ $c.Close(); throw "connect timeout $Ip`:$Port" }
  $c.EndConnect($ar); $c.NoDelay=$true; $s=$c.GetStream()
  function Cmd($name){
    $b=[Text.Encoding]::ASCII.GetBytes($name); $s.Write($b,0,$b.Length); $s.Flush()
    $buf=New-Object byte[] 8192; $sb=New-Object Text.StringBuilder; $d=(Get-Date).AddMilliseconds(3000)
    while((Get-Date) -lt $d){ if($s.DataAvailable){ $n=$s.Read($buf,0,$buf.Length); if($n){[void]$sb.Append([Text.Encoding]::ASCII.GetString($buf,0,$n))} } else { Start-Sleep -Milliseconds 40 } }
    $sb.ToString()
  }
  try {
    if($LotOnly){ [pscustomobject]@{ sol=(Cmd 'LOTTOREQ') } }
    else { [pscustomobject]@{ status=(Cmd 'GETSTATUS'); sop=(Cmd 'PRODREQ'); sol=(Cmd 'LOTTOREQ') } }
  }
  finally { $c.Close() }
}

function To-Num([string]$v){ [double]($v -replace ',','.') }
# DB decimals are decimal(6,1): clamp to +/-99999.9, round to 1 dp
function Clamp-Dec([double]$v){ $r=[Math]::Round($v,1); if($r -gt 99999.9){99999.9} elseif($r -lt -99999.9){-99999.9} else {$r} }
function To-Date([string]$v){
  # dd.MM.yyyy-HH:mm:ss
  [DateTime]::ParseExact($v.Trim(),'dd.MM.yyyy-HH:mm:ss',[Globalization.CultureInfo]::InvariantCulture)
}

function Parse-SOP([string]$resp){
  $m=[regex]::Match($resp,'SOP_?(.*?)_?EOP',[Text.RegularExpressions.RegexOptions]::Singleline)
  if(-not $m.Success){ return $null }
  $f = $m.Groups[1].Value -split '_'
  if($f.Count -lt 11){ return $null }
  [pscustomobject]@{
    start      = (To-Date $f[1])
    pesoNetto  = [int][Math]::Round((To-Num $f[2]))
    pesoMin    = (To-Num $f[3])
    pesoMax    = (To-Num $f[4])
    totConf    = [int64](To-Num $f[5])
    sottoToll  = [int64](To-Num $f[6])
    confOk     = [int64](To-Num $f[7])
    sopraToll  = [int64](To-Num $f[8])
    devStd     = (To-Num $f[9])
    mediaTot   = (To-Num $f[10])
    indeterm   = if($f.Count -gt 11){[int64](To-Num $f[11])}else{0}
    espMetal   = if($f.Count -gt 12){[int64](To-Num $f[12])}else{0}
    nfields    = $f.Count
  }
}
function Parse-SOL([string]$resp){
  $m=[regex]::Match($resp,'SOL_?(.*?)_?EOL',[Text.RegularExpressions.RegexOptions]::Singleline)
  if(-not $m.Success){ return $null }
  $f = $m.Groups[1].Value -split '_'
  if($f.Count -lt 15){ return $null }
  [pscustomobject]@{
    # f[0] only toggles 0/1 on PC01 (not a lot number); f[3] is the running
    # lot counter (e.g. 77) -> stored as progressivo_lotto.
    prog       = [int](To-Num $f[3])
    oraInizio  = (To-Date $f[1])
    oraFine    = (To-Date $f[2])
    dimensione = [int](To-Num $f[4])
    sogliaTU1  = (To-Num $f[5])
    campToll   = [int](To-Num $f[6])
    campMedia  = [int](To-Num $f[7])
    minoriTU1  = [int](To-Num $f[8])
    devStd     = (To-Num $f[9])
    mediaEff   = (To-Num $f[10])
    mediaCorr  = (To-Num $f[11])
    conforme   = ($f[12] -eq '1')
    flagMedia  = [int](To-Num $f[13])
    flagNC     = [int](To-Num $f[14])
  }
}

# codice_articolo (20) = 8-char article (zeros) + 12-char production name
function Codice-Articolo { ('00000000' + $CodeName.PadRight(12)).Substring(0,20) }
# ID_produzione (43) = line(4) + codice_articolo(20) + "dd.MM.yyyy-HH:mm:ss"(19)
function Id-Produzione([DateTime]$start){ ($Line.PadRight(4) + (Codice-Articolo) + $start.ToString('dd.MM.yyyy-HH:mm:ss')) }
function Shift-Now { $h=(Get-Date).Hour; if($h -ge 6 -and $h -lt 14){1} elseif($h -ge 14 -and $h -lt 22){2} else {3} }

# ---- DB ----
function Open-Db {
  $cn = New-Object System.Data.SqlClient.SqlConnection
  $cn.ConnectionString = "Server=$Server;Database=$Database;Integrated Security=SSPI;Connect Timeout=10"
  $cn.Open(); $cn
}
function Exec($cn,$sql,[hashtable]$p){
  $cmd=$cn.CreateCommand(); $cmd.CommandText=$sql
  foreach($k in $p.Keys){ [void]$cmd.Parameters.AddWithValue("@$k", $(if($null -eq $p[$k]){[DBNull]::Value}else{$p[$k]})) }
  $cmd.ExecuteNonQuery()
}

function Write-Produzione($cn,$sop){
  # INSERT a snapshot row each tick (like StatsViewer): the dashboard detects
  # "producing" from the bag-count delta BETWEEN snapshots, so a single updated
  # row would always read as belt-stopped. One row per tick, sharing ID_produzione
  # + tempo_inizio for the same production; a new SOP start makes a new ID.
  $idp = Id-Produzione $sop.start
  $p = @{
    idp=$idp; linea=$Line; inizio=$sop.start; fine=(Get-Date); term=$false; cod=(Codice-Articolo);
    oper='   '; turno=(Shift-Now); linp=(''.PadRight(10)); pnet=$sop.pesoNetto;
    pmax=(Clamp-Dec $sop.pesoMax); pmin=(Clamp-Dec $sop.pesoMin); tot=$sop.totConf; sotto=$sop.sottoToll; ok=$sop.confOk;
    sopra=$sop.sopraToll; indet=$sop.indeterm; media=(Clamp-Dec $sop.mediaTot); dev=(Clamp-Dec $sop.devStd); metal=$sop.espMetal
  }
  $ins = @"
INSERT INTO produzione (ID_produzione,numero_linea,tempo_inizio,tempo_fine,terminata,codice_articolo,
  operatore,turno,linea_produzione,peso_netto_impostato,peso_max_registrato,peso_min_registrato,
  totale_confezioni,confezioni_sotto_toll,confezioni_ok,confezioni_sopra_toll,confezioni_indeterminate,
  media_totale,deviazione_std,espulsi_metal)
VALUES (@idp,@linea,@inizio,@fine,@term,@cod,@oper,@turno,@linp,@pnet,@pmax,@pmin,
  @tot,@sotto,@ok,@sopra,@indet,@media,@dev,@metal)
"@
  [void](Exec $cn $ins $p); return 'INSERT'
}

function Upsert-Lotto($cn,$sop,$sol){
  # skip empty/placeholder lots (no accumulated weighing yet)
  if($null -eq $sol -or $sol.mediaEff -le 0){ return 'skip(empty)' }
  # One row per lot, keyed by production + lot start time: LOTTOREQ returns the
  # last finished lot, so the same lot is read again until the next one ends.
  # (Keying on SOL field 0 overwrote every lot: PC01 only sends 0/1 there.)
  $idp = Id-Produzione $sop.start
  $p = @{ idp=$idp; oin=$sol.oraInizio; ofi=$sol.oraFine; prog=$sol.prog; dim=$sol.dimensione;
    tu1=(Clamp-Dec $sol.sogliaTU1); ct=$sol.campToll; cm=$sol.campMedia; min=$sol.minoriTU1; dev=(Clamp-Dec $sol.devStd);
    meff=(Clamp-Dec $sol.mediaEff); mcorr=(Clamp-Dec $sol.mediaCorr); conf=$sol.conforme; fm=$sol.flagMedia; fnc=$sol.flagNC;
    compl=($sol.oraFine -gt $sol.oraInizio) }
  $upd = @"
UPDATE lotto SET ora_fine=@ofi, dimensione=@dim, soglia_TU1=@tu1, campioni_controllo_toll=@ct,
  campioni_controllo_media=@cm, conf_minori_TU1=@min, deviazione_std=@dev, media_effettiva=@meff,
  media_corretta=@mcorr, lotto_conforme=@conf, flag_media_corretta=@fm, flag_non_conformita=@fnc, completo=@compl,
  progressivo_lotto=@prog
WHERE ID_produzione=@idp AND ora_inizio=@oin
"@
  $n = Exec $cn $upd $p
  if($n -eq 0){
    $ins = @"
INSERT INTO lotto (ID_produzione,ora_inizio,ora_fine,progressivo_lotto,dimensione,soglia_TU1,
  campioni_controllo_toll,campioni_controllo_media,conf_minori_TU1,deviazione_std,media_effettiva,
  media_corretta,lotto_conforme,flag_media_corretta,flag_non_conformita,completo)
VALUES (@idp,@oin,@ofi,@prog,@dim,@tu1,@ct,@cm,@min,@dev,@meff,@mcorr,@conf,@fm,@fnc,@compl)
"@
    [void](Exec $cn $ins $p); return 'INSERT'
  }
  return 'UPDATE'
}

# ---- main ----
function Tick {
  $r = Read-Node
  $sop = Parse-SOP $r.sop
  if($null -eq $sop){ Log "PRODREQ unparseable: [$($r.sop)]" 'WARN'; return }
  $sane = ($sop.confOk + $sop.sottoToll + $sop.sopraToll) -eq $sop.totConf
  $summary = "tot=$($sop.totConf) ok=$($sop.confOk) u=$($sop.sottoToll) o=$($sop.sopraToll) media=$($sop.mediaTot) start=$($sop.start.ToString('dd.MM.yyyy HH:mm:ss')) sane=$sane"
  if($sop.totConf -le 0){ Log "idle (no production): $summary"; return }
  if(-not $sane){ Log "SANITY FAIL, not writing: $summary" 'WARN'; return }
  if($DryRun){ Log "DRYRUN $summary"; return }
  $cn = Open-Db
  try {
    $a = Write-Produzione $cn $sop
    $sol = Parse-SOL $r.sol
    $b = Upsert-Lotto $cn $sop $sol
    Log "produzione=$a lotto=$b(#$($sol.prog))  $summary"
  } finally { $cn.Close() }

  # PC01 lots are 50 bags (~50 s) and LOTTOREQ only returns the last one, so a
  # single read a minute misses lots: read the lot again half a minute later.
  Start-Sleep -Seconds 25
  try {
    $sol2 = Parse-SOL (Read-Node -LotOnly).sol
    $cn = Open-Db
    try { $c = Upsert-Lotto $cn $sop $sol2; Log "lotto=$c(#$($sol2.prog)) (2nd read)" } finally { $cn.Close() }
  } catch { Log "2nd lot read error: $($_.Exception.Message)" 'WARN' }
}

if($Loop){
  # single-instance guard so the Startup launcher + a manual start can't double-run
  $mutex = New-Object System.Threading.Mutex($false,'Global\SpigaPC01Collector')
  if(-not $mutex.WaitOne(0)){ Log "another collector instance is already running; exiting." 'WARN'; return }
  Log "=== PC01 collector starting (Line=$Line Server=$Server DryRun=$DryRun Interval=${IntervalSec}s) ==="
  while($true){
    try { Tick } catch { Log "tick error: $($_.Exception.Message)" 'ERROR' }
    Start-Sleep -Seconds $IntervalSec
  }
} else {
  Log "=== PC01 collector one-shot (Line=$Line Server=$Server DryRun=$DryRun) ==="
  try { Tick } catch { Log "tick error: $($_.Exception.Message)" 'ERROR' }
}
