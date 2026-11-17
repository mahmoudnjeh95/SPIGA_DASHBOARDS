<#
  SpigaDashboards - Ricciarelli check-weigher collector (continuous, read-only)

  Keeps one TCP connection to the HMI server port and sends "info" every
  -IntervalSec seconds (same request as Ricciarelli's RemotePanel.exe).
  Reconnects automatically if the HMI restarts or the cable drops.

  Output: data\ricciarelli_<machine>_<yyyyMMdd>.csv next to this script
          one row per poll when something changed, plus one row per minute
          as a heartbeat. Columns = timestamp + the 20 fields of the reply.
  Field list: see ricciarelli_info.ps1.

  Usage:  .\ricciarelli_collector.ps1 -Ip 192.168.2.40 -Port 4661 -Machine CW01
#>
param(
    [string]$Ip = '192.168.2.40',
    [int]$Port = 4661,
    [string]$Machine = 'RICCIARELLI',
    [double]$IntervalSec = 2,
    [int]$HeartbeatSec = 60
)

$names = 'Product','Speed','UnderTotal','OkTotal','OverTotal','UnderTU1Total','MetalRejects',
         'LotNumber','TU1','UnderTU1Lot','StdDevTotal','MeanTotal','StdDevLot','MeanLot','MinMean',
         'Nominal','LastNetWeight','RejectsTU1','RejectsMean','LotCount'
$dir = Join-Path $PSScriptRoot 'data'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir | Out-Null }
$req = [Text.Encoding]::ASCII.GetBytes('info')
$inv = [Globalization.CultureInfo]::InvariantCulture

function Say($m) { "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m }

function Query($s) {
    while ($s.DataAvailable) { $null = $s.ReadByte() }            # drop 0x00 keepalives
    $s.Write($req, 0, $req.Length)
    $sb = New-Object Text.StringBuilder
    $deadline = (Get-Date).AddMilliseconds(2000)
    while ((Get-Date) -lt $deadline) {
        if ($s.DataAvailable) {
            $b = $s.ReadByte()
            if ($b -lt 0) { throw 'connection closed' }
            if ($b -eq 13) { return $sb.ToString() }
            if ($b -ne 0) { [void]$sb.Append([char]$b) }
        } else { Start-Sleep -Milliseconds 5 }
    }
    throw 'no reply within 2 s'
}

$last = ''; $lastWrite = [DateTime]::MinValue
Say "Collector for $Machine -> ${Ip}:$Port every $IntervalSec s, CSV in $dir"
while ($true) {
    $c = $null
    try {
        $c = New-Object Net.Sockets.TcpClient
        $c.Connect($Ip, $Port)
        $s = $c.GetStream()
        Say 'Connected.'
        while ($true) {
            $line = Query $s
            $f = $line.Split('|')
            if ($f[0] -ne '@' -or $f.Count -lt 21) { Say "Ignored reply: $line"; Start-Sleep -Seconds $IntervalSec; continue }
            $vals = $f[1..20]
            $key = $vals -join '|'
            $now = Get-Date
            if ($key -ne $last -or ($now - $lastWrite).TotalSeconds -ge $HeartbeatSec) {
                $csv = Join-Path $dir ("ricciarelli_{0}_{1:yyyyMMdd}.csv" -f $Machine, $now)
                if (-not (Test-Path $csv)) { Add-Content $csv ('Timestamp;' + ($names -join ';')) -Encoding UTF8 }
                Add-Content $csv ($now.ToString('yyyy-MM-dd HH:mm:ss', $inv) + ';' + ($vals -join ';')) -Encoding UTF8
                if ($key -ne $last) {
                    Say ("lot {0}  OK {1}  -{2}  +{3}  metal {4}  lotCount {5}  mean {6}  speed {7}" -f `
                        $vals[7], $vals[3], $vals[2], $vals[4], $vals[6], $vals[19], $vals[13], $vals[1])
                }
                $last = $key; $lastWrite = $now
            }
            Start-Sleep -Milliseconds ([int]($IntervalSec * 1000))
        }
    } catch {
        Say "Link lost: $($_.Exception.Message). Retrying in 10 s."
    } finally { if ($c) { $c.Close() } }
    Start-Sleep -Seconds 10
}
