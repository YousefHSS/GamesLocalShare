<#
  do-probe-proxy.ps1 - Delivery Optimization cache-host PROBE.
  Listens on http://+:80/, LOGS every request (this is the point of the probe), and
  best-effort forwards each request to its real origin (Host header) so a download can
  actually proceed through us. Run elevated on the CACHE machine (e.g. the desktop).

  Goal: find out whether Delivery Optimization sends Xbox GAME content requests here when
  another PC has DOCacheHost pointed at this machine.

  Usage (elevated):
    .\do-probe-proxy.ps1
    .\do-probe-proxy.ps1 -Port 80 -LogOnly      # don't forward; just log + return 504 (fast)

  Stop with Ctrl+C.  ASCII only. Windows PowerShell 5.1 compatible.
#>
param(
  [int]$Port = 80,
  [switch]$LogOnly,
  [string]$LogDir = "F:\Documents\GamesLocalShare\PLANNING\xbox-validation\do-probe\logs"
)

$ErrorActionPreference = 'Stop'

# --- admin check ---
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run this in an ELEVATED PowerShell (binding port 80 needs admin)." -ForegroundColor Red; return }

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $LogDir "proxy-$stamp.log"
$tsv = Join-Path $LogDir "requests-$stamp.tsv"
"time`tclientIP`tmethod`thostHeader`trawUrl`trange`tstatus`tbytes`tuserAgent" | Out-File -FilePath $tsv -Encoding ASCII

# --- show who is on :80 and our LAN IPs ---
Write-Host "=== DO cache-host probe ===" -ForegroundColor Cyan
$busy = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($busy) {
  Write-Host ("WARNING: port {0} already has a listener (PID(s): {1})." -f $Port, (($busy.OwningProcess | Sort-Object -Unique) -join ',')) -ForegroundColor Yellow
  foreach ($p in ($busy.OwningProcess | Sort-Object -Unique)) { $pr = Get-Process -Id $p -ErrorAction SilentlyContinue; if ($pr) { Write-Host ("   PID {0} = {1}" -f $p, $pr.ProcessName) -ForegroundColor Yellow } }
  Write-Host "   Free port 80 first (e.g. stop Laragon/Apache/IIS/'World Wide Web Publishing Service')." -ForegroundColor Yellow
}
Write-Host "This machine's LAN IPv4 addresses (use one of these for DOCacheHost on the OTHER PC):" -ForegroundColor Green
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.*' -and $_.IPAddress -ne '127.0.0.1' } | ForEach-Object { Write-Host ("   {0}   ({1})" -f $_.IPAddress, $_.InterfaceAlias) -ForegroundColor Green }
Write-Host ("Logging to: {0}" -f $log)
Write-Host ("Requests TSV: {0}" -f $tsv)
Write-Host ("Mode: {0}" -f ($(if ($LogOnly) {'LOG-ONLY (504 fallback)'} else {'FORWARDING'})))
Write-Host ""

# --- listener ---
$listener = New-Object System.Net.HttpListener
$prefix = "http://+:$Port/"
$listener.Prefixes.Add($prefix)
try { $listener.Start() }
catch {
  Write-Host ("Start failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
  Write-Host "Trying to add a URL ACL reservation, then retrying..." -ForegroundColor Yellow
  & netsh http add urlacl url=$prefix user=Everyone | Out-Null
  $listener = New-Object System.Net.HttpListener; $listener.Prefixes.Add($prefix); $listener.Start()
}
Write-Host ("LISTENING on {0}  -- press Ctrl+C to stop." -f $prefix) -ForegroundColor Cyan
Write-Host ""

Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $true
$handler.AutomaticDecompression = [System.Net.DecompressionMethods]::None
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(30)

$selfIps = @((Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress)) + @('localhost','127.0.0.1')

function Write-Line($s, $color) { if ($color) { Write-Host $s -ForegroundColor $color } else { Write-Host $s }; Add-Content -Path $log -Value $s }

try {
  while ($listener.IsListening) {
    $ctx = $listener.GetContext()    # blocking
    $req = $ctx.Request
    $res = $ctx.Response
    $now = (Get-Date -Format 'HH:mm:ss.fff')
    $clientIp = $req.RemoteEndPoint.Address.ToString()
    $hostHdr = $req.UserHostName
    $raw = $req.RawUrl
    $range = $req.Headers['Range']
    $ua = $req.Headers['User-Agent']
    $status = 0; $bytes = 0

    # LOG IMMEDIATELY (the actual probe data)
    Write-Line ("[{0}] {1} {2}  Host={3}  Range={4}" -f $now, $clientIp, $req.HttpMethod, $raw, $hostHdr, $range) 'White'

    try {
      $originHost = ($hostHdr -split ':')[0]
      if ($LogOnly -or [string]::IsNullOrWhiteSpace($originHost) -or ($selfIps -contains $originHost)) {
        $status = 504; $res.StatusCode = 504
      } else {
        $target = "http://$hostHdr$raw"
        $msg = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $target)
        if ($range) { [void]$msg.Headers.TryAddWithoutValidation('Range', $range) }
        if ($ua) { [void]$msg.Headers.TryAddWithoutValidation('User-Agent', $ua) }
        $resp = $http.SendAsync($msg, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        $res.StatusCode = $status
        $ct = $resp.Content.Headers.ContentType; if ($ct) { $res.ContentType = $ct.ToString() }
        $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buf = New-Object byte[] 131072
        while (($n = $stream.Read($buf,0,$buf.Length)) -gt 0) { $res.OutputStream.Write($buf,0,$n); $bytes += $n }
        $stream.Dispose(); $resp.Dispose()
      }
    } catch {
      $status = 502; try { $res.StatusCode = 502 } catch {}
      Write-Line ("    forward error: {0}" -f $_.Exception.Message) 'DarkYellow'
    } finally {
      try { $res.OutputStream.Close() } catch {}
      try { $res.Close() } catch {}
    }

    $color = if ($status -ge 200 -and $status -lt 300) { 'Green' } elseif ($status -eq 504 -or $status -eq 502) { 'DarkYellow' } else { 'Gray' }
    Write-Line ("    -> {0}  {1} bytes  (origin {2})" -f $status, $bytes, $hostHdr) $color
    "{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}`t{8}" -f $now,$clientIp,$req.HttpMethod,$hostHdr,$raw,$range,$status,$bytes,$ua | Add-Content -Path $tsv
  }
} finally {
  $listener.Stop(); $listener.Close()
  Write-Host ""; Write-Host ("Stopped. Log: {0}" -f $log) -ForegroundColor Cyan
}
