<#
  SpigaDashboards - serial line sniffer (listen-only)

  Records what travels on an RS-232 line (e.g. the Ricciarelli check-weigher's
  COM1 link between the weighing board and the HMI) so its protocol can be decoded.

  The port is opened read-only in practice: this script NEVER writes to it.
  Wire the USB-RS232 adapter through a tap / "Y" cable so the machine keeps
  working: adapter RX (pin 2) + GND (pin 5) on the line you want to hear.

  Usage:
    .\serial_sniffer.ps1 -ListPorts
    .\serial_sniffer.ps1 -Port COM5 -AutoBaud            # try common speeds, 10 s each
    .\serial_sniffer.ps1 -Port COM5 -Baud 9600 -Minutes 10
  Output: serial_capture_<port>_<baud>_<timestamp>.log next to this script
          (hex + ASCII, one line per burst, with millisecond timestamps).
#>
param(
    [string]$Port,
    [int]$Baud = 9600,
    [ValidateSet('None','Odd','Even')][string]$Parity = 'None',
    [ValidateSet(7,8)][int]$DataBits = 8,
    [double]$Minutes = 5,
    [switch]$AutoBaud,
    [switch]$ListPorts
)

if ($ListPorts -or -not $Port) {
    "Available serial ports:"
    Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match '\(COM\d+\)' } |
        ForEach-Object { "  " + $_.Name }
    if (-not $Port) { return }
}

function Format-Burst([byte[]]$bytes) {
    $hex = ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' '
    $asc = -join ($bytes | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })
    "$hex  |$asc|"
}

function Open-Port([int]$b) {
    $sp = New-Object System.IO.Ports.SerialPort $Port, $b, ([System.IO.Ports.Parity]$Parity), $DataBits, ([System.IO.Ports.StopBits]::One)
    $sp.Handshake   = 'None'
    $sp.DtrEnable   = $false   # don't drive control lines on the machine's link
    $sp.RtsEnable   = $false
    $sp.ReadTimeout = 50
    $sp.Open()
    $sp
}

# Reads for $seconds, returns @{ Bytes; Printable; Bursts }; logs to $log if given.
function Capture([int]$b, [double]$seconds, [string]$log) {
    $sp = Open-Port $b
    $total = 0; $printable = 0; $bursts = 0
    $buf = New-Object System.Collections.Generic.List[byte]
    $last = [DateTime]::Now
    $end = [DateTime]::Now.AddSeconds($seconds)
    try {
        while ([DateTime]::Now -lt $end) {
            $n = $sp.BytesToRead
            if ($n -gt 0) {
                $chunk = New-Object byte[] $n
                $null = $sp.Read($chunk, 0, $n)
                $buf.AddRange($chunk); $last = [DateTime]::Now
            } elseif ($buf.Count -gt 0 -and ([DateTime]::Now - $last).TotalMilliseconds -gt 20) {
                # 20 ms of silence = end of one message burst
                $arr = $buf.ToArray(); $buf.Clear(); $bursts++
                $total += $arr.Length
                $printable += ($arr | Where-Object { ($_ -ge 32 -and $_ -le 126) -or $_ -in 10,13 }).Count
                if ($log) {
                    $line = "{0:HH:mm:ss.fff}  [{1,4}]  {2}" -f $last, $arr.Length, (Format-Burst $arr)
                    Add-Content -Path $log -Value $line -Encoding UTF8
                    $line
                }
            } else { Start-Sleep -Milliseconds 5 }
        }
    } finally { $sp.Close() }
    @{ Bytes = $total; Printable = $printable; Bursts = $bursts }
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

if ($AutoBaud) {
    "Trying common speeds on $Port (10 s each, listen-only)..."
    $results = foreach ($b in 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200) {
        $r = Capture $b 10 $null
        $ratio = if ($r.Bytes) { [math]::Round(100 * $r.Printable / $r.Bytes) } else { 0 }
        "  {0,6} baud : {1,5} bytes, {2,3} bursts, {3,3}% readable text" -f $b, $r.Bytes, $r.Bursts, $ratio | Write-Host
        [pscustomobject]@{ Baud = $b; Bytes = $r.Bytes; Ratio = $ratio }
    }
    $best = $results | Where-Object Bytes | Sort-Object Ratio, Bytes -Descending | Select-Object -First 1
    if ($best) { "Most likely speed: $($best.Baud) baud. Re-run with -Baud $($best.Baud) -Minutes 10 to record." }
    else { "No data at any speed. Check the wiring (adapter RX on the talking pin, GND on pin 5) and that the machine is running." }
    return
}

$log = Join-Path $PSScriptRoot ("serial_capture_{0}_{1}_{2}.log" -f $Port, $Baud, $stamp)
"Recording $Port at $Baud baud ($DataBits$($Parity[0])1) for $Minutes min -> $log  (listen-only, Ctrl+C to stop)"
$r = Capture $Baud ($Minutes * 60) $log
"Done: $($r.Bytes) bytes in $($r.Bursts) bursts. Log: $log"
