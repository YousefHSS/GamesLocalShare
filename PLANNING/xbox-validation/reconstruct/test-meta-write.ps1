<#
  test-meta-write.ps1 - decisive gate test for the single-copy approach.

  Question: when we write a metadata file (.xvi) back through the NORMAL Windows API, does the
  gaming filter store our bytes VERBATIM on disk (pass-through -> we can place metadata on a
  destination), or does it TRANSFORM them on write (-> placing metadata is hard / approach dead)?

  Method: write the file's own raw-on-disk bytes back to it via the normal API, then raw-read
  the disk again and compare. SAME => pass-through. DIFFERENT => transform-on-write.

  SAFE-ish: it writes the .xvi's OWN bytes back (no foreign data). If it disturbs the install,
  reinstall Clone Drone from the Xbox app -- it will come from your LAN cache (0 internet).
  Run ELEVATED. ASCII only.
#>
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$rx   = "F:\Documents\GamesLocalShare\PLANNING\xbox-validation\reconstruct\raw-extract.ps1"
$inst = "F:\Games\Clone Drone in the Danger Zone\368B2C2C-C6E2-4472-ACDA-52A5F18A1D51.xvi"
$rawA = "F:\xbox-singlecopy-src\meta-raw\368B2C2C-C6E2-4472-ACDA-52A5F18A1D51.xvi"

$sha = New-Object System.Security.Cryptography.SHA256Managed
function HX($p){ $b=[System.IO.File]::ReadAllBytes($p); ([BitConverter]::ToString($sha.ComputeHash($b)) -replace '-','').Substring(0,16) }

$A = HX $rawA
Write-Host ("A = on-disk(raw) .xvi BEFORE          = {0}" -f $A) -ForegroundColor Cyan

$bytes = [System.IO.File]::ReadAllBytes($rawA)
$w = [System.IO.File]::Open($inst,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Write,[System.IO.FileShare]::ReadWrite)
$w.Write($bytes,0,$bytes.Length); $w.Flush(); $w.Close()
Start-Sleep -Milliseconds 300

& $rx -File $inst -Out "F:\xbox-singlecopy-src\meta-raw\_roundtrip.xvi" | Out-Null
$B = HX "F:\xbox-singlecopy-src\meta-raw\_roundtrip.xvi"
Write-Host ("B = on-disk(raw) AFTER writing A back = {0}" -f $B) -ForegroundColor Cyan

Write-Host ""
if ($A -eq $B) {
  Write-Host "RESULT: PASS-THROUGH -> writes land verbatim. Metadata IS placeable -> single-copy viable." -ForegroundColor Green
} else {
  Write-Host "RESULT: TRANSFORMED on write -> the filter mangles metadata writes. Placing metadata is hard." -ForegroundColor Yellow
}