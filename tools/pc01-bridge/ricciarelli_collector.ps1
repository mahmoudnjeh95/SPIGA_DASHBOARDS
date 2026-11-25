<#
  ricciarelli_collector.ps1 -- read-only collector for Ricciarelli CPM checkweighers
  (Ricciarelli 1 = 192.168.2.53, Ricciarelli 2 = 192.168.2.54).

  These panels run the Ricciarelli "HMI_Ricciarelli_CP" app (internal OPC_XML_DA_Demo).
  Each panel runs a TCP server on <porta_tcp> (default 4660) that, on ANY client request
  (it reads up to 10 bytes and ignores them), replies with a fixed-width ASCII telegram
  _Backup_CD[0.._lstConcentratore-1] + CR. We poll it like StatsViewer polls the others,
  assign a production code, and write produzione + lotto rows -- so the line appears on
  the dashboard like every other line. Model: tools/pc01-bridge/pc01_collector.ps1.

  TELEGRAM MAP (verified 2026-11-25 by decompiling the HMI exe + live cross-check on .53):
    PRODUCTION TOTALS
      0:8   under   (_Contapesi_Meno_Totale)
      8:8   ok      (_Contapesi_Ok_Totale)
      16:8  over    (_Contapesi_Piu_Totale)
      24:8  total   (computed = under+ok+over)      <-- sanity: under+ok+over == total
      35:5  sottoTU1 total (_Conta_Sotto_TU1_Totale)
      40:10 sumDeltaQ (Sum of squared deviations)
      50:10 sumDelta  (Sum of deviations from nominal)
      61:1  sign of sumDelta (1=negative)
      82:4  nominal net weight (recipe code 01; grams)
    LOT
      88:2  lot std  (_Deviazione_Lotto, /10 -> g)
      90:4  lot mean integer g  +  95:1 tenths  (_Media_Lotto)
      96:4  min average allowed (g)             (_Media_Minima)
      106:4 lot number (_Num_Lotto)
      118:4 TU1 legal-minimum threshold (g)
    ARTICLE
      126:40 recipe / article name in use (_RicettaInUso, space padded)
  Derived totals:
    mean = nominal + sign*(sumDelta/total)/WeightScale
    std  = sqrt(sumDeltaQ/total - (sumDelta/total)^2)/WeightScale
  (Confirmed: differencing two cumulative frames yields a positive variance.)

  Production/lot tracking: the telegram is CUMULATIVE with no start timestamp, so we keep
  a small JSON state file per line (LogDir\Ricciarelli_<Line>_state.json): a new production
  starts when 'total' drops (counter reset); a new lot starts when the lot number changes.

  Run modes:
    -Once   poll once, print, exit (default).
    -Loop   poll forever every -IntervalSec.
    -Write  ACTUALLY write produzione/lotto. OFF by default (dry-run). Nothing touches the
            DB without -Write.
#>
[CmdletBinding()]
param(
  [string]$Ip          = '192.168.2.53',
  [int]   $Port        = 4660,
  [string]$Line        = 'RIC1',                 # dashboard numero_linea (char 4)
  [string]$Server      = '.\SQLEXPRESS',
  [string]$Database    = 'stats',
  [int]   $IntervalSec = 60,
  [string]$LogDir      = 'C:\StatsLog',
  [int]   $WeightScale = 10,                      # telegram deviation units per gram (0.1 g -> 10)
  [switch]$Once,
  [switch]$Loop,
  [switch]$Write
)

$ErrorActionPreference = 'Stop'
if(-not (Test-Path $LogDir)){ New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$LogFile   = Join-Path $LogDir ("Ricciarelli_{0}_collector.log" -f $Line)
$StateFile = Join-Path $LogDir ("Ricciarelli_{0}_state.json"   -f $Line)

function Log([string]$msg,[string]$lvl='INFO'){
  $line = "{0} [{1}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $lvl, $msg
  Write-Host $line
  try { Add-Content -Path $LogFile -Value $line -Encoding UTF8 } catch {}
}

# ---- poll the panel ----
function Read-Telegram {
  $c = New-Object Net.Sockets.TcpClient
  $ar = $c.BeginConnect($Ip,$Port,$null,$null)
  if(-not $ar.AsyncWaitHandle.WaitOne(2500)){ $c.Close(); throw "connect timeout $Ip`:$Port" }
  $c.EndConnect($ar); $c.NoDelay=$true; $s=$c.GetStream()
  try {
    Start-Sleep -Milliseconds 250
    $req=[Text.Encoding]::ASCII.GetBytes('??????????'); $s.Write($req,0,$req.Length); $s.Flush()
    $buf=New-Object byte[] 512; $sb=New-Object Text.StringBuilder; $d=(Get-Date).AddMilliseconds(2500)
    while((Get-Date) -lt $d){
      if($c.Available -gt 0){ $n=$s.Read($buf,0,$buf.Length); if($n){[void]$sb.Append([Text.Encoding]::ASCII.GetString($buf,0,$n))}; $d=(Get-Date).AddMilliseconds(400) }
      else { Start-Sleep -Milliseconds 50 }
    }
    $sb.ToString().TrimEnd("`r","`n")
  } finally { $c.Close() }
}

function Seg([string]$t,[int]$o,[int]$w){ if($t.Length -ge $o+$w){ $t.Substring($o,$w) } else { '' } }
function NumSeg([string]$t,[int]$o,[int]$w){ $v=Seg $t $o $w; if($v -match '^\d+$'){ [int64]$v } else { $null } }
# DB decimals are decimal(6,1): clamp/round
function ClampDec([double]$v){ $r=[Math]::Round($v,1); if($r -gt 99999.9){99999.9} elseif($r -lt -99999.9){-99999.9} else {$r} }

function Parse-Telegram([string]$t){
  if($t.Length -lt 110){ return $null }
  $under = NumSeg $t 0 8; $ok = NumSeg $t 8 8; $over = NumSeg $t 16 8; $total = NumSeg $t 24 8
  $sottoTU1 = NumSeg $t 35 5
  $sumDq = NumSeg $t 40 10; $sumD = NumSeg $t 50 10; $sign = Seg $t 61 1
  $nominal = NumSeg $t 82 4
  $lotStdRaw = NumSeg $t 88 2
  $lotMeanInt = NumSeg $t 90 4; $lotMeanDec = NumSeg $t 95 1
  $minAvg = NumSeg $t 96 4
  $lotNo = NumSeg $t 106 4
  $sogliaTU1 = NumSeg $t 118 4
  $article = (Seg $t 126 40).Trim(); if([string]::IsNullOrWhiteSpace($article)){ $article = $Line }

  $mean=$null; $std=$null; $meanDev=$null
  if($total -and $total -gt 0 -and $null -ne $sumD){
    $s = if($sign -eq '1'){ -1 } else { 1 }
    $meanDev = $s * ($sumD / $total)
    if($null -ne $sumDq){ $var=($sumDq/$total)-(($sumD/$total)*($sumD/$total)); if($var -lt 0){$var=0}; $std=[math]::Sqrt($var)/$WeightScale }
    if($null -ne $nominal){ $mean=$nominal + $meanDev/$WeightScale }
  }
  $lotMean = if($null -ne $lotMeanInt){ $lotMeanInt + (($(if($null -ne $lotMeanDec){$lotMeanDec}else{0}))/10.0) } else { $null }
  $lotStd  = if($null -ne $lotStdRaw){ $lotStdRaw/10.0 } else { $null }

  [pscustomobject]@{
    under=$under; ok=$ok; over=$over; total=$total; sottoTU1=$sottoTU1
    nominal=$nominal; mean=$mean; std=$std
    lotNo=$lotNo; lotMean=$lotMean; lotStd=$lotStd; minAvg=$minAvg; sogliaTU1=$sogliaTU1
    article=$article; rawLen=$t.Length
  }
}

# ---- codice_articolo (20) = 8 zeros + 12-char name, matching the other lines ----
function CodiceArticolo([string]$name){ ('00000000' + $name.PadRight(12)).Substring(0,20) }
function IdProduzione([string]$cod,[DateTime]$start){ ($Line.PadRight(4) + $cod + $start.ToString('dd.MM.yyyy-HH:mm:ss')) }
function ShiftNow { $h=(Get-Date).Hour; if($h -ge 6 -and $h -lt 14){1} elseif($h -ge 14 -and $h -lt 22){2} else {3} }

function Load-State { try { if(Test-Path $StateFile){ Get-Content $StateFile -Raw | ConvertFrom-Json } else { $null } } catch { $null } }
function Save-State($st){ try { $st | ConvertTo-Json | Set-Content -Path $StateFile -Encoding UTF8 } catch {} }

# ---- DB ----
function Open-Db { $cn=New-Object System.Data.SqlClient.SqlConnection; $cn.ConnectionString="Server=$Server;Database=$Database;Integrated Security=SSPI;Connect Timeout=10"; $cn.Open(); $cn }
function Exec($cn,$sql,[hashtable]$p){ $cmd=$cn.CreateCommand(); $cmd.CommandText=$sql; foreach($k in $p.Keys){ [void]$cmd.Parameters.AddWithValue("@$k",$(if($null -eq $p[$k]){[DBNull]::Value}else{$p[$k]})) }; $cmd.ExecuteNonQuery() }

function Write-Produzione($cn,$p,$idp,$start){
  $prm=@{ idp=$idp; linea=$Line; inizio=$start; fine=(Get-Date); term=$false; cod=(CodiceArticolo $p.article);
    oper='   '; turno=(ShiftNow); linp=(''.PadRight(10)); pnet=[int]$p.nominal; pmax=[decimal]0.0; pmin=[decimal]0.0;
    tot=[int]$p.total; sotto=[int]$p.under; ok=[int]$p.ok; sopra=[int]$p.over; indet=[int]$p.sottoTU1;
    media=(ClampDec ([double]$p.mean)); dev=(ClampDec ([double]$p.std)); metal=0 }
  $ins=@"
INSERT INTO produzione (ID_produzione,numero_linea,tempo_inizio,tempo_fine,terminata,codice_articolo,
  operatore,turno,linea_produzione,peso_netto_impostato,peso_max_registrato,peso_min_registrato,
  totale_confezioni,confezioni_sotto_toll,confezioni_ok,confezioni_sopra_toll,confezioni_indeterminate,
  media_totale,deviazione_std,espulsi_metal)
VALUES (@idp,@linea,@inizio,@fine,@term,@cod,@oper,@turno,@linp,@pnet,@pmax,@pmin,
  @tot,@sotto,@ok,@sopra,@indet,@media,@dev,@metal)
"@
  [void](Exec $cn $ins $prm); 'INSERT'
}

function Upsert-Lotto($cn,$p,$idp,$lotStart){
  if($null -eq $p.lotNo){ return 'skip(no lot)' }
  $prm=@{ idp=$idp; oin=$lotStart; ofi=(Get-Date); prog=[int]$p.lotNo; dim=0;
    tu1=(ClampDec ([double]$p.sogliaTU1)); ct=0; cm=0; min=0; dev=(ClampDec ([double]$p.lotStd));
    meff=(ClampDec ([double]$p.lotMean)); mcorr=(ClampDec ([double]$p.lotMean)); conf=$true; fm=0; fnc=0; compl=$false }
  $upd=@"
UPDATE lotto SET ora_fine=@ofi, soglia_TU1=@tu1, deviazione_std=@dev, media_effettiva=@meff,
  media_corretta=@mcorr, progressivo_lotto=@prog WHERE ID_produzione=@idp AND ora_inizio=@oin
"@
  $n=Exec $cn $upd $prm
  if($n -eq 0){
    $insL=@"
INSERT INTO lotto (ID_produzione,ora_inizio,ora_fine,progressivo_lotto,dimensione,soglia_TU1,
  campioni_controllo_toll,campioni_controllo_media,conf_minori_TU1,deviazione_std,media_effettiva,
  media_corretta,lotto_conforme,flag_media_corretta,flag_non_conformita,completo)
VALUES (@idp,@oin,@ofi,@prog,@dim,@tu1,@ct,@cm,@min,@dev,@meff,@mcorr,@conf,@fm,@fnc,@compl)
"@
    [void](Exec $cn $insL $prm); return 'INSERT'
  }
  'UPDATE'
}

# ---- one poll ----
function Tick {
  try { $raw = Read-Telegram }
  catch { $em=if($_.Exception.InnerException){$_.Exception.InnerException.Message}else{$_.Exception.Message}; Log "panel $Ip`:$Port unreachable ($em)" 'WARN'; return }
  $p = Parse-Telegram $raw
  if($null -eq $p){ Log "unparseable telegram ($($raw.Length) bytes): [$raw]" 'WARN'; return }
  $sane = ($null -ne $p.total) -and (($p.under + $p.ok + $p.over) -eq $p.total)
  $meanT = if($null -ne $p.mean){'{0:N1}' -f $p.mean}else{'n/a'}; $stdT = if($null -ne $p.std){'{0:N2}' -f $p.std}else{'n/a'}
  $lmT = if($null -ne $p.lotMean){'{0:N1}' -f $p.lotMean}else{'n/a'}; $lsT = if($null -ne $p.lotStd){'{0:N1}' -f $p.lotStd}else{'n/a'}
  $sProd = "art='$($p.article)' tot=$($p.total) ok=$($p.ok) under=$($p.under) over=$($p.over) sottoTU1=$($p.sottoTU1) nominal=$($p.nominal) mean=$meanT std=$stdT sane=$sane"
  $sLot  = "lot#=$($p.lotNo) lotMean=$lmT lotStd=$lsT TU1=$($p.sogliaTU1) minAvg=$($p.minAvg)"

  if(-not $Write){ Log "DRYRUN produzione: $sProd"; Log "DRYRUN lotto:      $sLot"; return }
  if(-not $sane){ Log "SANITY FAIL (under+ok+over != total), not writing: $sProd" 'WARN'; return }
  if($p.total -le 0){ Log "idle (no production): $sProd"; return }

  # production/lot tracking
  $st = Load-State
  $now = Get-Date
  $cod = CodiceArticolo $p.article
  if($null -eq $st -or [int64]$p.total -lt [int64]$st.lastTotal -or $st.article -ne $p.article){
    $prodStart = $now; $lotStart = $now; $lastLot = $p.lotNo
  } else {
    $prodStart = [DateTime]$st.prodStart
    if($p.lotNo -ne $st.lastLot){ $lotStart = $now } else { $lotStart = [DateTime]$st.lotStart }
    $lastLot = $p.lotNo
  }
  $idp = IdProduzione $cod $prodStart

  $cn = Open-Db
  try {
    $a = Write-Produzione $cn $p $idp $prodStart
    $b = Upsert-Lotto $cn $p $idp $lotStart
    Log "produzione=$a lotto=$b  $sProd | $sLot"
  } finally { $cn.Close() }
  Save-State ([pscustomobject]@{ lastTotal=$p.total; prodStart=$prodStart; lotStart=$lotStart; lastLot=$lastLot; article=$p.article })
}

if($Loop){
  $mutex = New-Object System.Threading.Mutex($false,("Global\SpigaRicciarelli_{0}" -f $Line))
  if(-not $mutex.WaitOne(0)){ Log "another collector instance ($Line) already running; exiting." 'WARN'; return }
  Log "=== Ricciarelli collector ($Line $Ip`:$Port) starting  Write=$Write Interval=${IntervalSec}s ==="
  while($true){ try { Tick } catch { Log "tick error: $($_.Exception.Message)" 'ERROR' }; Start-Sleep -Seconds $IntervalSec }
} else {
  Log "=== Ricciarelli collector ($Line $Ip`:$Port) one-shot  Write=$Write ==="
  try { Tick } catch { Log "tick error: $($_.Exception.Message)" 'ERROR' }
}
