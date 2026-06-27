<#
  probe-tamper.ps1 - reversible single-byte tamper probe for the MSIXVC trust gates.

  GOAL: find out WHICH integrity layer Gaming Services actually enforces, by flipping
  exactly ONE byte in a chosen layer of a CLEAN install, then having you run
  Xbox app -> Manage -> Files -> Verify/Repair (or just Resume an install) and watch
  what GS does (proxy log / NIC bytes / Xbox UI).

    -Target signature  flips 1 byte INSIDE the 512-byte RSA-4096 header signature
                       (offset 0x10 of the GUID blob).
                       GS reacts  => signature IS verified => a forged/edited hash tree
                                     can NEVER pass; the header MUST stay genuine
                                     (your transplant / header-only-download plan).
                       No reaction => signature NOT verified => the door is much wider
                                     (you could forge the tree itself).

    -Target hashtree   flips 1 byte deep in the integrity hash-tree region
                       (offset 0x100000 of the GUID blob).
                       GS reacts  => the tree is validated against the header root hash.
                       No reaction => tree contents not independently checked.

    -Target content    flips 1 byte in a real Content\ file.
                       GS reacts  => content blocks ARE hash-checked against the tree.
                                     (Expectation from prior work: block-granular repair,
                                      i.e. it fixes ~that block, not the whole game.)
                       No reaction => content NOT re-hashed at the gate you triggered
                                     => a placeholder-then-overlay flow is viable.

  SAFETY: backs up the original byte to probe-tamper-backup.json. Run with -Restore to
  put the byte back WITHOUT relying on Verify, so the clean control install is preserved.

  Run ELEVATED, with the game NOT running. ASCII only. Windows PowerShell 5.1.

    .\probe-tamper.ps1 -Target signature
    # ... run Verify in the Xbox app, observe ...
    .\probe-tamper.ps1 -Target signature -Restore
#>
param(
  [Parameter(Mandatory=$true)][ValidateSet('signature','hashtree','content')][string]$Target,
  [switch]$Restore,
  [string]$GameDir = "F:\Games\Clone Drone in the Danger Zone",
  [string]$File,          # explicit signed file to patch (e.g. the cached .msixvc); overrides auto-locate
  [int64]$Offset = -1
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }
if (-not (Test-Path -LiteralPath $GameDir)) { Write-Host "ERROR: game dir not found: $GameDir" -ForegroundColor Red; return }

$backupFile = Join-Path $PSScriptRoot 'probe-tamper-backup.json'

# --- locate the GUID blob (file named exactly a GUID, no extension) ---
function Get-GuidBlob {
  Get-ChildItem -LiteralPath $GameDir -File |
    Where-Object { $_.Name -match '^[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}$' } |
    Sort-Object Length -Descending | Select-Object -First 1
}

# --- pick a real content file big enough to patch at the chosen offset ---
function Get-ContentFile([int64]$minLen) {
  $c = Join-Path $GameDir 'Content'
  if (-not (Test-Path -LiteralPath $c)) { return $null }
  Get-ChildItem -LiteralPath $c -Recurse -File |
    Where-Object { $_.Length -gt $minLen -and $_.Extension -ne '.exe' } |
    Sort-Object Length -Descending | Select-Object -First 1
}

# --- open a file RW; if GS holds a lock, stop the services once and retry ---
function Open-RW([string]$path) {
  try { return [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite) }
  catch {
    Write-Host "  file locked; stopping GamingServices* to release it..." -ForegroundColor DarkYellow
    foreach ($svc in 'GamingServicesNet','GamingServices') {
      try { Stop-Service -Name $svc -Force -ErrorAction Stop; Write-Host ("    stopped {0}" -f $svc) -ForegroundColor DarkYellow } catch {}
    }
    Start-Sleep -Milliseconds 800
    return [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
  }
}

# --- resolve target file + default offset ---
switch ($Target) {
  'signature' { $defOff = 0x10 }
  'hashtree'  { $defOff = 0x100000 }
  'content'   { $defOff = 0x1000 }
}
if ($File) {
  if (-not (Test-Path -LiteralPath $File)) { Write-Host ("ERROR: -File not found: {0}" -f $File) -ForegroundColor Red; return }
  $tf = Get-Item -LiteralPath $File
} else {
  switch ($Target) {
    'signature' { $tf = Get-GuidBlob }
    'hashtree'  { $tf = Get-GuidBlob }
    'content'   { $tf = Get-ContentFile 0x4000 }
  }
}
if (-not $tf) { Write-Host ("ERROR: could not locate target file for '{0}'." -f $Target) -ForegroundColor Red; return }
$off = if ($Offset -ge 0) { $Offset } else { $defOff }
$path = $tf.FullName
if ($off -ge $tf.Length) { Write-Host ("ERROR: offset 0x{0:X} beyond file size {1}." -f $off, $tf.Length) -ForegroundColor Red; return }

# --- load backup store ---
$store = @{}
if (Test-Path -LiteralPath $backupFile) {
  try { (Get-Content -LiteralPath $backupFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $store[$_.Name] = $_.Value } } catch {}
}

if ($Restore) {
  if (-not $store.ContainsKey($Target)) { Write-Host ("ERROR: no backup recorded for target '{0}'." -f $Target) -ForegroundColor Red; return }
  $b = $store[$Target]
  $fs = Open-RW $b.Path
  try { $fs.Position = [int64]$b.Offset; $fs.WriteByte([byte]$b.OrigByte); $fs.Flush() } finally { $fs.Close() }
  Write-Host ("RESTORED {0}: offset 0x{1:X} -> 0x{2:X2} ({3})" -f $Target, [int64]$b.Offset, [int]$b.OrigByte, $b.Path) -ForegroundColor Green
  $store.Remove($Target)
  ($store | ConvertTo-Json) | Set-Content -LiteralPath $backupFile -Encoding ASCII
  return
}

# --- TAMPER: read, back up, flip one byte ---
Write-Host ("=== tamper probe: {0} ===" -f $Target) -ForegroundColor Cyan
Write-Host ("File   : {0}" -f $path)
Write-Host ("Offset : 0x{0:X} ({1})" -f $off, $off)
$fs = Open-RW $path
try {
  $fs.Position = $off
  $orig = $fs.ReadByte()
  if ($orig -lt 0) { throw "read past EOF" }
  $new = $orig -bxor 0xFF
  $fs.Position = $off
  $fs.WriteByte([byte]$new)
  $fs.Flush()
} finally { $fs.Close() }

$store[$Target] = [pscustomobject]@{ Target=$Target; Path=$path; Offset=$off; OrigByte=$orig; NewByte=$new; When=(Get-Date).ToString('s') }
($store | ConvertTo-Json) | Set-Content -LiteralPath $backupFile -Encoding ASCII

Write-Host ("FLIPPED: 0x{0:X2} -> 0x{1:X2}  (backup in {2})" -f $orig, $new, $backupFile) -ForegroundColor Yellow
Write-Host ""
Write-Host "NEXT:" -ForegroundColor Cyan
Write-Host "  1) Xbox app -> Clone Drone -> Manage -> Files -> Verify and repair" -ForegroundColor Gray
Write-Host "     (watch the LAN proxy log + NIC bytes + the Xbox UI)." -ForegroundColor Gray
Write-Host "  2) Interpret:" -ForegroundColor Gray
switch ($Target) {
  'signature' {
    Write-Host "       reacts   => SIGNATURE enforced: header must stay genuine (transplant it)." -ForegroundColor Gray
    Write-Host "       no react => signature NOT checked: a forged hash tree could pass." -ForegroundColor Gray
  }
  'hashtree' {
    Write-Host "       reacts   => hash tree validated against the signed header root." -ForegroundColor Gray
    Write-Host "       no react => tree contents not independently verified." -ForegroundColor Gray
  }
  'content' {
    Write-Host "       small repair => content hash-checked, block-granular (fixes ~1 block)." -ForegroundColor Gray
    Write-Host "       full re-dl   => content checked, coarse re-acquire." -ForegroundColor Gray
    Write-Host "       no react     => content NOT re-hashed at this gate (placeholder viable)." -ForegroundColor Gray
  }
}
Write-Host ("  3) Put the byte back any time: .\probe-tamper.ps1 -Target {0} -Restore" -f $Target) -ForegroundColor Gray
