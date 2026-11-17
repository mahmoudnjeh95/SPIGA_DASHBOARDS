<#
  SpigaDashboards - TCP listener (receive-only)

  Pretends to be the server a Ricciarelli HMI connects to ("Ip controllo peso ad
  uso server" / "Porta tcp ad uso server") and logs every byte it receives, so the
  protocol can be decoded. It NEVER sends anything back.

  Usage:  .\tcp_listener.ps1 -Port 4660 -Minutes 30
  Output: tcp_capture_<port>_<timestamp>.log next to this script.
#>
param([int]$Port = 4660, [double]$Minutes = 30)

$log = Join-Path $PSScriptRoot ("tcp_capture_{0}_{1:yyyyMMdd_HHmmss}.log" -f $Port, (Get-Date))
function Log($m) { $l = "{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $m; Add-Content $log $l -Encoding UTF8; $l }

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, $Port)
$listener.Start()
Log "Listening on TCP $Port (receive-only) -> $log"
$end = (Get-Date).AddMinutes($Minutes)
$clients = New-Object System.Collections.ArrayList
try {
    while ((Get-Date) -lt $end) {
        while ($listener.Pending()) {
            $c = $listener.AcceptTcpClient()
            [void]$clients.Add($c)
            Log "CONNECT from $($c.Client.RemoteEndPoint)"
        }
        foreach ($c in @($clients)) {
            try {
                $s = $c.GetStream()
                if ($s.DataAvailable) {
                    $buf = New-Object byte[] 8192
                    $n = $s.Read($buf, 0, $buf.Length)
                    $b = $buf[0..($n-1)]
                    $hex = ($b | ForEach-Object { $_.ToString('X2') }) -join ' '
                    $asc = -join ($b | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })
                    Log ("[{0,4}] {1}  |{2}|" -f $n, $hex, $asc)
                } elseif ($c.Client.Poll(0, 'SelectRead') -and $c.Client.Available -eq 0) {
                    Log "DISCONNECT $($c.Client.RemoteEndPoint)"; $c.Close(); $clients.Remove($c)
                }
            } catch { Log "ERROR $_"; $clients.Remove($c) }
        }
        Start-Sleep -Milliseconds 20
    }
} finally { $listener.Stop(); $clients | ForEach-Object { $_.Close() }; Log "Stopped." }
