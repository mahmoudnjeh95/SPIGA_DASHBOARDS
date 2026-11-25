<#
  SpigaDashboards - Ricciarelli ControlTerm bridge  (runs ON the check-weigher)

  ControlTerm (D:\Ricciarelli, V.800r12) has no network port and no database:
  it talks to the weighing board over a serial line and writes EVERY protocol
  message to D:\Ricciarelli\Log\MessageLog.txt (UTF-16, rotated to .bak at
  ~30 MB, about every 40 min). This script follows that file, rebuilds the
  counters exactly as ControlTerm does (CpComm.CpHandler), and every minute
  writes one SNAPSHOT row to Stiavelli's stats.dbo.produzione, in the same
  shape as the Stiavelli lines, so the dashboards read it with no change:
    - one ID_produzione per production; counters cumulative since tempo_inizio
    - a new production starts when the article (program / codes 41-42) or the
      nominal weight changes; the previous one is closed with terminata = 1
    - tempo_fine = time of the snapshot

  Protocol used (board -> PC, each byte's low nibble, big-endian):
    0x41  code table: code i = 4 nibbles at 1+(i-1)*4. 1 nominal, 2 offset,
          3 under limit, 4 over limit, 7 over counts as OK, 41/42 article,
          43 lot size, 78 tenths mode, 105 program, 110 model (-> divisions)
    0x42  one pack: weight 4 nibbles (in divisions) + speed 3 nibbles
    0x55 / 0x56  running / stopped      0x6C  metal reject
  Read-only towards ControlTerm and the board: it only reads the log file.

  Nothing is lost if the database is unreachable: rows wait in pending.csv and
  are sent in order when it comes back. Every row is also kept in a daily CSV.

  Usage (test without database):  .\ricciarelli_ct_bridge.ps1 -DryRun
  Install as a service-like task:  .\ricciarelli_ct_bridge_install.ps1
#>
param(
    [string]$LogFile    = 'D:\Ricciarelli\Log\MessageLog.txt',
    [string]$DataDir    = 'D:\Spiga\bridge',
    [string]$Machine    = 'CW02',                 # = produzione.numero_linea / Machines.MachineId
    [string]$Server     = '',                     # e.g. 192.168.2.10\SQLEXPRESS  or  SERVEUR\SQLEXPRESS
    [string]$Database   = 'stats',
    [string]$Table      = 'dbo.produzione',
    [string]$User       = '',                     # SQL login; empty = Windows authentication
    [string]$Password   = '',
    [int]   $SnapshotSec = 60,
    [switch]$FromStart,                           # replay the whole current log (tests)
    [switch]$Once,                                # read what is there, write one snapshot, exit (tests)
    [switch]$DryRun                               # no database: CSV only
)

$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory $DataDir -Force | Out-Null }
$stateFile   = Join-Path $DataDir 'state.json'
$pendingFile = Join-Path $DataDir 'pending.csv'
$traceFile   = Join-Path $DataDir 'bridge.log'

function Say($m) {
    $l = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m
    $l
    try {
        if ((Test-Path $traceFile) -and (Get-Item $traceFile).Length -gt 5MB) { Move-Item $traceFile "$traceFile.old" -Force }
        Add-Content $traceFile $l -Encoding UTF8
    } catch {}
}

# ---------------------------------------------------------------- state
$st = [ordered]@{
    LogPos = 0L; Codes = @{}
    ProdId = ''; ProdStart = ''; Article = ''; Nominal = 0
    Total = 0; Under = 0; Ok = 0; Over = 0; Metal = 0
    Sum = 0.0; SumSq = 0.0; Min = $null; Max = $null
    Running = $false; Speed = 0; LastWeight = 0; LastMsg = ''
}
if (Test-Path $stateFile) {
    $j = Get-Content $stateFile -Raw | ConvertFrom-Json
    foreach ($p in $j.PSObject.Properties) { $st[$p.Name] = $p.Value }
    $c = @{}; if ($j.Codes) { foreach ($p in $j.Codes.PSObject.Properties) { $c[[int]$p.Name] = [int]$p.Value } }
    $st.Codes = $c
    Say "State loaded: production $($st.ProdId), $($st.Total) packs"
}
function Save-State {
    $o = [ordered]@{}; foreach ($k in $st.Keys) { $o[$k] = $st[$k] }
    $c = @{}; foreach ($k in $st.Codes.Keys) { $c["$k"] = $st.Codes[$k] }; $o.Codes = $c
    ($o | ConvertTo-Json -Depth 4) | Set-Content $stateFile -Encoding UTF8
}

function Code([int]$i) { if ($st.Codes.ContainsKey($i)) { [int]$st.Codes[$i] } else { 0 } }
function Divs {
    switch (Code 110) { 1 { 10 } 3 { 5 } 5 { 2 } 15 { 1 } 30 { 1 } default { 10 } }
}
function Granularity {
    switch (Code 110) { 1 { 1 } 3 { 2 } 5 { 5 } 15 { 10 } 30 { 20 } default { 1 } }
}
function Article-Code {
    $a = Code 41; $b = Code 42
    if ($a -or $b) { 'RIC{0:000}-{1:000}' -f $a, $b } else { 'RIC-P{0}' -f (Code 105) }
}

function Nib([byte[]]$b, [int]$off, [int]$n) {
    $v = 0; for ($k = 0; $k -lt $n; $k++) { $v = ($v -shl 4) + ($b[$off + $k] -band 15) }; $v
}

# ---------------------------------------------------------------- productions
function New-Production([datetime]$t) {
    $st.ProdStart = $t.ToString('yyyy-MM-dd HH:mm:ss', $inv)
    $st.ProdId    = '{0}_{1:yyyyMMdd_HHmmss}' -f $Machine, $t
    $st.Article   = Article-Code
    $st.Nominal   = Code 1
    foreach ($k in 'Total','Under','Ok','Over','Metal') { $st[$k] = 0 }
    $st.Sum = 0.0; $st.SumSq = 0.0; $st.Min = $null; $st.Max = $null
    Say "New production $($st.ProdId)  article $($st.Article)  nominal $($st.Nominal) g"
}

function Check-Production([datetime]$t) {
    if (-not $st.Codes.Count) { return }
    if (-not $st.ProdId) { New-Production $t; return }
    if ((Article-Code) -ne $st.Article -or (Code 1) -ne $st.Nominal) {
        if ($st.Total -gt 0) { Queue-Snapshot $t 1 }     # close the old one
        New-Production $t
    }
}

# ---------------------------------------------------------------- messages
function On-Message([datetime]$t, [int]$code, [byte[]]$b) {
    switch ($code) {
        0x41 {
            if ($b.Count -lt 1 + 119 * 4) { return }
            $c = @{}; for ($i = 1; $i -lt 120; $i++) { $c[$i] = Nib $b (1 + ($i - 1) * 4) 4 }
            $st.Codes = $c
            Check-Production $t
        }
        0x42 {
            if ($b.Count -lt 8 -or -not $st.Codes.Count) { return }
            if (-not $st.ProdId) { Check-Production $t }
            $div = Nib $b 1 4; $st.Speed = Nib $b 5 3
            $divs = Divs; $g = Granularity
            if ((Code 78) -eq 0) {                       # grams
                $w = [int][Math]::Floor($div / $divs)
                if ($w -ge (Code 2)) { $w -= (Code 2) }
                $r = $w % $g; $w = [int]([Math]::Floor($w / $g) * $g); if ($r -gt $g / 2) { $w += $g }
                $grams = [double]$w; $cmp = $w
            } else {                                     # tenths of gram
                if ($div -ge (Code 2)) { $div -= (Code 2) }
                $cmp = [int][Math]::Floor($div / $divs) * 10 + ($div % $divs)
                $grams = $cmp / 10.0
            }
            if ($cmp -ge (Code 4)) { $st.Over++ }
            elseif ($cmp -le (Code 3)) { $st.Under++ }
            else { $st.Ok++ }
            $st.Total++
            $st.Sum += $grams; $st.SumSq += $grams * $grams
            if ($null -eq $st.Min -or $grams -lt $st.Min) { $st.Min = $grams }
            if ($null -eq $st.Max -or $grams -gt $st.Max) { $st.Max = $grams }
            $st.LastWeight = $grams; $st.Running = $true
        }
        0x55 { $st.Running = $true }
        0x56 { $st.Running = $false; $st.Speed = 0 }
        0x6C { $st.Metal++ }
    }
}

$rx = [regex]'^(?<ts>\d\d/\d\d/\d{4} \d\d:\d\d:\d\d) ==>|^RECEIVED code: 0x(?<c>[0-9A-F]{2}), lenght: \d+(, bytes: (?<b>[0-9A-Fx-]+))?'
$script:lastTs = Get-Date
$script:partial = ''
function On-Text([string]$text) {
    $text = $script:partial + $text
    $lines = $text -split "`r?`n"
    $script:partial = $lines[-1]
    for ($i = 0; $i -lt $lines.Count - 1; $i++) {
        $m = $rx.Match($lines[$i]); if (-not $m.Success) { continue }
        if ($m.Groups['ts'].Success) {
            $script:lastTs = [datetime]::ParseExact($m.Groups['ts'].Value, 'dd/MM/yyyy HH:mm:ss', $inv); continue
        }
        $code = [Convert]::ToInt32($m.Groups['c'].Value, 16)
        if ($code -notin 0x41, 0x42, 0x55, 0x56, 0x6C) { continue }
        $bytes = if ($m.Groups['b'].Success) { [byte[]]($m.Groups['b'].Value.Split('-') | ForEach-Object { [Convert]::ToByte($_.Substring(2), 16) }) } else { [byte[]]@($code) }
        On-Message $script:lastTs $code $bytes
        $st.LastMsg = $script:lastTs.ToString('yyyy-MM-dd HH:mm:ss', $inv)
    }
}

# Reads [from, end) of a UTF-16 file that ControlTerm keeps open for writing.
function Read-From([string]$path, [long]$from) {
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        $len = $fs.Length; if ($from -lt 2) { $from = 2 }        # skip BOM
        $from -= $from % 2
        if ($len -le $from) { return @('', $len) }
        $fs.Position = $from
        $buf = New-Object byte[] ($len - $from); $n = 0
        while ($n -lt $buf.Length) { $r = $fs.Read($buf, $n, $buf.Length - $n); if ($r -le 0) { break }; $n += $r }
        $n -= $n % 2
        return @([Text.Encoding]::Unicode.GetString($buf, 0, $n), ($from + $n))
    } finally { $fs.Close() }
}

function Poll-Log {
    if (-not (Test-Path $LogFile)) { return }
    $len = (Get-Item $LogFile).Length
    if ($len -lt $st.LogPos) {                                   # rotated: finish the old file first
        $bak = [IO.Path]::ChangeExtension($LogFile, '.bak')
        if (Test-Path $bak) { $r = Read-From $bak $st.LogPos; On-Text $r[0] }
        $script:partial = ''; $st.LogPos = 0
        Say 'Log rotated.'
    }
    $r = Read-From $LogFile $st.LogPos
    if ($r[0]) { On-Text $r[0] }
    $st.LogPos = $r[1]
}

# ---------------------------------------------------------------- output
$cols = 'ID_produzione','numero_linea','tempo_inizio','tempo_fine','terminata','codice_articolo','operatore','turno',
        'linea_produzione','peso_netto_impostato','peso_max_registrato','peso_min_registrato','totale_confezioni',
        'confezioni_sotto_toll','confezioni_ok','confezioni_sopra_toll','confezioni_indeterminate','media_totale',
        'deviazione_std','espulsi_metal'

function Queue-Snapshot([datetime]$t, [int]$closed) {
    if (-not $st.ProdId) { return }
    $n = $st.Total
    $mean = if ($n) { $st.Sum / $n } else { 0 }
    $sd = if ($n -gt 1) { [Math]::Sqrt([Math]::Max(0, ($st.SumSq - $n * $mean * $mean) / ($n - 1))) } else { 0 }
    $f = { param($x) ([double]$x).ToString('0.##', $inv) }
    $row = @($st.ProdId, $Machine, $st.ProdStart, $t.ToString('yyyy-MM-dd HH:mm:ss', $inv), $closed, $st.Article, '', 0, '',
             $st.Nominal, (& $f ($(if ($null -ne $st.Max) { $st.Max } else { 0 }))), (& $f ($(if ($null -ne $st.Min) { $st.Min } else { 0 }))),
             $n, $st.Under, $st.Ok, $st.Over, 0, (& $f $mean), (& $f $sd), $st.Metal) -join ';'
    $day = Join-Path $DataDir ('produzione_{0:yyyyMMdd}.csv' -f $t)
    if (-not (Test-Path $day)) { Add-Content $day ($cols -join ';') -Encoding UTF8 }
    Add-Content $day $row -Encoding UTF8
    if (-not $DryRun) { Add-Content $pendingFile $row -Encoding UTF8 }
}

function Connection-String {
    $b = New-Object Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $Server; $b['Initial Catalog'] = $Database; $b['Connect Timeout'] = 10
    if ($User) { $b['User ID'] = $User; $b['Password'] = $Password } else { $b['Integrated Security'] = $true }
    $b.ConnectionString
}

function Flush-Pending {
    if ($DryRun -or -not (Test-Path $pendingFile)) { return }
    $rows = @(Get-Content $pendingFile -Encoding UTF8 | Where-Object { $_ })
    if (-not $rows.Count) { return }
    $cn = New-Object Data.SqlClient.SqlConnection (Connection-String)
    try {
        $cn.Open()
        $sql = "INSERT INTO $Table (" + ($cols -join ',') + ') VALUES (' + (($cols | ForEach-Object { "@$_" }) -join ',') + ')'
        $sent = 0
        foreach ($line in $rows) {
            $v = $line.Split(';')
            $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql
            for ($k = 0; $k -lt $cols.Count; $k++) {
                $val = $v[$k]
                $obj = switch ($k) {
                    { $_ -in 2, 3 }              { [datetime]::ParseExact($val, 'yyyy-MM-dd HH:mm:ss', $inv) }
                    { $_ -in 4, 7, 9, 12, 13, 14, 15, 16, 19 } { [int]$val }
                    { $_ -in 10, 11, 17, 18 }    { [double]::Parse($val, $inv) }
                    default                      { $val }
                }
                [void]$cmd.Parameters.AddWithValue("@$($cols[$k])", $obj)
            }
            [void]$cmd.ExecuteNonQuery(); $sent++
        }
        Remove-Item $pendingFile
        Say "Sent $sent row(s) to $Server/$Database"
    } catch {
        Say "Database not reachable ($($_.Exception.Message)); $($rows.Count) row(s) kept in pending.csv"
    } finally { $cn.Close() }
}

# ---------------------------------------------------------------- main
if (-not $st.LogPos -and -not $FromStart -and (Test-Path $LogFile)) {
    # First start: learn the code table from the existing logs, then follow new lines only.
    $bak = [IO.Path]::ChangeExtension($LogFile, '.bak')
    foreach ($p in @($bak, $LogFile)) {
        if (-not (Test-Path $p)) { continue }
        $r = Read-From $p 0
        $m = [regex]::Matches($r[0], 'RECEIVED code: 0x41, lenght: \d+, bytes: ([0-9A-Fx-]+)')
        if ($m.Count) {
            $b = [byte[]]($m[$m.Count - 1].Groups[1].Value.Split('-') | ForEach-Object { [Convert]::ToByte($_.Substring(2), 16) })
            On-Message (Get-Date) 0x41 $b
        }
        if ($p -eq $LogFile) { $st.LogPos = $r[1] }
    }
    Say "First start: code table $(if ($st.Codes.Count) { 'found' } else { 'not found yet' }), following new lines."
}
Say "Bridge $Machine : $LogFile -> $(if ($DryRun) { 'CSV only' } else { "$Server/$Database $Table" }), every $SnapshotSec s"

$next = (Get-Date).AddSeconds($SnapshotSec)
while ($true) {
    try { Poll-Log } catch { Say "Log read error: $($_.Exception.Message)" }
    $now = Get-Date
    if ($Once -or $now -ge $next) {
        # Only while ControlTerm is alive (it logs several messages a second):
        # no snapshot = the dashboard shows the line as disconnected.
        $alive = $st.LastMsg -and ($now - [datetime]::ParseExact($st.LastMsg, 'yyyy-MM-dd HH:mm:ss', $inv)).TotalSeconds -lt 120
        if ($alive -or $Once) { Queue-Snapshot $now 0 }
        Save-State
        Flush-Pending
        if ($st.ProdId) {
            Say ("{0}  total {1}  -{2} ok {3} +{4}  metal {5}  speed {6}  {7}" -f $st.Article, $st.Total, $st.Under, $st.Ok, $st.Over, $st.Metal, $st.Speed, $(if ($st.Running) { 'RUN' } else { 'STOP' }))
        }
        if ($Once) { break }
        $next = $now.AddSeconds($SnapshotSec)
    }
    Start-Sleep -Seconds 2
}
