<#
  pathb-zero-content.ps1 - Path B experiment setup.

  PATH B HYPOTHESIS: the receiver should only need the small GENUINE header+hash-tree
  (first 14,303,232 bytes of the .msixvc). Serve a package whose header+tree is genuine
  but whose CONTENT is zeroed; if Gaming Services finalizes the install anyway, overlay
  the real Content\ afterward -> a native (trusted, delta-updatable) install with NO
  multi-GB content served over the wire.

  THE QUESTION THIS SETTLES: does GS hash-verify content blocks AS IT DOWNLOADS, or does
  it trust the stream and only check on explicit Verify?
    - GS re-requests the zeroed content ranges / errors / falls back to real CDN
        => content IS verified on download => Path B DEAD (serve real content = Path A).
    - GS sails through one pass and FINALIZES
        => content NOT verified on download => Path B VIABLE: finalize on zeros, then
           overlay the real Content\, then confirm launch + Verify + a delta update.

  HOW IT WORKS: starts from the sibling .genuine backup (guarantees a clean header+tree),
  copies it over the live cached .msixvc, then zeros bytes [HeaderBytes .. EOF). The LAN
  proxy serves this file as a cache HIT during a fresh Install.

  -Restore copies .genuine back over the .msixvc (undo).

  Run ELEVATED. ASCII only. Windows PowerShell 5.1.
    .\pathb-zero-content.ps1
    .\pathb-zero-content.ps1 -Restore
#>
param(
  [string]$Msixvc = "F:\xbox-cache\assets1.xboxlive.com\9\d7850504-ff3f-4e5b-bc23-734e77570b4d\368b2c2c-c6e2-4472-acda-52a5f18a1d51\1.9.2.0.3d4d9c86-1327-498a-879e-735504c0493f\DoborogGames.CloneDroneintheDangerZone_1.9.2.0_x64__w6hf08ggk4es4.msixvc",
  [int64]$HeaderBytes = 14303232,
  [switch]$Restore
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$genuine = "$Msixvc.genuine"
if (-not (Test-Path -LiteralPath $genuine)) { Write-Host ("ERROR: genuine backup missing: {0}" -f $genuine) -ForegroundColor Red; return }

if ($Restore) {
  Write-Host "Restoring genuine .msixvc from .genuine backup..." -ForegroundColor Cyan
  Copy-Item -LiteralPath $genuine -Destination $Msixvc -Force
  Write-Host ("RESTORED: {0}" -f $Msixvc) -ForegroundColor Green
  return
}

# --- start from a clean genuine copy so header+tree are guaranteed genuine ---
Write-Host "1) Seeding live .msixvc from .genuine (clean header+tree)..." -ForegroundColor Cyan
Copy-Item -LiteralPath $genuine -Destination $Msixvc -Force

$total = (Get-Item -LiteralPath $Msixvc).Length
if ($HeaderBytes -ge $total) { Write-Host ("ERROR: HeaderBytes {0} >= file size {1}." -f $HeaderBytes, $total) -ForegroundColor Red; return }
$contentLen = $total - $HeaderBytes

Write-Host ("2) Zeroing content region [{0:N0} .. {1:N0}) = {2:N0} bytes ({3:N2} GB), keeping header+tree genuine..." -f $HeaderBytes, $total, $contentLen, ($contentLen/1GB)) -ForegroundColor Cyan
$fs = [System.IO.File]::Open($Msixvc, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
  $fs.Position = $HeaderBytes
  $bufSize = 8MB
  $zeros = New-Object byte[] $bufSize
  $rem = $contentLen
  while ($rem -gt 0) {
    $chunk = [int][Math]::Min([int64]$bufSize, $rem)
    $fs.Write($zeros, 0, $chunk)
    $rem -= $chunk
  }
  $fs.Flush()
} finally { $fs.Close() }

# sanity: confirm the header region still matches genuine
$ok = $true
$a = [System.IO.File]::OpenRead($Msixvc); $b = [System.IO.File]::OpenRead($genuine)
try {
  $ba = New-Object byte[] 65536; $bb = New-Object byte[] 65536; $left = $HeaderBytes
  while ($left -gt 0 -and $ok) {
    $c = [int][Math]::Min(65536, $left)
    $na = $a.Read($ba,0,$c); $nb = $b.Read($bb,0,$c)
    if ($na -ne $nb) { $ok = $false; break }
    for ($i=0; $i -lt $na; $i++) { if ($ba[$i] -ne $bb[$i]) { $ok = $false; break } }
    $left -= $na
  }
} finally { $a.Close(); $b.Close() }
$okColor = 'Red'; if ($ok) { $okColor = 'Green' }
Write-Host ("   header+tree genuine match: {0}" -f $ok) -ForegroundColor $okColor

Write-Host ""
Write-Host "DONE. The cached .msixvc now has a genuine header+tree + zeroed content." -ForegroundColor Yellow
Write-Host ""
Write-Host "NEXT (Path B run):" -ForegroundColor Cyan
Write-Host "  1) Uninstall Clone Drone in the Xbox app." -ForegroundColor Gray
Write-Host "  2) Start the proxy + hosts redirect:  lan-cache\xbox-cache-proxy.ps1" -ForegroundColor Gray
Write-Host "  3) Xbox app -> Install Clone Drone. Watch the proxy log:" -ForegroundColor Gray
Write-Host "       - repeated re-requests of ranges past offset 14,303,232, or a jump to" -ForegroundColor Gray
Write-Host "         real CDN  => content verified on download => Path B DEAD." -ForegroundColor Gray
Write-Host "       - one clean pass and the install FINALIZES  => Path B VIABLE." -ForegroundColor Gray
Write-Host "  4) If it finalizes: overlay the real Content\ (apply-overlay.ps1 / robocopy" -ForegroundColor Gray
Write-Host "     from the sender stage), then launch + Verify + force an update to confirm" -ForegroundColor Gray
Write-Host "     trust survives as a delta." -ForegroundColor Gray
Write-Host "  5) Undo the cache change:  .\pathb-zero-content.ps1 -Restore" -ForegroundColor Gray
