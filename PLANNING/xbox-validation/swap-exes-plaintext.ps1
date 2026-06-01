# TEST B (causation isolation):
# Replace ONLY the content-protected executables of an existing CLEAN MSIXVC
# install with the plaintext versions from our stage, leaving every other
# byte untouched. Then a Verify & Repair tells us whether those 2 files alone
# are the trigger for the full re-download.
#
# Self-elevates. Takes ownership of each target EXE, overwrites it with the
# stage's plaintext copy, then runs `icacls /reset` so it re-inherits the
# install folder's ACLs (same end state the overlay produces).
#
# Usage (from a normal prompt; it will elevate):
#   .\swap-exes-plaintext.ps1 `
#       -GameDir   "F:\Games\Clone Drone in the Danger Zone" `
#       -StageDir  "G:\stage\Clone Drone in the Danger Zone"
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$StageDir,
    [string[]]$Relative = @(
        'Content\Clone Drone in the Danger Zone.exe',
        'Content\UnityCrashHandler64.exe'
    )
)

$ErrorActionPreference = 'Stop'

# --- self-elevate ---
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevating..." -ForegroundColor Yellow
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",
           '-GameDir',"`"$GameDir`"",'-StageDir',"`"$StageDir`"")
    Start-Process powershell -Verb RunAs -ArgumentList $a
    exit 0
}

$GameDir  = (Resolve-Path -LiteralPath $GameDir).Path
$StageDir = (Resolve-Path -LiteralPath $StageDir).Path
Write-Host ""
Write-Host "=== swap-exes-plaintext (TEST B) ===" -ForegroundColor Green
Write-Host "  GameDir:  $GameDir"
Write-Host "  StageDir: $StageDir"
Write-Host ""

$admins = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    ).Translate([System.Security.Principal.NTAccount]).Value

foreach ($rel in $Relative) {
    $dst = Join-Path $GameDir  $rel
    $src = Join-Path $StageDir $rel
    Write-Host ("--- {0}" -f $rel) -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $src)) { Write-Host "  STAGE MISSING: $src" -ForegroundColor Red; continue }
    if (-not (Test-Path -LiteralPath $dst)) { Write-Host "  DEST MISSING:  $dst" -ForegroundColor Red; continue }

    # 1. take ownership + grant admins full control so we can overwrite
    & takeown.exe /F "$dst" | Out-Null
    & icacls.exe "$dst" /grant "${admins}:F" /Q /C | Out-Null

    # 2. overwrite with the plaintext stage copy (data+attrs only)
    Copy-Item -LiteralPath $src -Destination $dst -Force
    Write-Host ("  copied plaintext ({0:N0} bytes)" -f (Get-Item -LiteralPath $dst).Length) -ForegroundColor Green

    # 3. reset ACLs so the file re-inherits from the install folder
    & icacls.exe "$dst" /reset /Q /C | Out-Null

    # 4. confirm it is now a readable MZ
    try {
        $fs = [System.IO.File]::Open($dst,'Open','Read','ReadWrite')
        $b = New-Object byte[] 2; [void]$fs.Read($b,0,2); $fs.Close()
        $mz = ($b[0] -eq 0x4D -and $b[1] -eq 0x5A)
        Write-Host ("  now readable, MZ={0}" -f $mz) -ForegroundColor Green
    } catch {
        Write-Host ("  WARN: still unreadable: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done. The clean install now has plaintext executables." -ForegroundColor Green
Write-Host "Next: run measure-net.ps1, then click Verify & Repair in the Xbox app." -ForegroundColor Yellow
Write-Host "Press Enter to close..." -ForegroundColor DarkGray
[void](Read-Host)
