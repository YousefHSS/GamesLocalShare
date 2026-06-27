<#
  prime-blob.ps1 - pre-populate the LAN cache with a game's .msixvc (or any CDN object).

  Downloads the full object from the REAL origin into the cache layout that xbox-cache-proxy.ps1
  serves from:  <CacheDir>\<host>\<sanitized-path>.  Run this ONCE per game version (on a PC
  that resolves the CDN normally, i.e. WITHOUT the hosts redirect active).

  Get the URL from cdn-host-sniff.ps1 output (Host + Path). Example:
    .\prime-blob.ps1 -Url "http://assets1.xboxlive.com/9/d7850504-.../...msixvc"

  -LocalFile : instead of downloading from the origin, seed the cache from a local file
  (e.g. a reconstructed .msixvc from xvdtool). The bytes you supply become what the proxy
  serves for that URL. Example:
    .\prime-blob.ps1 -Url "http://assets1.xboxlive.com/9/.../...msixvc" `
                     -LocalFile "C:\Users\SIGMA\source\repos\xvdtool\XVDTool\Sample\final.msixvc"

  Uses BITS (resumable) with an HttpClient fallback. ASCII only. PS 5.1 compatible.
#>
param(
  [Parameter(Mandatory=$true)][string]$Url,
  [string]$LocalFile,
  [string]$CacheDir = "F:\xbox-cache",
  [string]$DnsServer = '1.1.1.1',
  [switch]$Force
)
$ErrorActionPreference = 'Stop'

$u = [System.Uri]$Url
$hostName = $u.Host
$rawPath = $u.PathAndQuery
$rel = ($rawPath -replace '\?.*$','').TrimStart('/')
$rel = $rel -replace '[\\/]+','\' -replace '[:*?"<>|]','_'
$dest = Join-Path (Join-Path $CacheDir $hostName) $rel
$destDir = Split-Path $dest -Parent
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

# Resolve the REAL IP via public DNS so this works even if a hosts redirect is present.
$ip = $null
try {
  $ans = Resolve-DnsName -Name $hostName -Server $DnsServer -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1
  if ($ans) { $ip = $ans.IPAddress }
} catch {}
Write-Host ("Priming cache:" ) -ForegroundColor Cyan
Write-Host ("  URL    : {0}" -f $Url)
Write-Host ("  Host   : {0}  (real IP {1})" -f $hostName, $(if($ip){$ip}else{'<system DNS>'}))
Write-Host ("  Dest   : {0}" -f $dest)

if ((Test-Path -LiteralPath $dest) -and -not $Force) {
  $sz = (Get-Item -LiteralPath $dest).Length
  Write-Host ("  Already cached ({0:N1} MB). Use -Force to overwrite, or delete it to re-prime." -f ($sz/1MB)) -ForegroundColor Yellow
  return
}

# ---- LocalFile mode: seed the cache from a file on disk (no origin download) ----
if ($LocalFile) {
  if (-not (Test-Path -LiteralPath $LocalFile)) { throw ("LocalFile not found: {0}" -f $LocalFile) }
  $src = Get-Item -LiteralPath $LocalFile
  Write-Host ("  Source : {0}  ({1:N1} MB)" -f $src.FullName, ($src.Length/1MB)) -ForegroundColor Green
  Write-Host "  Seeding cache from local file (copy)..." -ForegroundColor Green
  Copy-Item -LiteralPath $src.FullName -Destination $dest -Force
  $cs = (Get-Item -LiteralPath $dest).Length
  Write-Host ("DONE. Cached {0:N1} MB at {1}" -f ($cs/1MB), $dest) -ForegroundColor Green
  return
}

$tmp = "$dest.part"
$ok = $false

# Preferred: BITS against the real IP with Host header (resumable, fast, no hosts dependency).
try {
  $srcByIp = if ($ip) { ($Url -replace [regex]::Escape($hostName), $ip) } else { $Url }
  $headers = if ($ip) { "Host: $hostName" } else { $null }
  Write-Host "  Downloading via BITS..." -ForegroundColor Green
  if ($headers) { Start-BitsTransfer -Source $srcByIp -Destination $tmp -CustomHeaders $headers -ErrorAction Stop }
  else          { Start-BitsTransfer -Source $Url    -Destination $tmp -ErrorAction Stop }
  $ok = $true
} catch {
  Write-Host ("  BITS failed ({0}); falling back to HttpClient stream..." -f $_.Exception.Message) -ForegroundColor Yellow
}

if (-not $ok) {
  Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
  $h = New-Object System.Net.Http.HttpClient; $h.Timeout = [TimeSpan]::FromHours(6)
  $target = if ($ip) { ($Url -replace [regex]::Escape($hostName), $ip) } else { $Url }
  $msg = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $target)
  $msg.Headers.Host = $hostName
  $resp = $h.SendAsync($msg, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
  if (-not $resp.IsSuccessStatusCode) { throw ("origin returned {0}" -f [int]$resp.StatusCode) }
  $in = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
  $out = [System.IO.File]::Create($tmp)
  try {
    $buf = New-Object byte[] 1048576; $total = [long]0; $lastP = Get-Date
    while (($n = $in.Read($buf,0,$buf.Length)) -gt 0) {
      $out.Write($buf,0,$n); $total += $n
      if (((Get-Date) - $lastP).TotalSeconds -ge 2) { Write-Host ("    {0:N1} MB..." -f ($total/1MB)) -ForegroundColor DarkGray; $lastP = Get-Date }
    }
  } finally { $out.Dispose(); $in.Dispose(); $resp.Dispose() }
  $ok = $true
}

if ($ok -and (Test-Path -LiteralPath $tmp)) {
  Move-Item -LiteralPath $tmp -Destination $dest -Force
  $sz = (Get-Item -LiteralPath $dest).Length
  Write-Host ("DONE. Cached {0:N1} MB at {1}" -f ($sz/1MB), $dest) -ForegroundColor Green
} else {
  Write-Host "FAILED to prime." -ForegroundColor Red
}
