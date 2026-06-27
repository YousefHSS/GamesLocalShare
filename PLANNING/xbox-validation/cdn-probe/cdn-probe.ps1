<#
  cdn-probe.ps1 - find out WHERE and HOW Gaming Services downloads game content.

  Run this ELEVATED while an Xbox game is actively downloading/repairing in the Xbox app.
  It samples live TCP connections, keeps only the ones owned by Gaming Services / Xbox
  download processes, resolves the remote IPs back to hostnames, and tallies port usage
  (80 = HTTP = cacheable;  443 = HTTPS = needs TLS interception).

  Decision it answers:
    * Mostly port 80  -> transparent LAN caching proxy is feasible (serve ciphertext blobs).
    * Mostly port 443 -> would need TLS MITM; check for cert pinning before investing.

  Usage (elevated):
    .\cdn-probe.ps1                 # samples for 60s, every 2s
    .\cdn-probe.ps1 -Seconds 120    # sample longer

  ASCII only. Windows PowerShell 5.1 compatible.
#>
param(
  [int]$Seconds = 60,
  [int]$IntervalMs = 2000,
  [string]$OutDir = "F:\Documents\GamesLocalShare\PLANNING\xbox-validation\cdn-probe\logs"
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (need owning-process info for connections)." -ForegroundColor Red; return }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$tsv = Join-Path $OutDir "cdn-conns-$stamp.tsv"
"time`tprocess`tpid`tremoteIP`tremotePort`thost" | Out-File -FilePath $tsv -Encoding ASCII

# Processes that do Xbox/Store/GamingServices downloading. We match by name (case-insensitive
# 'like'); svchost is included because some download paths run inside a service host.
$nameLike = @('GamingServices','XboxGip','XboxPcApp','GameBar','Microsoft.GamingServices','svchost','BackgroundTransferHost','wsappx','wuauclt','dosvc')

$dnsCache = @{}
function Resolve-IPName($ip) {
  if ($dnsCache.ContainsKey($ip)) { return $dnsCache[$ip] }
  $name = ''
  try { $name = [System.Net.Dns]::GetHostEntry($ip).HostName } catch { $name = '' }
  $dnsCache[$ip] = $name
  return $name
}

Write-Host "=== Gaming Services CDN probe ===" -ForegroundColor Cyan
Write-Host ("Sampling every {0} ms for {1} s. START A GAME DOWNLOAD NOW if not already running." -f $IntervalMs, $Seconds) -ForegroundColor Yellow
Write-Host ("Logging to: {0}" -f $tsv)
Write-Host ""

$portTally = @{}      # port -> hit count
$hostTally = @{}      # host -> hit count
$seen      = @{}      # dedupe key process|ip|port within a single sample handled by hashset per loop
$deadline  = (Get-Date).AddSeconds($Seconds)

while ((Get-Date) -lt $deadline) {
  $now = (Get-Date -Format 'HH:mm:ss')
  $conns = $null
  try {
    $conns = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
             Where-Object { $_.RemotePort -in 80,443 -and $_.RemoteAddress -notin '127.0.0.1','::1' -and $_.RemoteAddress -notlike 'fe80*' -and $_.RemoteAddress -notlike '192.168.*' -and $_.RemoteAddress -notlike '10.*' }
  } catch {}
  if ($conns) {
    foreach ($c in $conns) {
      $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
      $pname = if ($proc) { $proc.ProcessName } else { ('pid' + $c.OwningProcess) }
      $match = $false
      foreach ($nl in $nameLike) { if ($pname -like ("*" + $nl + "*")) { $match = $true; break } }
      if (-not $match) { continue }
      $ip = $c.RemoteAddress; $port = $c.RemotePort
      $h = Resolve-IPName $ip
      $line = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}" -f $now,$pname,$c.OwningProcess,$ip,$port,$h
      Add-Content -Path $tsv -Value $line
      $key = "$pname|$ip|$port"
      if (-not $seen.ContainsKey($key)) {
        $seen[$key] = $true
        Write-Host ("  {0}  {1} (pid {2})  ->  {3}:{4}  {5}" -f $now,$pname,$c.OwningProcess,$ip,$port,$h) -ForegroundColor White
      }
      if ($portTally.ContainsKey($port)) { $portTally[$port]++ } else { $portTally[$port] = 1 }
      $hk = if ($h) { $h } else { $ip }
      if ($hostTally.ContainsKey($hk)) { $hostTally[$hk]++ } else { $hostTally[$hk] = 1 }
    }
  }
  Start-Sleep -Milliseconds $IntervalMs
}

Write-Host ""
Write-Host "=== PORT TALLY (samples seen) ===" -ForegroundColor Cyan
if ($portTally.Count -eq 0) {
  Write-Host "  NOTHING captured. Either no game was downloading, or the downloader process" -ForegroundColor Yellow
  Write-Host "  name is not in the match list. Re-run with a download active; if still empty," -ForegroundColor Yellow
  Write-Host "  tell me and we'll widen the capture (pktmon full trace)." -ForegroundColor Yellow
} else {
  $portTally.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    $label = if ($_.Key -eq 80) {'HTTP  (cacheable - GOOD)'} elseif ($_.Key -eq 443) {'HTTPS (needs TLS MITM)'} else {'other'}
    Write-Host ("  port {0,-4} : {1,5} hits   {2}" -f $_.Key, $_.Value, $label)
  }
  Write-Host ""
  Write-Host "=== TOP HOSTS ===" -ForegroundColor Cyan
  $hostTally.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 25 | ForEach-Object {
    Write-Host ("  {0,5}  {1}" -f $_.Value, $_.Key)
  }
}
Write-Host ""
Write-Host ("Full per-sample log: {0}" -f $tsv) -ForegroundColor Cyan
