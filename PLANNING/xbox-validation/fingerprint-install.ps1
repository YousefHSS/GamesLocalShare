# Fingerprints an installed/transferred MSIXVC game so a CLEAN install and a
# TRANSFERRED install can be diffed to find the update/verify-trust trigger.
# ASCII-only. Run as Administrator (needs HKLM GamingServices + protected files).
#
# Usage:
#   .\fingerprint-install.ps1 -GameDir "F:\Games\Clone Drone in the Danger Zone" -Out clean.txt
#   .\fingerprint-install.ps1 -GameDir "C:\XboxGames\Clone Drone in the Danger Zone" -Out transferred.txt
# Then diff the two .txt files.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$Out
)

$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$gs = 'HKLM:\SOFTWARE\Microsoft\GamingServices'
$L  = New-Object System.Collections.Generic.List[string]
function W($s){ $L.Add([string]$s) }

W "MSIXVC INSTALL FINGERPRINT"
W ("Host:    " + $env:COMPUTERNAME)
W ("When:    " + (Get-Date).ToString('o'))
W ("GameDir: " + $GameDir)
W "==================================================================="

# --- Find the content GUID from the .xvi/.xvs filenames at the top level ---
$metaFiles = @(Get-ChildItem -LiteralPath $GameDir -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.xvi','.xvs','.xct','.smd','.xsp' -or $_.Name -match '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$' })
$contentGuid = $null
foreach($f in $metaFiles){ if($f.Extension -eq '.xvi'){ $contentGuid = $f.BaseName } }
W ("Content GUID: " + $contentGuid)

# --- Hash every streaming-metadata file (these are the transfer-sensitive bits) ---
W ""
W "## Streaming-metadata files (size + full sha256) ##"
foreach($f in ($metaFiles | Sort-Object Name)){
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    W ("  {0,12} bytes  {1}  {2}" -f $f.Length, $h, $f.Name)
}

# --- Decode .xvs (UTF-16 JSON streaming-state) and surface the trust-relevant fields ---
W ""
W "## .xvs decoded (key fields) ##"
$xvs = $metaFiles | Where-Object { $_.Extension -eq '.xvs' } | Select-Object -First 1
if($xvs){
    try{
        $txt = [System.IO.File]::ReadAllText($xvs.FullName, [System.Text.Encoding]::Unicode)
        $j = $txt | ConvertFrom-Json
        W ("  Request.Type        = " + $j.Request.Type)
        W ("  Request.InstanceId  = " + $j.Request.InstanceId)
        W ("  Request.StoreId     = " + $j.Request.StoreId)
        W ("  Request.ShaderState = " + $j.Request.ShaderStateId)
        W ("  Status.Operation    = " + $j.Status.Operation)
        W ("  Status.Result       = " + $j.Status.Result)
        W ("  Source.Current.BuildId      = " + $j.Status.Source.Current.BuildId)
        W ("  Source.Current.BuildVersion = " + $j.Status.Source.Current.BuildVersion)
        W ("  Progress.Package.TotalBytes    = " + $j.Status.Progress.Package.TotalBytes)
        W ("  Progress.Package.StreamedBytes = " + $j.Status.Progress.Package.StreamedBytes)
        $complete = ($j.Status.Progress.Package.TotalBytes -eq $j.Status.Progress.Package.StreamedBytes)
        W ("  => StreamedBytes == TotalBytes ? " + $complete)
        W ("  Constraint.PackageBuildId   = " + $j.Constraint.PackageBuildId)
        W ("  raw length (chars) = " + $txt.Length)
    } catch {
        W ("  (failed to parse .xvs as JSON: " + $_.Exception.Message + ")")
    }
} else { W "  (no .xvs file)" }

# --- .xvi header (block bitmap format marker) ---
W ""
W "## .xvi header ##"
$xvi = $metaFiles | Where-Object { $_.Extension -eq '.xvi' } | Select-Object -First 1
if($xvi){
    $b = [System.IO.File]::ReadAllBytes($xvi.FullName)
    $hex = ($b[0..([Math]::Min(15,$b.Length-1))] | ForEach-Object { $_.ToString('x2') }) -join ' '
    $asc = -join ($b[0..([Math]::Min(15,$b.Length-1))] | ForEach-Object { if($_ -ge 32 -and $_ -lt 127){[char]$_}else{'.'} })
    W ("  hex:   " + $hex)
    W ("  ascii: " + $asc)
}

# --- GamingServices registry: Root + Metadata for this content GUID ---
W ""
W "## GamingServices PackageRepository (Root + Metadata) ##"
foreach($leaf in 'Root','Metadata'){
    foreach($sub in (Get-ChildItem (Join-Path $gs "PackageRepository\$leaf") -Recurse -ErrorAction SilentlyContinue)){
        $p = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
        $vals = ($p.PSObject.Properties | Where-Object Name -notmatch '^PS' | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; '
        if($contentGuid -and ("$($sub.PSChildName) $vals" -match $contentGuid)){
            W ("  [$leaf] " + $sub.PSChildName)
            if($vals){ W ("     " + $vals) }
        }
    }
}

# --- StreamingSummaries entry for this content GUID ---
W ""
W "## StreamingSummaries entry ##"
foreach($sub in (Get-ChildItem (Join-Path $gs 'StreamingSummaries') -ErrorAction SilentlyContinue)){
    $p = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
    foreach($pp in ($p.PSObject.Properties | Where-Object Name -notmatch '^PS')){
        if($contentGuid -and $pp.Name -match $contentGuid){
            W ("  " + $sub.PSChildName + " : " + $pp.Name + " = " + $pp.Value)
        }
    }
}

# --- Folder ACL summary (package SID / SYSAPPID conditional ACE?) ---
W ""
W "## Folder ACL (package-SID / conditional ACE check) ##"
$acl = Get-Acl -LiteralPath $GameDir
foreach($a in $acl.Access){
    if($a.IdentityReference -match 'S-1-15-2|APPLICATION PACKAGE|S-1-15-3'){
        W ("  " + $a.IdentityReference + "  " + $a.FileSystemRights + "  " + $a.AccessControlType)
    }
}
W ("  (owner: " + $acl.Owner + ")")

# --- Total content size ---
$all = @(Get-ChildItem -LiteralPath $GameDir -Recurse -File -Force -ErrorAction SilentlyContinue)
$sum = ($all | Measure-Object Length -Sum).Sum
W ""
W ("## TOTAL: {0} files, {1:N2} GB ##" -f $all.Count, ($sum/1GB))

[System.IO.File]::WriteAllLines($Out, $L)
Write-Host ("Wrote " + $Out + " (" + $L.Count + " lines)") -ForegroundColor Green
