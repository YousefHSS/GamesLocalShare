<#
  apply-overlay.ps1 - single-copy overlay test.
  Overlays the backed-up PLAINTEXT content + the RAW (true on-disk) metadata onto the currently
  (re)installing Clone Drone, to test whether a copied install + correct metadata yields a
  TRUSTED base (so Verify repairs only the ~1.8 MB of EXEs, instead of a full re-download).

  Run ELEVATED while the Xbox app install is PAUSED. ASCII only.

  Sources:
    content + (view) metadata : F:\xbox-singlecopy-src\Clone Drone in the Danger Zone   (robocopy backup, no EXEs)
    RAW metadata (overrides)  : F:\xbox-singlecopy-src\meta-raw\*                        (true on-disk bytes)
#>
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$backup  = "F:\xbox-singlecopy-src\Clone Drone in the Danger Zone"
$metaRaw = "F:\xbox-singlecopy-src\meta-raw"

# The Xbox app stages an in-progress install under <drive>\Games\<contentGUID>\ and renames it
# to the friendly name on completion. Auto-detect whichever exists.
$guid = "368B2C2C-C6E2-4472-ACDA-52A5F18A1D51"
$candidates = @()
foreach ($d in 'F','C','D','E','G') {
  $candidates += "$($d):\Games\$guid"
  $candidates += "$($d):\Games\Clone Drone in the Danger Zone"
  $candidates += "$($d):\XboxGames\$guid"
  $candidates += "$($d):\XboxGames\Clone Drone in the Danger Zone"
}
$install = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $install) { Write-Host "ERROR: no install/staging folder found. Is it (re)installing + paused?" -ForegroundColor Red; return }
Write-Host ("Target install folder: {0}" -f $install) -ForegroundColor Cyan
if (-not (Test-Path $backup))  { Write-Host "ERROR: backup not found: $backup" -ForegroundColor Red; return }

Write-Host "1) Overlaying content + metadata from backup (excludes the 2 locked EXEs)..." -ForegroundColor Cyan
# /XO off: always overwrite. Skip the 2 EXEs (not in backup anyway). Don't delete extras.
robocopy $backup $install /E /R:0 /W:0 /NFL /NDL /NP | Select-Object -Last 8

Write-Host "`n2) Overwriting metadata with RAW on-disk bytes (skipping any GS-locked ones)..." -ForegroundColor Cyan
Get-ChildItem -LiteralPath $metaRaw -File | Where-Object { $_.Name -notlike '_*' } | ForEach-Object {
  $dst = Join-Path $install $_.Name
  $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
  try {
    $w = [System.IO.File]::Open($dst, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
    $w.Write($bytes,0,$bytes.Length); $w.SetLength($bytes.Length); $w.Flush(); $w.Close()
    Write-Host ("   wrote raw {0} ({1} bytes)" -f $_.Name, $bytes.Length) -ForegroundColor Green
  } catch {
    Write-Host ("   SKIP (locked/denied) {0}" -f $_.Name) -ForegroundColor DarkYellow
  }
}

Write-Host "`nOverlay applied. Now RESUME the install in the Xbox app, then click Verify & Repair." -ForegroundColor Yellow
Write-Host "Watch the proxy: ~1.8 MB pulled = trusted base (WIN); full = base still untrusted." -ForegroundColor Yellow