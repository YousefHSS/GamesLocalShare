<#
  fetch-cdn-package.ps1 — pull ANY version of a package straight from the Xbox CDN.

  Removes the "wait for a real update" step from the cross-version experiment. The CDN serves
  these packages to a plain unauthenticated ranged GET — the app's own gap-fill does exactly
  that (XboxCacheProxyService.FetchRange sends nothing but Host: and Range:, no token, no
  cookie) — so given a URL you can fetch an old version at any time.

  WHERE THE OLD VERSION'S URL COMES FROM

  The version is baked into the package path, and the Store only ever requests the current one,
  so there is no index of old versions to browse. But you have probably already recorded some:
  every capture writes <name>.skl.json next to the skeleton with CachePath + CacheRoot, and the
  relative part IS the CDN URL path (see the RelativeCachePath comment in SkeletonWatcherService).
  So any skeleton captured BEFORE that game updated hands you a working old-version URL.

  Run with -List to see every URL you can derive right now.

  USAGE
    fetch-cdn-package.ps1 -List [-SkeletonStore <dir>] [-CacheRoot <dir>]
    fetch-cdn-package.ps1 -Manifest <name.skl.json> [-Out <file>] [-DiffAgainst <new.msixvc>]
    fetch-cdn-package.ps1 -UrlPath /path/to/Foo_1.2.3.0_x64__abc.msixvc -Out <file>

  Defaults: -SkeletonStore %LOCALAPPDATA%\GamesLocalShare\xbox-skeleton\skeletons
            -CacheRoot     %LOCALAPPDATA%\GamesLocalShare\xbox-cache
            -CdnHost       assets1.xboxlive.com
            -Out           .\<packagename>.msixvc

  NOTE ON DNS: while this app's proxy is running, assets1.xboxlive.com is redirected to
  127.0.0.1 by the hosts file. This script therefore resolves the REAL CDN IP by querying a
  public resolver directly (same trick as XboxCacheProxyService.ResolveARecord) and connects by
  IP with a Host: header. It refuses to proceed if the address it gets back is loopback or
  private — pass -Ip <addr> to override.

  Downloads resume: an interrupted run leaves <Out>.part and picks up where it stopped.

  Exit codes: 0 ok | 1 error | 2 bad args
#>
param(
  [switch]$List,
  [string]$Manifest,
  [string]$UrlPath,
  [string]$Out,
  [string]$DiffAgainst,
  [string]$SkeletonStore,
  [string]$CacheRoot,
  [string]$CdnHost = 'assets1.xboxlive.com',
  [string]$DnsServer = '1.1.1.1',
  [string]$Ip,
  [int]$ChunkMB = 8
)

$ErrorActionPreference = 'Stop'

# NB: $ErrorActionPreference is Stop, which makes Write-Error terminate on the spot — the exit
# code after it would never run. Report failures through this instead.
function Fail([string]$message, [int]$code) {
  Write-Host ''
  Write-Host ("ERROR: " + $message) -ForegroundColor Red
  exit $code
}

if (-not $SkeletonStore) { $SkeletonStore = Join-Path $env:LOCALAPPDATA 'GamesLocalShare\xbox-skeleton\skeletons' }
if (-not $CacheRoot)     { $CacheRoot     = Join-Path $env:LOCALAPPDATA 'GamesLocalShare\xbox-cache' }
if ($ChunkMB -le 0) { $ChunkMB = 8 }

# ---- manifest -> CDN url path ----------------------------------------------

# CachePath mirrors the CDN layout: <CacheRoot>\<host>\<...>\<PackageFullName>.msixvc
# so stripping the root and the leading host segment yields the URL path.
function Get-UrlPathFromManifest($man) {
  $cachePath = [string]$man.CachePath
  $cacheRoot = [string]$man.CacheRoot
  if (-not $cachePath) { return $null }
  if (-not $cacheRoot -or -not $cachePath.StartsWith($cacheRoot, [StringComparison]::OrdinalIgnoreCase)) { return $null }
  $rel = $cachePath.Substring($cacheRoot.Length).TrimStart('\', '/')
  $parts = @($rel -split '[\\/]' | Where-Object { $_ })
  if ($parts.Count -lt 2) { return $null }   # need at least <host>\<file>
  return '/' + (($parts[1..($parts.Count - 1)]) -join '/')
}

function Read-Manifests([string]$dir) {
  if (-not (Test-Path -LiteralPath $dir)) { return @() }
  Get-ChildItem -LiteralPath $dir -Filter '*.skl.json' -File -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $m = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
      [pscustomobject]@{
        File     = $_.FullName
        Name     = $m.Name
        Version  = $m.CapturedVersion
        Captured = $m.CapturedAt
        Bytes    = $m.PackageBytes
        Package  = [string]$m.CachePath
        UrlPath  = Get-UrlPathFromManifest $m
      }
    } catch { }
  }
}

if ($List) {
  $mans = Read-Manifests $SkeletonStore
  if (-not $mans -or $mans.Count -eq 0) {
    Write-Host "No .skl.json manifests under: $SkeletonStore"
    Write-Host "(Nothing captured yet, or the skeleton store is elsewhere — pass -SkeletonStore.)"
    exit 0
  }
  Write-Host ''
  Write-Host "Packages you can fetch from the CDN right now (from captured skeletons):"
  Write-Host ''
  foreach ($m in $mans) {
    $have = if ($m.Package -and (Test-Path -LiteralPath $m.Package)) { 'cached locally' } else { 'not on disk' }
    Write-Host ("  {0}" -f $m.Name)
    Write-Host ("      captured version : {0}   ({1})" -f $(if ($m.Version) { $m.Version } else { 'unknown' }), $m.Captured)
    Write-Host ("      package          : {0}  [{1}]" -f $(if ($m.Bytes) { "{0:N0} bytes" -f $m.Bytes } else { '?' }), $have)
    if ($m.UrlPath) {
      Write-Host ("      url              : http://{0}{1}" -f $CdnHost, $m.UrlPath)
      Write-Host ("      fetch            : .\fetch-cdn-package.ps1 -Manifest `"{0}`"" -f $m.File)
    } else {
      Write-Host  "      url              : (cannot derive — manifest has no CacheRoot-relative path)"
    }
    Write-Host ''
  }
  Write-Host "A version here that is OLDER than what is installed now is exactly the 'old package'"
  Write-Host "the cross-version diff needs — no update to wait for."
  exit 0
}

# ---- resolve what to fetch --------------------------------------------------

if (-not $UrlPath -and -not $Manifest) {
  Fail "Nothing to fetch. Pass -Manifest <name.skl.json> or -UrlPath </...msixvc>, or -List to see what is available." 2
}

if ($Manifest) {
  if (-not (Test-Path -LiteralPath $Manifest)) { Fail "Manifest not found: $Manifest" 2 }
  $man = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
  $UrlPath = Get-UrlPathFromManifest $man
  if (-not $UrlPath) {
    Fail "Could not derive a CDN path from $Manifest (no CacheRoot-relative CachePath). Pass -UrlPath explicitly." 2
  }
  Write-Host ("manifest : {0}" -f $Manifest)
  Write-Host ("title    : {0}  (captured version {1})" -f $man.Name, $(if ($man.CapturedVersion) { $man.CapturedVersion } else { 'unknown' }))
}

if (-not $UrlPath.StartsWith('/')) { $UrlPath = '/' + $UrlPath }
if (-not $Out) { $Out = Join-Path (Get-Location) ([IO.Path]::GetFileName($UrlPath.Split('?')[0])) }

# ---- resolve the REAL cdn ip (bypassing our own hosts redirect) -------------

if (-not $Ip) {
  try {
    $rec = Resolve-DnsName -Name $CdnHost -Server $DnsServer -Type A -ErrorAction Stop |
             Where-Object { $_.IPAddress } | Select-Object -First 1
    if ($rec) { $Ip = $rec.IPAddress }
  } catch {
    Write-Host "Resolve-DnsName failed ($_); falling back to the system resolver (may hit the hosts redirect)."
    try { $Ip = ([System.Net.Dns]::GetHostAddresses($CdnHost) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString } catch { }
  }
}
if (-not $Ip) { Fail "Could not resolve $CdnHost. Pass -Ip <addr>." 1 }

$addr = [System.Net.IPAddress]::Parse($Ip)
$b = $addr.GetAddressBytes()
if ($addr.ToString().StartsWith('127.') -or $b[0] -eq 10 -or ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31)) {
  Fail "$CdnHost resolved to $Ip — that is this machine's proxy redirect, not the CDN. Stop the proxy, or pass -Ip <real addr>." 1
}

Write-Host ''
Write-Host ("url      : http://{0}{1}" -f $CdnHost, $UrlPath)
Write-Host ("via ip   : {0}" -f $Ip)
Write-Host ("out      : {0}" -f $Out)

# ---- download ---------------------------------------------------------------

try { Add-Type -AssemblyName System.Net.Http -ErrorAction Stop | Out-Null } catch { }  # already loaded on PS7
$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(10)

function Send-Ranged([long]$from, [long]$toInclusive) {
  $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, ("http://" + $Ip + $UrlPath))
  $req.Headers.Host = $CdnHost
  $req.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($from, $toInclusive)
  return $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
}

# Probe one byte to learn the full size (and prove the object exists).
$probe = Send-Ranged 0 0
try {
  $code = [int]$probe.StatusCode
  if ($code -ne 206 -and $code -ne 200) {
    Fail "CDN returned HTTP $code for $UrlPath — wrong path, or that version is no longer served." 1
  }
  $total = $null
  if ($probe.Content.Headers.ContentRange -and $probe.Content.Headers.ContentRange.Length) {
    $total = [long]$probe.Content.Headers.ContentRange.Length
  } elseif ($probe.Content.Headers.ContentLength) {
    $total = [long]$probe.Content.Headers.ContentLength
  }
} finally { $probe.Dispose() }

if (-not $total -or $total -le 0) { Fail "CDN did not report a size for $UrlPath." 1 }
Write-Host ("size     : {0:N0} bytes ({1:F2} GB)" -f $total, ($total / 1GB))
Write-Host ''

$part    = $Out + '.part'
$partUrl = $part + '.url'
$url     = "http://$CdnHost$UrlPath"
$have = 0L
if (Test-Path -LiteralPath $part) {
  $prev = ''
  if (Test-Path -LiteralPath $partUrl) { $prev = (Get-Content -LiteralPath $partUrl -Raw).Trim() }
  if ($prev -ne $url) {
    Write-Host "existing .part belongs to a different url — starting over"
    Remove-Item -LiteralPath $part -Force
  } else {
    $have = (Get-Item -LiteralPath $part).Length
    if ($have -gt $total) { $have = 0; Remove-Item -LiteralPath $part -Force }
    elseif ($have -gt 0) { Write-Host ("resuming at {0:N0} bytes" -f $have) }
  }
}
Set-Content -LiteralPath $partUrl -Value $url

$chunk = [long]$ChunkMB * 1MB
$startHave = $have
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$fs = [System.IO.File]::Open($part, 'OpenOrCreate', 'Write', 'None')
try {
  $fs.Position = $have
  $buf = New-Object -TypeName byte[] -ArgumentList 1MB
  while ($have -lt $total) {
    $to = [Math]::Min($have + $chunk, $total) - 1
    $resp = Send-Ranged $have $to
    try {
      $sc = [int]$resp.StatusCode
      if ($sc -ne 206 -and $sc -ne 200) { throw "HTTP $sc at offset $have" }
      $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
      while ($true) {
        $n = $stream.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        $fs.Write($buf, 0, $n)
        $have += $n
      }
    } finally { $resp.Dispose() }

    $mb = $have / 1MB
    $thisRun = ($have - $startHave) / 1MB
    $rate = if ($sw.Elapsed.TotalSeconds -gt 0) { $thisRun / $sw.Elapsed.TotalSeconds } else { 0 }
    Write-Progress -Activity "Fetching $([IO.Path]::GetFileName($Out))" `
                   -Status ("{0:N0} / {1:N0} MB   {2:F1} MB/s" -f $mb, ($total / 1MB), $rate) `
                   -PercentComplete ([int](100.0 * $have / $total))
  }
  $fs.Flush($true)
}
catch {
  Write-Progress -Activity 'Fetching' -Completed
  Write-Host ''
  Write-Host ("download interrupted at {0:N0} of {1:N0} bytes: {2}" -f $have, $total, $_) -ForegroundColor Red
  Write-Host  "Re-run the same command to resume from where it stopped."
  exit 1
}
finally { $fs.Dispose() }
Write-Progress -Activity 'Fetching' -Completed

$got = (Get-Item -LiteralPath $part).Length
if ($got -ne $total) { Fail "Short download: got $got of $total bytes. Re-run to resume." 1 }
Move-Item -LiteralPath $part -Destination $Out -Force
Remove-Item -LiteralPath $partUrl -Force -ErrorAction SilentlyContinue

Write-Host ("done     : {0:N0} bytes in {1:N1}s ({2:F1} MB/s this run)" -f $got, $sw.Elapsed.TotalSeconds, ((($got - $startHave) / 1MB) / [Math]::Max(0.001, $sw.Elapsed.TotalSeconds)))
Write-Host ("saved    : {0}" -f $Out)

# ---- hand off to the diff ---------------------------------------------------

$diffScript = Join-Path $PSScriptRoot 'diff-packages.ps1'
Write-Host ''
if ($DiffAgainst) {
  if (-not (Test-Path -LiteralPath $DiffAgainst)) { Fail "-DiffAgainst not found: $DiffAgainst" 2 }
  Write-Host "running the cross-version diff..."
  & $diffScript -Old $Out -New $DiffAgainst
} else {
  Write-Host "Next, diff it against the CURRENT version's package:"
  Write-Host ("  .\diff-packages.ps1 -Old `"{0}`" -New `"<current>.msixvc`"" -f $Out)
  Write-Host ''
  Write-Host "The current one is under your cache root:"
  Write-Host ("  {0}\{1}\..." -f $CacheRoot, $CdnHost)
}
exit 0
