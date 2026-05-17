<#
.SYNOPSIS
    Sample total NIC bytes received over a window. Used to estimate how much
    the Xbox app actually downloads during a recognition step.

.PARAMETER DurationSeconds
    How long to sample (default 60).

.PARAMETER InterfaceAlias
    Optional NIC alias to filter (e.g. "Wi-Fi", "Ethernet"). If omitted, all
    physical adapters are summed.

.PARAMETER OutPath
    Optional JSON path to write the result to.

.EXAMPLE
    .\measure-network.ps1 -DurationSeconds 120 -InterfaceAlias "Wi-Fi"
#>
[CmdletBinding()]
param(
    [int]    $DurationSeconds = 60,
    [string] $InterfaceAlias,
    [string] $OutPath
)

$ErrorActionPreference = 'Stop'

function Get-NicBytes {
    param([string] $Alias)
    $stats = if ($Alias) {
        Get-NetAdapterStatistics -Name $Alias -ErrorAction Stop
    } else {
        Get-NetAdapter -Physical -ErrorAction Stop |
            Where-Object Status -eq 'Up' |
            ForEach-Object { Get-NetAdapterStatistics -Name $_.Name -ErrorAction SilentlyContinue }
    }

    $rx = ($stats | Measure-Object -Property ReceivedBytes -Sum).Sum
    $tx = ($stats | Measure-Object -Property SentBytes     -Sum).Sum
    [pscustomobject]@{ Received = [int64]$rx; Sent = [int64]$tx }
}

$start = Get-NicBytes -Alias $InterfaceAlias
$startedAt = Get-Date
Write-Host ("Sampling for {0}s... (start: rx={1:N0} tx={2:N0})" -f $DurationSeconds, $start.Received, $start.Sent) -ForegroundColor Cyan
Start-Sleep -Seconds $DurationSeconds
$end = Get-NicBytes -Alias $InterfaceAlias
$endedAt = Get-Date

$deltaRx = $end.Received - $start.Received
$deltaTx = $end.Sent     - $start.Sent

$result = [pscustomobject]@{
    StartedAtUtc   = $startedAt.ToUniversalTime().ToString('o')
    EndedAtUtc     = $endedAt.ToUniversalTime().ToString('o')
    DurationSecs   = $DurationSeconds
    InterfaceAlias = $InterfaceAlias
    ReceivedBytes  = $deltaRx
    SentBytes      = $deltaTx
    ReceivedMB     = [math]::Round($deltaRx / 1MB, 2)
    SentMB         = [math]::Round($deltaTx / 1MB, 2)
}

Write-Host ""
Write-Host ("Received : {0:N0} bytes ({1:N2} MB)" -f $deltaRx, $result.ReceivedMB) -ForegroundColor Green
Write-Host ("Sent     : {0:N0} bytes ({1:N2} MB)" -f $deltaTx, $result.SentMB)

if ($OutPath) {
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $OutPath -Encoding UTF8
    Write-Host ("Wrote    : {0}" -f $OutPath)
}

$result
