<#
  SpigaDashboards - Ricciarelli check-weigher "info" query

  Protocol (from HMI_Ricciarelli_CP.exe, Form_Main.ClientConnectionRemote):
  The HMI listens on <porta_base_tcp_remota> .. +<num_porte_tcp_remote>-1 when
  "Porta tcp ad uso server" / "Num. porte ad uso server" are filled in.
  Client sends ASCII "info"; HMI replies one line, '|' separated, ending in CR:

   0 "@"                     11 std deviation total
   1 product code (eco41/42)  12 mean weight total
   2 speed (pcs/min)          13 std deviation lot
   3 count under (-) total    14 mean weight lot
   4 count OK total           15 minimum mean
   5 count over (+) total     16 nominal weight
   6 count under TU1 total    17 last net weight
   7 metal rejects            18 rejects for TU1
   8 lot number               19 rejects for mean
   9 TU1 threshold            20 pieces in lot
  10 count under TU1 lot
  Decimals use ',' (e.g. 500,12). Between replies the HMI sends a 0x00 keepalive.

  Read-only: "info" is exactly what Ricciarelli's own RemotePanel.exe sends.
  Usage:  .\ricciarelli_info.ps1 -Ip 192.168.2.40 -Port 4661 -Count 10
#>
param([string]$Ip = '192.168.2.40', [int]$Port = 4661, [int]$Count = 10, [int]$IntervalMs = 2000)

$names = 'Tag','Product','Speed','UnderTotal','OkTotal','OverTotal','UnderTU1Total','MetalRejects',
         'LotNumber','TU1','UnderTU1Lot','StdDevTotal','MeanTotal','StdDevLot','MeanLot','MinMean',
         'Nominal','LastNetWeight','RejectsTU1','RejectsMean','LotCount'

$log = Join-Path $PSScriptRoot ("ricciarelli_info_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
function Log($m) { $l = "{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $m; Add-Content $log $l -Encoding UTF8; $l }

$c = New-Object Net.Sockets.TcpClient
$c.Connect($Ip, $Port)
$s = $c.GetStream()
Log "Connected to ${Ip}:$Port -> $log"
$req = [Text.Encoding]::ASCII.GetBytes('info')
try {
    for ($i = 1; $i -le $Count; $i++) {
        while ($s.DataAvailable) { $null = $s.ReadByte() }          # drop keepalives
        $s.Write($req, 0, $req.Length)
        $sb = New-Object Text.StringBuilder
        $deadline = (Get-Date).AddMilliseconds(1500)
        while ((Get-Date) -lt $deadline) {
            if ($s.DataAvailable) {
                $b = $s.ReadByte()
                if ($b -eq 13) { break }
                if ($b -ne 0) { [void]$sb.Append([char]$b) }
            } else { Start-Sleep -Milliseconds 5 }
        }
        $line = $sb.ToString()
        Log "#$i RAW: $line"
        $f = $line.Split('|')
        if ($f[0] -eq '@') {
            $o = [ordered]@{}; for ($k = 1; $k -lt [Math]::Min($f.Count, $names.Count); $k++) { $o[$names[$k]] = $f[$k] }
            Log ("     " + (($o.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '  '))
        } else { Log "     (no valid reply)" }
        Start-Sleep -Milliseconds $IntervalMs
    }
} finally { $c.Close(); Log "Closed." }
