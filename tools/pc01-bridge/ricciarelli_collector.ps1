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

  -Source Log (Ricciarelli 2, .54): that panel runs the newer Ricciarelli "ControlTerm"
  (V. 800r12) which has NO TCP server; it only answers a data concentrator on serial COM2.
  But ControlTerm logs every message of the weigher head (COM3) to
  D:\Ricciarelli\Log\MessageLog.txt (UTF-16, rotated to .bak every hour), readable over
  the panel's "Ricciareli" share. We tail that log and count the packs exactly like
  ControlTerm does (CpHandler.handle0x42, verified 2026-11-25 against 1 kg / 0.5 kg test
  bags and a live 400 g production):
    0x41 (len 482) recipe codes 1..119, 4 hex nibbles each (sent when ControlTerm starts):
         c1 nominal, c2 tare, c3 min, c4 max, c43 lot size, c78 decimals, c110 model
    0x42 (len 10)  one weighed pack: net weight (4 nibbles, in 1/divs g) + speed
         g = floor(div/divs) - tare; g <= min -> under, g >= max -> over, else OK;
         a lot is c43 OK packs.
  A production is one recipe: it ends when the recipe changes or after -IdleHours with
  no pack. The recipe is kept in the state file (it is only sent at ControlTerm start),
  seeded from -Nominal/-Tare/-MinOk/-MaxOk/-LotSize. The panel clock runs a few minutes
  off, so pack times are shifted by (now - newest log entry). The share account is
  stored once with -SetCredential (DPAPI, machine scope) next to this script.

  Run modes:
    -Once   poll once, print, exit (default).
    -Loop   poll forever every -IntervalSec.
    -Write  ACTUALLY write produzione/lotto. OFF by default (dry-run). Nothing touches the
            DB without -Write. A dry run keeps its own state file (..._state.dryrun.json).
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
  [ValidateSet('Telegram','Log')]
  [string]$Source      = 'Telegram',             # Log = tail ControlTerm's MessageLog (RIC2)
  [string]$LogShare    = '',                     # default \\<Ip>\Ricciareli\Ricciarelli\Log
  [string]$CredFile    = '',                     # default <script dir>\Ricciarelli_<Line>.cred
  [double]$IdleHours   = 4,                      # Log: no pack for this long = new production
  [int]   $Nominal     = 400,                    # Log: recipe seed until a 0x41 is seen
  [int]   $Tare        = 3,
  [int]   $MinOk       = 390,
  [int]   $MaxOk       = 430,
  [int]   $LotSize     = 100,
  [string]$StartAt     = '',                     # Log, first run only: ignore packs before this local time
  [switch]$SetCredential,
  [string]$CredUser    = '',
  [string]$CredPassword= '',
  [switch]$Once,
  [switch]$Loop,
  [switch]$Write
)

$ErrorActionPreference = 'Stop'
if(-not (Test-Path $LogDir)){ New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
$LogFile   = Join-Path $LogDir ("Ricciarelli_{0}_collector.log" -f $Line)
$StateFile = Join-Path $LogDir ("Ricciarelli_{0}_state{1}.json" -f $Line, $(if($Write){''}else{'.dryrun'}))
if(-not $LogShare){ $LogShare = "\\$Ip\Ricciareli\Ricciarelli\Log" }
$ScriptDir = if($PSScriptRoot){ $PSScriptRoot } else { (Get-Location).Path }
if(-not $CredFile){ $CredFile = Join-Path $ScriptDir ("Ricciarelli_{0}.cred" -f $Line) }

# State-file times are written as local 'yyyy-MM-ddTHH:mm:ss' text: a [DateTime] through
# Windows PowerShell's ConvertTo/From-Json comes back in UTC (one hour early here).
$Inv = [Globalization.CultureInfo]::InvariantCulture
function DtOut([DateTime]$d){ $d.ToString('yyyy-MM-ddTHH:mm:ss', $Inv) }
function DtIn($v){
  if($v -is [DateTime]){ return $v.ToLocalTime() }
  [DateTime]::ParseExact([string]$v,'yyyy-MM-ddTHH:mm:ss',$Inv)
}

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
function Save-State($st){ try { $st | ConvertTo-Json -Depth 6 | Set-Content -Path $StateFile -Encoding UTF8 } catch {} }

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
    $prodStart = DtIn $st.prodStart
    if($p.lotNo -ne $st.lastLot){ $lotStart = $now } else { $lotStart = DtIn $st.lotStart }
    $lastLot = $p.lotNo
  }
  $idp = IdProduzione $cod $prodStart

  $cn = Open-Db
  try {
    $a = Write-Produzione $cn $p $idp $prodStart
    $b = Upsert-Lotto $cn $p $idp $lotStart
    Log "produzione=$a lotto=$b  $sProd | $sLot"
  } finally { $cn.Close() }
  Save-State ([pscustomobject]@{ lastTotal=$p.total; prodStart=(DtOut $prodStart); lotStart=(DtOut $lotStart); lastLot=$lastLot; article=$p.article })
}

# ======================= Source Log (ControlTerm MessageLog) =======================

function Save-Credential {
  if(-not $CredUser -or -not $CredPassword){ throw '-SetCredential needs -CredUser and -CredPassword' }
  Add-Type -AssemblyName System.Security
  $bytes = [Text.Encoding]::UTF8.GetBytes("$CredUser`n$CredPassword")
  $enc = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, 'LocalMachine')
  [IO.File]::WriteAllBytes($CredFile, $enc)
  # Machine-scope DPAPI: any local account could decrypt it, so only admins may read the file.
  try { & icacls.exe $CredFile /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null } catch {}
  Log "credential for $Line saved to $CredFile (DPAPI, machine scope, admins only)"
}

function Connect-Share {
  if(Test-Path $LogShare){ return }
  if(-not (Test-Path $CredFile)){ throw "no access to $LogShare and no credential file $CredFile (run once with -SetCredential)" }
  Add-Type -AssemblyName System.Security
  $txt = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($CredFile), $null, 'LocalMachine'))
  $user, $secret = $txt -split "`n", 2
  if(-not ('SpigaNet' -as [type])){
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class SpigaNet {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public class NETRESOURCE { public int dwScope, dwType, dwDisplayType, dwUsage;
    public string lpLocalName, lpRemoteName, lpComment, lpProvider; }
  [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
  public static extern int WNetAddConnection2(NETRESOURCE r, string pwd, string user, int flags);
}
'@
  }
  $share = ($LogShare -split '\\' | Where-Object { $_ })[0..1] -join '\'
  $r = New-Object SpigaNet+NETRESOURCE; $r.dwType = 1; $r.lpRemoteName = "\\$share"
  $rc = [SpigaNet]::WNetAddConnection2($r, $secret, $user, 0)
  if($rc -ne 0 -and $rc -ne 1219){ throw "cannot connect \\$share (error $rc)" }
}

function Open-Shared([string]$path){ [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite') }

# First 64 chars of a log file: identifies it across the hourly rotation.
function Read-Head([string]$path){
  $fs = Open-Shared $path
  try { $b = New-Object byte[] 128; $n = $fs.Read($b, 0, 128); [Text.Encoding]::Unicode.GetString($b, 0, $n - ($n % 2)) } finally { $fs.Close() }
}

# Text from byte $off to the last complete entry (entries end with a blank line).
# Returns the text and the byte offset just after it.
function Read-Entries([string]$path, [long]$off){
  $fs = Open-Shared $path
  try {
    $len = $fs.Length; if($len -le $off){ return @('', $off) }
    [void]$fs.Seek($off, 'Begin'); $n = [int]($len - $off); $n -= $n % 2
    $b = New-Object byte[] $n; $r = 0
    while($r -lt $n){ $k = $fs.Read($b, $r, $n - $r); if($k -le 0){ break }; $r += $k }
    $t = [Text.Encoding]::Unicode.GetString($b, 0, $r - ($r % 2))
    $cut = $t.LastIndexOf("`r`n`r`n"); if($cut -lt 0){ return @('', $off) }
    $t = $t.Substring(0, $cut + 4)
    @($t, ($off + 2 * $t.Length))
  } finally { $fs.Close() }
}

function Nib([int[]]$b, [int]$o, [int]$n){ $v = 0; for($i = 0; $i -lt $n; $i++){ $v = ($v -shl 4) + ($b[$o + $i] -band 15) }; $v }

# Legal TU1 (nominal minus tolerable negative error, EU 76/211); grams.
function Tu1Of([double]$nom){
  $tne = if($nom -lt 50){ $nom*0.09 } elseif($nom -lt 100){ 4.5 } elseif($nom -lt 200){ $nom*0.045 } elseif($nom -lt 300){ 9 } elseif($nom -lt 500){ $nom*0.03 } elseif($nom -lt 1000){ 15 } else { $nom*0.015 }
  [Math]::Round($nom - $tne, 1)
}

function New-Recipe([hashtable]$c){
  $divs = switch([int]$c[110]){ 3 {5} 5 {2} 15 {1} 30 {1} default {10} }
  $gran = switch([int]$c[110]){ 3 {2} 5 {5} 15 {10} 30 {20} default {1} }
  $dec = [int]$c[78]
  $nom = if($dec -eq 1){ $c[1]/10.0 } else { [double]$c[1] }
  [pscustomobject]@{ c1=[int]$c[1]; tare=[int]$c[2]; min=[int]$c[3]; max=[int]$c[4]; lotSize=[int]$c[43];
    dec=$dec; divs=$divs; gran=$gran; nominal=$nom; tu1=(Tu1Of $nom) }
}

# Pack weight exactly as ControlTerm's handle0x42: returns @(compareValue, grams).
function Pack-Weight($rc, [int]$div){
  if($rc.dec -eq 0){
    $g = [Math]::Floor($div / $rc.divs); if($g -ge $rc.tare){ $g -= $rc.tare }
    $rem = $g % $rc.gran; $g = $g - $rem; if($rem -gt $rc.gran / 2){ $g += $rc.gran }
    return @($g, [double]$g)
  }
  if($div -ge $rc.tare){ $div -= $rc.tare }
  $w10 = [Math]::Floor($div / $rc.divs) * 10 + ($div % $rc.divs)
  @($w10, $w10 / 10.0)
}

function New-Acc { [pscustomobject]@{ n=0; sum=0.0; sumSq=0.0 } }
function Add-Acc($a, [double]$g){ $a.n++; $a.sum += $g; $a.sumSq += $g*$g }
function Mean-Of($a){ if($a.n -gt 0){ $a.sum / $a.n } else { 0 } }
function Std-Of($a){ if($a.n -lt 2){ return 0 }; $m = $a.sum / $a.n; $v = ($a.sumSq - $a.n*$m*$m) / ($a.n - 1); if($v -lt 0){ 0 } else { [Math]::Sqrt($v) } }

function New-Production($rc, [DateTime]$t){
  [pscustomobject]@{ start=(DtOut $t); lastPack=(DtOut $t); total=0; ok=0; under=0; over=0; belowTu1=0;
    acc=(New-Acc); lotNo=1; lotStart=(DtOut $t); lotEnd=(DtOut $t); lotBelow=0; lot=(New-Acc); article=('{0}G' -f $rc.nominal) }
}

function Lot-Row($pr, $rc, [bool]$complete){
  [pscustomobject]@{ no=$pr.lotNo; start=$pr.lotStart; end=$pr.lotEnd; size=$pr.lot.n; mean=(Mean-Of $pr.lot);
    std=(Std-Of $pr.lot); below=$pr.lotBelow; tu1=$rc.tu1; complete=$complete }
}

function Tick-Log {
  Connect-Share
  $cur = Join-Path $LogShare 'MessageLog.txt'; $bak = Join-Path $LogShare 'MessageLog.bak'
  $st = Load-State
  $rc = if($st -and $st.recipe){ $st.recipe } else {
    $seed = @{ 1=$Nominal; 2=$Tare; 3=$MinOk; 4=$MaxOk; 43=$LotSize; 78=0; 110=1 }
    Log "no recipe seen yet: using seed nominal=$Nominal tare=$Tare ok=$MinOk..$MaxOk lot=$LotSize" 'WARN'
    New-Recipe $seed }
  $pr = if($st){ $st.prod } else { $null }
  if($pr){ $pr.acc = [pscustomobject]$pr.acc; $pr.lot = [pscustomobject]$pr.lot }

  # New text since last time, across the hourly rotation.
  $head = Read-Head $cur
  $text = ''
  if($st -and $st.logHead -eq $head){ $r = Read-Entries $cur ([long]$st.logOffset); $text = $r[0]; $off = $r[1] }
  else {
    if($st -and (Test-Path $bak) -and (Read-Head $bak) -eq $st.logHead){
      $r = Read-Entries $bak ([long]$st.logOffset); $text = $r[0]; Log "log rotated; read the rest of MessageLog.bak"
    } elseif($st){ Log "log rotated and the previous file is gone: some packs may be missing" 'WARN' }
    elseif(Test-Path $bak){ $r = Read-Entries $bak 0; $text = $r[0]; Log "first run: replaying MessageLog.bak + MessageLog.txt" }
    $r = Read-Entries $cur 0; $text += $r[0]; $off = $r[1]
  }

  $entries = [regex]::Matches($text, '(\d\d/\d\d/\d{4} \d\d:\d\d:\d\d) ==> Protocol ==>\r\nRECEIVED code: (0x4[12]), lenght: (\d+), bytes: ([0-9A-Fx-]+)')
  $allTimes = [regex]::Matches($text, '(\d\d/\d\d/\d{4} \d\d:\d\d:\d\d) ==> ')
  $skew = [TimeSpan]::Zero
  if($allTimes.Count){
    $last = [DateTime]::ParseExact($allTimes[$allTimes.Count-1].Groups[1].Value, 'dd/MM/yyyy HH:mm:ss', $Inv)
    $skew = (Get-Date) - $last
    if([Math]::Abs($skew.TotalMinutes) -gt 30){ Log ("panel clock off by {0:N0} min; using it anyway" -f $skew.TotalMinutes) 'WARN' }
  }

  $done = New-Object System.Collections.ArrayList   # completed lots to write
  $packs = 0; $finished = $null
  $from = if(-not $st -and $StartAt){ [DateTime]::Parse($StartAt) } else { [DateTime]::MinValue }
  foreach($m in $entries){
    $t = [DateTime]::ParseExact($m.Groups[1].Value, 'dd/MM/yyyy HH:mm:ss', $Inv) + $skew
    if($t -lt $from -and $m.Groups[2].Value -eq '0x42'){ continue }
    $b = [int[]]($m.Groups[4].Value -split '-' | ForEach-Object { [Convert]::ToInt32($_.Substring(2), 16) })
    if($m.Groups[2].Value -eq '0x41'){
      if($b.Count -lt 482){ continue }
      $c = @{}; for($i = 1; $i -lt 120; $i++){ $c[$i] = Nib $b (1 + 4*($i-1)) 4 }
      $new = New-Recipe $c
      if($new.c1 -ne $rc.c1 -or $new.min -ne $rc.min -or $new.max -ne $rc.max -or $new.tare -ne $rc.tare -or $new.lotSize -ne $rc.lotSize){
        Log "recipe: nominal=$($new.nominal) tare=$($new.tare) ok=$($new.min)..$($new.max) lot=$($new.lotSize)"
        if($pr){ $finished = $pr; $pr = $null }
      }
      $rc = $new; continue
    }
    if($b.Count -lt 8){ continue }
    if($pr -and ($t - (DtIn $pr.lastPack)).TotalHours -ge $IdleHours){ $finished = $pr; $pr = $null }
    if(-not $pr){ $pr = New-Production $rc $t }
    $w = Pack-Weight $rc (Nib $b 1 4); $cmp = $w[0]; $g = $w[1]
    $pr.total++; $pr.lastPack = DtOut $t; $packs++
    if($cmp -ge $rc.max){ $pr.over++ }
    elseif($cmp -le $rc.min){ $pr.under++ }
    else {
      $pr.ok++; Add-Acc $pr.acc $g
      if($pr.lot.n -eq 0){ $pr.lotStart = DtOut $t }
      Add-Acc $pr.lot $g; $pr.lotEnd = DtOut $t
      if($g -lt $rc.tu1){ $pr.belowTu1++; $pr.lotBelow++ }
      if($pr.lot.n -ge $rc.lotSize){
        [void]$done.Add((Lot-Row $pr $rc $true))
        $pr.lotNo++; $pr.lot = New-Acc; $pr.lotBelow = 0
      }
    }
  }

  $sum = if($pr){ "art='$($pr.article)' since=$($pr.start) tot=$($pr.total) ok=$($pr.ok) under=$($pr.under) over=$($pr.over) belowTU1=$($pr.belowTu1) mean={0:N1} std={1:N2} lot#{2} {3}/{4}" -f (Mean-Of $pr.acc), (Std-Of $pr.acc), $pr.lotNo, $pr.lot.n, $rc.lotSize } else { 'no production yet' }
  Log ("+{0} packs (panel clock {1:+0;-0} s)  {2}" -f $packs, $skew.TotalSeconds, $sum)

  if($Write -and $pr){
    $cn = Open-Db
    try {
      $cod = CodiceArticolo $pr.article; $start = DtIn $pr.start; $idp = IdProduzione $cod $start
      $p = [pscustomobject]@{ article=$pr.article; nominal=$rc.nominal; total=$pr.total; under=$pr.under; ok=$pr.ok;
        over=$pr.over; sottoTU1=$pr.belowTu1; mean=(Mean-Of $pr.acc); std=(Std-Of $pr.acc) }
      $a = Write-Produzione $cn $p $idp $start
      $lots = @($done) + @(if($pr.lot.n -gt 0){ Lot-Row $pr $rc $false })
      $res = foreach($l in $lots){ Write-LotRow $cn $idp $l }
      Log "produzione=$a lotto=$($res -join ',')"
    } finally { $cn.Close() }
  } elseif($done.Count){ Log ("DRYRUN completed lots: " + (($done | ForEach-Object { "#{0} n={1} mean={2:N1} std={3:N2}" -f $_.no, $_.size, $_.mean, $_.std }) -join '; ')) }
  if($finished){ Log "production $($finished.start) closed: tot=$($finished.total) ok=$($finished.ok)" }

  Save-State ([pscustomobject]@{ logHead=$head; logOffset=$off; recipe=$rc; prod=$pr })
}

# One lotto row per lot, keyed on (ID_produzione, progressivo_lotto).
function Write-LotRow($cn, [string]$idp, $l){
  $prm = @{ idp=$idp; oin=(DtIn $l.start); ofi=(DtIn $l.end); prog=[int]$l.no; dim=[int]$l.size;
    tu1=(ClampDec ([double]$l.tu1)); ct=0; cm=0; min=[int]$l.below; dev=(ClampDec ([double]$l.std));
    meff=(ClampDec ([double]$l.mean)); mcorr=(ClampDec ([double]$l.mean)); conf=($l.below -eq 0); fm=0; fnc=0; compl=[bool]$l.complete }
  $upd = @"
UPDATE lotto SET ora_inizio=@oin, ora_fine=@ofi, dimensione=@dim, soglia_TU1=@tu1, conf_minori_TU1=@min,
  deviazione_std=@dev, media_effettiva=@meff, media_corretta=@mcorr, lotto_conforme=@conf, completo=@compl
WHERE ID_produzione=@idp AND progressivo_lotto=@prog
"@
  if((Exec $cn $upd $prm) -gt 0){ return 'UPDATE' }
  $ins = @"
INSERT INTO lotto (ID_produzione,ora_inizio,ora_fine,progressivo_lotto,dimensione,soglia_TU1,
  campioni_controllo_toll,campioni_controllo_media,conf_minori_TU1,deviazione_std,media_effettiva,
  media_corretta,lotto_conforme,flag_media_corretta,flag_non_conformita,completo)
VALUES (@idp,@oin,@ofi,@prog,@dim,@tu1,@ct,@cm,@min,@dev,@meff,@mcorr,@conf,@fm,@fnc,@compl)
"@
  [void](Exec $cn $ins $prm); 'INSERT'
}

if($SetCredential){ Save-Credential; return }
if($Source -eq 'Log'){ Set-Item function:Tick ${function:Tick-Log} }

if($Loop){
  $mutex = New-Object System.Threading.Mutex($false,("Global\SpigaRicciarelli_{0}" -f $Line))
  if(-not $mutex.WaitOne(0)){ Log "another collector instance ($Line) already running; exiting." 'WARN'; return }
  Log "=== Ricciarelli collector ($Line $Ip`:$Port) starting  Write=$Write Interval=${IntervalSec}s ==="
  while($true){ try { Tick } catch { Log "tick error: $($_.Exception.Message)" 'ERROR' }; Start-Sleep -Seconds $IntervalSec }
} else {
  Log "=== Ricciarelli collector ($Line $Ip`:$Port) one-shot  Write=$Write ==="
  try { Tick } catch { Log "tick error: $($_.Exception.Message)" 'ERROR' }
}
