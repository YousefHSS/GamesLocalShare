<#
.SYNOPSIS
    Snapshot the full Gaming Services "trust state" for an MSIXVC title:
    registry (GamingServices), on-disk streaming metadata (.xvi/.xvs/.xsp/
    .xct/.smd + GUID blob) with SHA256, ACLs, and AppX package state.

    Run it twice (e.g. -Label pre-launch, then -Label post-launch) and diff
    the two output folders to see exactly what first-launch / an update mutates.

.PARAMETER ContentGuid
    The MSIXVC content GUID (basename of the .xvi). Default = Forza Horizon 6.

.PARAMETER GameRoot
    Live install folder containing the .xvi/.xvs/.xsp/.smd + GUID blob.

.PARAMETER Label
    Free-text label appended to the snapshot folder name.

.EXAMPLE
    .\snapshot-trust-state.ps1 -Label pre-launch
#>
param(
    [string] $ContentGuid = 'AB03F40C-E85D-467B-8C67-3870B89BD2D1',
    [string] $GameRoot     = 'F:\Games\Forza Horizon 6',
    [string] $Label        = 'snap'
)

$ErrorActionPreference = 'Continue'
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$outDir = Join-Path $PSScriptRoot ("runs\trust-snapshots\{0}-{1}" -f $stamp, $Label)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Write-Host "Snapshot -> $outDir" -ForegroundColor Green

# 1. Full GamingServices registry export (complete + diffable).
$regOut = Join-Path $outDir 'GamingServices.reg'
& reg.exe export 'HKLM\SOFTWARE\Microsoft\GamingServices' $regOut /y | Out-Null
Write-Host "  [reg] GamingServices exported"

# 2. Focused registry values (human-readable JSON) for the target package.
$gs = 'HKLM:\SOFTWARE\Microsoft\GamingServices'
$reg = [ordered]@{}
foreach ($sub in 'StreamingCheckpoints','StreamingTracking','StreamingSummaries','PackageBackups','StreamingServices','StreamingRequests') {
    $node = [ordered]@{}
    Get-ChildItem "$gs\$sub" -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $vals = [ordered]@{}
        (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).PSObject.Properties |
            Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { $vals[$_.Name] = "$($_.Value)" }
        $node[$_.Name] = $vals
    }
    $reg[$sub] = $node
}
# PackageRepository entries that mention our GUID
foreach ($sub in 'Metadata','Root','Package') {
    $node = [ordered]@{}
    Get-ChildItem "$gs\PackageRepository\$sub" -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match $ContentGuid } | ForEach-Object {
            $vals = [ordered]@{}
            (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).PSObject.Properties |
                Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { $vals[$_.Name] = "$($_.Value)" }
            Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                $leaf = [ordered]@{}
                (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).PSObject.Properties |
                    Where-Object { $n = $_.Name; $n -notmatch '^PS' } | ForEach-Object { $leaf[$_.Name] = "$($_.Value)" }
                $vals["[child] $($_.PSChildName)"] = $leaf
            }
            $node[$_.PSChildName] = $vals
        }
    $reg["PackageRepository\$sub"] = $node
}
$reg | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $outDir 'gs-registry.json')
Write-Host "  [reg] focused JSON written"

# 3. On-disk streaming metadata: size, mtime, SHA256, ACL.
$files = @()
if (Test-Path -LiteralPath $GameRoot) {
    Get-ChildItem -LiteralPath $GameRoot -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.xvi','.xvs','.xct','.smd','.xsp' -or $_.Name -eq $ContentGuid -or $_.Name -like "$ContentGuid.*" } |
        ForEach-Object {
            $sha = $null
            try { $sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 -ErrorAction Stop).Hash } catch { $sha = "ERR:$($_.Exception.Message)" }
            $acl = $null
            try { $acl = (Get-Acl -LiteralPath $_.FullName).Sddl } catch {}
            $files += [ordered]@{
                Name=$_.Name; Length=$_.Length; LastWriteUtc=$_.LastWriteTimeUtc.ToString('o'); Sha256=$sha; Sddl=$acl
            }
        }
    # Folder-level ACL too (first-launch sometimes re-stamps it).
    $rootAcl = $null; try { $rootAcl = (Get-Acl -LiteralPath $GameRoot).Sddl } catch {}
    [ordered]@{ Root=$GameRoot; RootSddl=$rootAcl; Files=$files } | ConvertTo-Json -Depth 6 |
        Set-Content (Join-Path $outDir 'disk-metadata.json')
    Write-Host "  [disk] $($files.Count) streaming-metadata files hashed"
} else {
    Write-Host "  [disk] GameRoot not found: $GameRoot" -ForegroundColor Yellow
}

# 4. AppX package state (may be empty for MSIXVC, capture anyway).
try {
    Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Forte|Forza' } |
        Select-Object Name,PackageFullName,Version,InstallLocation,Status,IsBundle,SignatureKind |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outDir 'appx-state.json')
} catch {}

# 5. AppRepository folder listing for the package (state DB side-files).
$arBase = "$env:ProgramData\Microsoft\Windows\AppRepository\Packages"
Get-ChildItem $arBase -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'ForteBaseGame' } |
    ForEach-Object { Get-ChildItem $_.FullName -Force -ErrorAction SilentlyContinue |
        Select-Object FullName,Length,@{n='LastWriteUtc';e={$_.LastWriteTimeUtc.ToString('o')}} } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outDir 'apprepository.json')

Write-Host "Done." -ForegroundColor Green
Write-Host $outDir
