<#
  cdn-host-sniff.ps1 - read the HTTP request line + Host header that Gaming Services sends
  for game content (port 80 is plaintext, so the hostname + URL path are right there).

  Uses a raw socket in promiscuous mode (SIO_RCVALL). Run ELEVATED. Captures outbound
  port-80 HTTP requests, prints "GET <path>  Host: <host>" for each, and tallies hosts.
  This gives us the EXACT hostname(s) to redirect for the LAN cache.

  Usage (elevated):
    .\cdn-host-sniff.ps1                 # auto-detect LAN IP, capture 60s
    .\cdn-host-sniff.ps1 -Seconds 120
    .\cdn-host-sniff.ps1 -LocalIP 192.168.1.244

  Start/keep a game DOWNLOAD running while this captures. ASCII only. PS 5.1 compatible.
#>
param(
  [string]$LocalIP,
  [int]$Seconds = 60,
  [string]$OutDir = "F:\Documents\GamesLocalShare\PLANNING\xbox-validation\cdn-probe\logs"
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (raw sockets need admin)." -ForegroundColor Red; return }

if (-not $LocalIP) {
  $cfg = Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1
  if ($cfg) { $LocalIP = $cfg.IPv4Address.IPAddress }
}
if (-not $LocalIP) { Write-Host "ERROR: could not auto-detect LAN IP; pass -LocalIP <addr>." -ForegroundColor Red; return }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$tsv = Join-Path $OutDir "cdn-hosts-$stamp.tsv"
"time`tmethod`thost`tpath" | Out-File -FilePath $tsv -Encoding ASCII

Write-Host "=== HTTP Host sniffer (port 80) ===" -ForegroundColor Cyan
Write-Host ("Binding raw socket to {0}; capturing {1}s. KEEP A DOWNLOAD RUNNING." -f $LocalIP, $Seconds) -ForegroundColor Yellow
Write-Host ("Logging to: {0}" -f $tsv); Write-Host ""

$sock = New-Object System.Net.Sockets.Socket([System.Net.Sockets.AddressFamily]::InterNetwork, [System.Net.Sockets.SocketType]::Raw, [System.Net.Sockets.ProtocolType]::IP)
$sock.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse($LocalIP), 0)))
$sock.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::IP, [System.Net.Sockets.SocketOptionName]::HeaderIncluded, $true)
# SIO_RCVALL = promiscuous: receive all IP packets on this interface (in+out).
[void]$sock.IOControl([System.Net.Sockets.IOControlCode]::ReceiveAll, [BitConverter]::GetBytes(1), $null)
$sock.ReceiveTimeout = 1000

$buf = New-Object byte[] 65535
$hostTally = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$ascii = [System.Text.Encoding]::ASCII

try {
  while ((Get-Date) -lt $deadline) {
    $n = 0
    try { $n = $sock.Receive($buf) } catch { continue }   # timeout -> loop, re-check deadline
    if ($n -le 0) { continue }
    $ihl = ($buf[0] -band 0x0F) * 4
    if ($buf[9] -ne 6) { continue }                        # 6 = TCP
    if ($n -lt ($ihl + 20)) { continue }
    # TCP: bytes [ihl]=srcPortHi,[ihl+1]=srcPortLo,[ihl+2]=dstPortHi,[ihl+3]=dstPortLo
    $dstPort = ($buf[$ihl+2] -shl 8) -bor $buf[$ihl+3]
    if ($dstPort -ne 80) { continue }                      # only OUTBOUND requests to :80
    $tcpHdr = (($buf[$ihl+12] -band 0xF0) -shr 4) * 4
    $payOff = $ihl + $tcpHdr
    $payLen = $n - $payOff
    if ($payLen -le 16) { continue }
    $text = $ascii.GetString($buf, $payOff, [Math]::Min($payLen, 2048))
    if ($text -notmatch '^(GET|HEAD|POST) ') { continue }
    $method = ''; $path = ''; $hostHdr = ''
    $lines = $text -split "`r`n"
    if ($lines[0] -match '^(\S+)\s+(\S+)\s+HTTP') { $method = $matches[1]; $path = $matches[2] }
    foreach ($l in $lines) { if ($l -match '^(?i)Host:\s*(.+)$') { $hostHdr = $matches[1].Trim(); break } }
    if (-not $hostHdr) { continue }
    $now = (Get-Date -Format 'HH:mm:ss')
    $shortPath = if ($path.Length -gt 90) { $path.Substring(0,90) + '...' } else { $path }
    Write-Host ("  {0}  {1}  Host={2}  Path={3}" -f $now,$method,$hostHdr,$shortPath) -ForegroundColor White
    "{0}`t{1}`t{2}`t{3}" -f $now,$method,$hostHdr,$path | Add-Content -Path $tsv
    if ($hostTally.ContainsKey($hostHdr)) { $hostTally[$hostHdr]++ } else { $hostTally[$hostHdr] = 1 }
  }
}
finally {
  try { [void]$sock.IOControl([System.Net.Sockets.IOControlCode]::ReceiveAll, [BitConverter]::GetBytes(0), $null) } catch {}
  $sock.Close()
}

Write-Host ""
Write-Host "=== HOST TALLY (HTTP requests captured) ===" -ForegroundColor Cyan
if ($hostTally.Count -eq 0) {
  Write-Host "  No HTTP requests captured. Make sure a download was active AND that you used the" -ForegroundColor Yellow
  Write-Host "  interface that reaches the internet (try -LocalIP with the right adapter address)." -ForegroundColor Yellow
} else {
  $hostTally.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  {0,6}  {1}" -f $_.Value, $_.Key) -ForegroundColor Green
  }
}
Write-Host ""
Write-Host ("Full request log: {0}" -f $tsv) -ForegroundColor Cyan
