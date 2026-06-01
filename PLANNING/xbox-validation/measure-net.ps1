# Live cumulative network-received monitor, used to measure how much a
# "Verify & Repair" click actually pulls from the CDN.
#
# Uses the same physical-NIC selection as the overlay's verdict measurement,
# so numbers are comparable. Prints a running total every few seconds.
#
# Usage:
#   1. Run this:   .\measure-net.ps1
#   2. It prints "BASELINE SET" - now click Verify & Repair in the Xbox app.
#   3. Watch the cumulative MB. When it stops growing, press Ctrl+C.
#      The final line is the total pulled during the window.
[CmdletBinding()]
param(
    [int]$IntervalSeconds = 3,
    [int]$MaxSeconds = 3600,
    [string]$Out
)

function Select-PhysicalNic {
    (Get-NetAdapter | Where-Object {
        $_.Status -eq 'Up' -and $_.HardwareInterface -eq $true -and
        $_.InterfaceDescription -notmatch 'VMware|Hyper-V|Virtual|VirtualBox|WireGuard|Wintun|TAP|VPN'
    } | Sort-Object -Property LinkSpeed -Descending | Select-Object -First 1).Name
}

$nic = Select-PhysicalNic
if (-not $nic) { Write-Host "No physical NIC found." -ForegroundColor Red; exit 1 }

$base = (Get-NetAdapterStatistics -Name $nic).ReceivedBytes
$startTime = Get-Date
Write-Host ("BASELINE SET on NIC '{0}'  (rx={1:N0} bytes)" -f $nic, $base) -ForegroundColor Green
Write-Host "Now click Verify & Repair in the Xbox app. Ctrl+C to stop." -ForegroundColor Yellow
Write-Host ""

$peakMB = 0
$log = New-Object System.Collections.Generic.List[string]
try {
    while (((Get-Date) - $startTime).TotalSeconds -lt $MaxSeconds) {
        Start-Sleep -Seconds $IntervalSeconds
        $now = (Get-NetAdapterStatistics -Name $nic).ReceivedBytes
        $deltaMB = [math]::Round(($now - $base) / 1MB, 1)
        if ($deltaMB -gt $peakMB) { $peakMB = $deltaMB }
        $elapsed = [int]((Get-Date) - $startTime).TotalSeconds
        $line = ("t+{0,5}s   received = {1,10:N1} MB  ({2:N2} GB)" -f $elapsed, $deltaMB, ($deltaMB/1024))
        Write-Host $line
        $log.Add($line)
    }
} finally {
    $finalMB = [math]::Round(((Get-NetAdapterStatistics -Name $nic).ReceivedBytes - $base) / 1MB, 1)
    Write-Host ""
    Write-Host ("=== TOTAL RECEIVED: {0:N1} MB  ({1:N2} GB) ===" -f $finalMB, ($finalMB/1024)) -ForegroundColor Cyan
    if ($Out) {
        $log.Add(("TOTAL RECEIVED: {0:N1} MB" -f $finalMB))
        [System.IO.File]::WriteAllLines($Out, $log)
        Write-Host ("Wrote $Out") -ForegroundColor Green
    }
}
