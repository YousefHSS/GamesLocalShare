<#
.SYNOPSIS
    Probe whether a title is MSIXVC (XboxGames\<Name>) or plain MSIX
    (Program Files\WindowsApps).

.DESCRIPTION
    Run this on PC A AFTER the title is installed. It looks for the game
    folder across all drives and reports the layout type so you know
    whether the pre-staged-transfer experiment is applicable.

    MSIXVC layout (transfer experiment applies):
        <Drive>:\XboxGames\<Name>\
            <GUID>          (encrypted package envelope)
            <GUID>.xvi
            <GUID>.xvs
            Content\
                AppxManifest.xml
                <game files>

    Plain MSIX layout (this transfer strategy does NOT apply):
        C:\Program Files\WindowsApps\<PackageFullName>\
            AppxManifest.xml
            <game files>

.PARAMETER GameName
    The folder name as it would appear under XboxGames, e.g. "Hi-Fi Rush",
    OR a substring of the publisher portion of the PFN, e.g. "Microsoft.624F8B84B80".

.PARAMETER Path
    Inspect a specific folder directly (skips the name-based search).
    Useful if the Xbox app installed to a non-standard location like
    F:\Games\<title>.

.PARAMETER ExtraSearchRoots
    Additional folders to search by name in addition to <Drive>:\XboxGames
    and C:\Program Files\WindowsApps. Pass paths like "F:\Games".

.EXAMPLE
    .\probe-package-layout.ps1 -GameName "Pentiment"

.EXAMPLE
    .\probe-package-layout.ps1 -Path "F:\Games\Hollow Knight Silksong"

.EXAMPLE
    .\probe-package-layout.ps1 -GameName "Silksong" -ExtraSearchRoots "F:\Games"
#>
[CmdletBinding(DefaultParameterSetName='ByName')]
param(
    [Parameter(Mandatory, ParameterSetName='ByName')]
    [string]   $GameName,
    [Parameter(Mandatory, ParameterSetName='ByPath')]
    [string]   $Path,
    [Parameter(ParameterSetName='ByName')]
    [string[]] $ExtraSearchRoots = @()
)

$ErrorActionPreference = 'Stop'

function Inspect-Folder {
    param([string]$FolderPath, [string]$LayoutHint)
    $files = Get-ChildItem -LiteralPath $FolderPath -File -Force -ErrorAction SilentlyContinue
    $hasEnvelope     = ($files | Where-Object { $_.Extension -in '.xvi','.xvs','.xct','.xsp' }).Count -gt 0
    $hasManifestRoot = Test-Path -LiteralPath (Join-Path $FolderPath 'AppxManifest.xml')
    $hasContentMan   = Test-Path -LiteralPath (Join-Path $FolderPath 'Content\AppxManifest.xml')
    $size = (Get-ChildItem -LiteralPath $FolderPath -Recurse -File -Force -ErrorAction SilentlyContinue |
             Measure-Object -Sum Length).Sum
    # Try to sniff PFN from ACL - root first, then Content\ fallback
    $pfn = $null
    foreach ($probe in @($FolderPath, (Join-Path $FolderPath 'Content'))) {
        if (-not (Test-Path -LiteralPath $probe)) { continue }
        try {
            $sddl = (Get-Acl -LiteralPath $probe -ErrorAction Stop).Sddl
            $m = [regex]::Matches($sddl, 'WIN://SYSAPPID\s+Contains\s+"([^"]+)"')
            if ($m.Count -gt 0) { $pfn = $m[0].Groups[1].Value; break }
        } catch { }
    }

    $layout = $LayoutHint
    if (-not $layout) {
        if     ($hasEnvelope -and $hasContentMan) { $layout = 'MSIXVC' }
        elseif ($hasManifestRoot)                  { $layout = 'plain-MSIX-like' }
        elseif ($hasEnvelope)                      { $layout = 'MSIXVC-no-content-manifest' }
        else                                       { $layout = 'unknown' }
    }
    return [pscustomobject]@{
        Layout            = $layout
        Path              = $FolderPath
        BytesGB           = [math]::Round($size/1GB, 2)
        HasEnvelope       = $hasEnvelope
        HasManifestAtRoot = $hasManifestRoot
        HasContentMan     = $hasContentMan
        SniffedPFN        = $pfn
    }
}

$found = @()

# --- ByPath mode: just inspect the given folder ----------------------------
if ($PSCmdlet.ParameterSetName -eq 'ByPath') {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "Path not found: $Path" -ForegroundColor Red
        exit 1
    }
    $Path = (Resolve-Path -LiteralPath $Path).Path
    Write-Host ""
    Write-Host "Inspecting: $Path" -ForegroundColor Cyan
    Write-Host ""
    $found += Inspect-Folder -FolderPath $Path
    $found | Format-List
    Write-Host ""
    $row = $found[0]
    if ($row.HasEnvelope -and $row.HasContentMan) {
        Write-Host "VERDICT: MSIXVC - transfer experiment APPLIES." -ForegroundColor Green
        Write-Host "  Use as -GameFolder:  `"$($row.Path)`"" -ForegroundColor Green
        if ($row.SniffedPFN) { Write-Host "  PackageFamilyName:    $($row.SniffedPFN)" -ForegroundColor Green }
    } elseif ($row.HasManifestAtRoot -and -not $row.HasEnvelope) {
        Write-Host "VERDICT: plain MSIX layout - transfer experiment DOES NOT APPLY." -ForegroundColor Red
    } elseif ($row.HasEnvelope) {
        Write-Host "VERDICT: MSIXVC envelope present but Content\AppxManifest.xml missing." -ForegroundColor Yellow
        Write-Host "  Possibly an in-progress download. Re-run after install completes." -ForegroundColor Yellow
    } else {
        Write-Host "VERDICT: unknown layout. Not an Xbox PC install in either format." -ForegroundColor Yellow
        Write-Host "  This is probably the wrong folder - perhaps a non-Xbox copy of the game?" -ForegroundColor Yellow
    }
    exit 0
}

# --- ByName mode -----------------------------------------------------------
Write-Host ""
Write-Host "Searching for '$GameName'..." -ForegroundColor Cyan
Write-Host ""

# Check XboxGames on every drive, plus any extra search roots
$searchRoots = @()
foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { Test-Path "$($_.Root)" })) {
    $xg = Join-Path $d.Root 'XboxGames'
    if (Test-Path -LiteralPath $xg) { $searchRoots += $xg }
}
foreach ($r in $ExtraSearchRoots) {
    if (Test-Path -LiteralPath $r) { $searchRoots += $r }
}
foreach ($root in $searchRoots) {
    $candidates = Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*$GameName*" }
    foreach ($c in $candidates) {
        $found += Inspect-Folder -FolderPath $c.FullName
    }
}

# Check WindowsApps
$wa = 'C:\Program Files\WindowsApps'
if (Test-Path -LiteralPath $wa) {
    $candidates = Get-ChildItem -LiteralPath $wa -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*$GameName*" }
    foreach ($c in $candidates) {
        $size = 0
        try {
            $size = (Get-ChildItem -LiteralPath $c.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
                     Measure-Object -Sum Length).Sum
        } catch { }
        $found += [pscustomobject]@{
            Layout      = 'plain-MSIX'
            Path        = $c.FullName
            BytesGB     = [math]::Round($size/1GB, 2)
            HasEnvelope = $false
        }
    }
}

# Get-AppxPackage cross-check
try {
    $pkgs = Get-AppxPackage -PackageTypeFilter Main -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*$GameName*" -or $_.PackageFullName -like "*$GameName*" }
    foreach ($p in $pkgs) {
        $found += [pscustomobject]@{
            Layout      = 'AppxPackage-record'
            Path        = $p.InstallLocation
            BytesGB     = $null
            HasEnvelope = $null
        }
    }
} catch { }

if ($found.Count -eq 0) {
    Write-Host "No matching install found." -ForegroundColor Yellow
    Write-Host "Make sure the title is installed and try a different substring." -ForegroundColor Yellow
    exit 1
}

$found | Format-Table -AutoSize

$msixvc = $found | Where-Object Layout -eq 'MSIXVC'
$plain  = $found | Where-Object Layout -eq 'plain-MSIX'

Write-Host ""
if ($msixvc) {
    Write-Host "VERDICT: MSIXVC - transfer experiment APPLIES." -ForegroundColor Green
    Write-Host "  Use this path as -GameFolder in xbox-transfer-sender.ps1:" -ForegroundColor Green
    Write-Host ("    `"$($msixvc[0].Path)`"") -ForegroundColor White
} elseif ($plain) {
    Write-Host "VERDICT: plain MSIX - this transfer strategy DOES NOT APPLY." -ForegroundColor Red
    Write-Host "  This title installs to WindowsApps via standard AppX deployment." -ForegroundColor Red
    Write-Host "  Pick an MSIXVC title instead (Pentiment, Hi-Fi Rush, Grounded)." -ForegroundColor Red
} else {
    Write-Host "VERDICT: ambiguous - inspect the rows above." -ForegroundColor Yellow
}
