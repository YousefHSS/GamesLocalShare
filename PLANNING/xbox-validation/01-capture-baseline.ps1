<#
.SYNOPSIS
    Capture the baseline state of an Xbox PC game install on PC A.

.DESCRIPTION
    Records package identity (PackageFullName, PackageFamilyName, Version, SignatureKind),
    full file tree, per-file SHA256 hashes of small metadata files, and total size on disk.
    Output is written as JSON + text under -OutDir for later comparison on PC B.

.PARAMETER GameFolder
    Full path to the game's top-level folder under XboxGames, e.g. "D:\XboxGames\Hi-Fi Rush".

.PARAMETER OutDir
    Directory to write baseline artifacts into. Created if missing.

.EXAMPLE
    .\01-capture-baseline.ps1 -GameFolder "D:\XboxGames\Hi-Fi Rush" -OutDir .\runs\baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GameFolder,
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $GameFolder)) {
    throw "GameFolder not found: $GameFolder"
}

$null = New-Item -ItemType Directory -Path $OutDir -Force

Write-Host "[1/5] Resolving package identity..." -ForegroundColor Cyan

# Strategy:
#   a) Match any AppX package whose InstallLocation lives under the game folder
#      OR under its Content subdir (canonical Xbox layout).
#   b) If that fails, sniff the Content folder ACL for a SYSAPPID conditional
#      ACE (e.g. WIN://SYSAPPID Contains "<PackageFamilyName>"), and look up
#      the package by family name. This handles Humble / non-store MSIX installs
#      where InstallLocation can be elsewhere.
$candidatePaths = @($GameFolder)
$contentPath = Join-Path $GameFolder 'Content'
if (Test-Path -LiteralPath $contentPath) { $candidatePaths += $contentPath }

$allPackages = @()
try {
    $allPackages = @(Get-AppxPackage -AllUsers -ErrorAction Stop)
} catch {
    Write-Host "  Get-AppxPackage -AllUsers failed ($($_.Exception.Message.Trim())). Falling back to current-user enumeration." -ForegroundColor DarkYellow
    try {
        $allPackages = @(Get-AppxPackage -ErrorAction Stop)
    } catch {
        Write-Host "  Get-AppxPackage also failed: $($_.Exception.Message.Trim())" -ForegroundColor DarkYellow
    }
}

$pkg = $allPackages |
    Where-Object {
        $loc = $_.InstallLocation
        if (-not $loc) { return $false }
        foreach ($p in $candidatePaths) {
            if ($loc -ieq $p -or $loc.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        return $false
    } |
    Select-Object -First 1

if (-not $pkg) {
    # Fallback (b): sniff SYSAPPID from the Content ACL.
    $sysAppId = $null
    if (Test-Path -LiteralPath $contentPath) {
        try {
            $sddl = (Get-Acl -LiteralPath $contentPath).Sddl
            $m = [regex]::Match($sddl, 'SYSAPPID\s+Contains\s+"([^"]+)"', 'IgnoreCase')
            if ($m.Success) { $sysAppId = $m.Groups[1].Value }
        } catch { }
    }
    if ($sysAppId) {
        Write-Host "  Sniffed SYSAPPID from ACL: $sysAppId" -ForegroundColor DarkGray
        $pkg = $allPackages | Where-Object { $_.PackageFamilyName -ieq $sysAppId } | Select-Object -First 1
        if (-not $pkg) {
            $pkg = $allPackages | Where-Object { $_.PackageFullName -ieq $sysAppId } | Select-Object -First 1
        }
    }
}

if (-not $pkg) {
    Write-Warning "No AppX package matched $GameFolder. Falling back to folder-only metadata."
}

Write-Host "[2/5] Walking file tree..." -ForegroundColor Cyan
$files = Get-ChildItem -LiteralPath $GameFolder -Recurse -Force -File -ErrorAction SilentlyContinue
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
$fileCount  = $files.Count

Write-Host "[3/5] Hashing small metadata files (<= 2 MB)..." -ForegroundColor Cyan
$hashSkipped = 0
$hashes = foreach ($f in $files) {
    if ($f.Length -le 2MB) {
        $h = Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256 -ErrorAction SilentlyContinue
        if ($h) {
            [pscustomobject]@{
                RelativePath = $f.FullName.Substring($GameFolder.Length).TrimStart('\','/')
                SizeBytes    = $f.Length
                SHA256       = $h.Hash
            }
        } else {
            $hashSkipped++
        }
    }
}

Write-Host "[4/5] Sampling ACLs on Content folder..." -ForegroundColor Cyan
$aclSample = $null
if (Test-Path -LiteralPath $contentPath) {
    try {
        $aclSample = (Get-Acl -LiteralPath $contentPath).Sddl
    } catch {
        $aclSample = "ERROR: $($_.Exception.Message)"
    }
}

Write-Host "[5/5] Writing baseline JSON..." -ForegroundColor Cyan
$baseline = [pscustomobject]@{
    CapturedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
    Host               = $env:COMPUTERNAME
    GameFolder         = $GameFolder
    TotalBytes         = $totalBytes
    FileCount          = $fileCount
    ContentAclSddl     = $aclSample
    Package            = if ($pkg) {
        [pscustomobject]@{
            Name              = $pkg.Name
            PackageFullName   = $pkg.PackageFullName
            PackageFamilyName = $pkg.PackageFamilyName
            Publisher         = $pkg.Publisher
            Architecture      = $pkg.Architecture.ToString()
            Version           = $pkg.Version.ToString()
            SignatureKind     = $pkg.SignatureKind.ToString()
            InstallLocation   = $pkg.InstallLocation
        }
    } else { $null }
    SmallFileHashes    = $hashes
}

$jsonPath = Join-Path $OutDir 'baseline.json'
$baseline | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

# Also dump a flat tree listing for human inspection.
$treePath = Join-Path $OutDir 'tree.txt'
$files |
    Sort-Object FullName |
    ForEach-Object { '{0,12}  {1}' -f $_.Length, $_.FullName.Substring($GameFolder.Length).TrimStart('\','/') } |
    Set-Content -LiteralPath $treePath -Encoding UTF8

Write-Host ""
Write-Host "Baseline written:" -ForegroundColor Green
Write-Host "  JSON : $jsonPath"
Write-Host "  Tree : $treePath"
Write-Host ""
Write-Host ("Total size: {0:N0} bytes ({1:N2} GB) across {2:N0} files" -f $totalBytes, ($totalBytes / 1GB), $fileCount)
if ($hashSkipped -gt 0) {
    Write-Host ("Hashes    : {0} small files skipped (access denied / locked)" -f $hashSkipped) -ForegroundColor DarkYellow
}
if ($pkg) {
    Write-Host "Package   : $($pkg.PackageFullName)"
    Write-Host "Family    : $($pkg.PackageFamilyName)"
    Write-Host "Signature : $($pkg.SignatureKind)"
}
