# Diffs two manifests produced by hash-package.ps1 and classifies each file by
# intent so you can see whether real game content changed or only the Xbox
# packaging/streaming-container plumbing that the Store regenerates on every install.
#
# Usage:
#   .\diff-manifests.ps1 -A manifest-A.tsv -B manifest-B.tsv [-Csv report.csv]
#
# Implementation notes (Windows PowerShell 5.1):
#   - Keys are lowercased explicitly (NTFS paths are case-insensitive).
#   - Values are stored as a plain array @(hash,size,mtime,displayPath) and read
#     by index. Earlier versions used nested hashtables + Dictionary.TryGetValue([ref])
#     and `if`-as-hashtable-value; BOTH silently produced $null/0 in PS 5.1 and made
#     every file compare "identical". Do not reintroduce those constructs.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$A,
    [Parameter(Mandatory)][string]$B,
    [string]$Csv
)

function Load-Manifest($path) {
    $full = (Resolve-Path -LiteralPath $path).Path
    $m = @{}
    foreach ($line in [System.IO.File]::ReadLines($full)) {
        if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
        $t = $line -split "`t", 4
        if ($t.Count -lt 3) { continue }
        $sz = 0L; [void][int64]::TryParse($t[2], [ref]$sz)
        $mt = ''; if ($t.Count -ge 4) { $mt = $t[3] }
        $m[$t[0].ToLowerInvariant()] = @($t[1], $sz, $mt, $t[0])   # hash, size, mtime, displayPath
    }
    return $m
}

# Classify by INTENT, not just extension.
function Classify([string]$lowerPath) {
    if ($lowerPath -match '^ab03f40c' -or $lowerPath -match '\.(xvi|xvs|xct|smd|xsp|xss|x5e|xvd)$') { return 'streaming_container' }
    if ($lowerPath -match '\\media\\.*\.zip$')        { return 'content_chunk' }
    if ($lowerPath -match '\\media\\.*\.(bk2|bik)$')  { return 'content_video' }
    if ($lowerPath -match '\\media\\')                { return 'content_media_other' }
    if ($lowerPath -match '\\shaders?\\')             { return 'shaders' }
    if ($lowerPath -match '(appxmanifest\.xml|microsoftgame\.config|resources\.pri|networkmanifest\.xml|hash\.manifest|appxblockmap\.xml|appxsignature\.p7x|\.identity$|update\.alignmentchunk|game\.chunkmeta|layout_.*\.xml$)') { return 'package_metadata' }
    if ($lowerPath -match '\.(exe|dll)$')             { return 'binary' }
    return 'other'
}

Write-Host "Loading A: $A"
$mA = Load-Manifest $A
Write-Host "Loading B: $B"
$mB = Load-Manifest $B
Write-Host "A files: $($mA.Count) | B files: $($mB.Count)"

$union = @{}
foreach ($k in $mA.Keys) { $union[$k] = $true }
foreach ($k in $mB.Keys) { $union[$k] = $true }

$results = New-Object System.Collections.Generic.List[object]
foreach ($k in $union.Keys) {
    $inA = $mA.ContainsKey($k); $inB = $mB.ContainsKey($k)
    $sizeA = 0L; $hashA = ''; $mtA = ''; $disp = $k
    if ($inA) { $hashA = [string]$mA[$k][0]; $sizeA = [int64]$mA[$k][1]; $mtA = [string]$mA[$k][2]; $disp = $mA[$k][3] }
    $sizeB = 0L; $hashB = ''; $mtB = ''
    if ($inB) { $hashB = [string]$mB[$k][0]; $sizeB = [int64]$mB[$k][1]; $mtB = [string]$mB[$k][2]; if (-not $inA) { $disp = $mB[$k][3] } }

    if     (-not $inA) { $status = 'added' }
    elseif (-not $inB) { $status = 'removed' }
    elseif ($hashA -eq $hashB) { if ($mtA -ne $mtB) { $status = 'identical_rewritten' } else { $status = 'identical' } }
    else   { $status = 'changed' }

    $results.Add([pscustomobject]@{
        Path = $disp; Status = $status; Category = (Classify $k)
        SizeA = $sizeA; SizeB = $sizeB; HashA = $hashA; HashB = $hashB; MTimeA = $mtA; MTimeB = $mtB
    })
}

function Bytes-For($r) { if ($r.Status -eq 'removed') { return [int64]$r.SizeA } else { return [int64]$r.SizeB } }

# Per-status totals
$byStatus = @{}
foreach ($r in $results) {
    if (-not $byStatus.ContainsKey($r.Status)) { $byStatus[$r.Status] = @{ Files = 0; Bytes = [int64]0 } }
    $byStatus[$r.Status].Files++
    $byStatus[$r.Status].Bytes += (Bytes-For $r)
}
[int64]$totalA = 0; foreach ($v in $mA.Values) { $totalA += [int64]$v[1] }
[int64]$totalB = 0; foreach ($v in $mB.Values) { $totalB += [int64]$v[1] }
[int64]$survBytes = 0; $survFiles = 0
foreach ($s in 'identical','identical_rewritten') { if ($byStatus.ContainsKey($s)) { $survBytes += $byStatus[$s].Bytes; $survFiles += $byStatus[$s].Files } }

Write-Host ""
Write-Host "=== Headline ==="
Write-Host ("A total: {0,9:N2} GB ({1} files)   B total: {2,9:N2} GB ({3} files)" -f ($totalA/1GB), $mA.Count, ($totalB/1GB), $mB.Count)
foreach ($s in 'identical','identical_rewritten','changed','added','removed') {
    if ($byStatus.ContainsKey($s)) { Write-Host ("  {0,-20} {1,9:N3} GB ({2} files)" -f $s, ($byStatus[$s].Bytes/1GB), $byStatus[$s].Files) }
}
if ($totalA -gt 0) {
    Write-Host ("Content survival: {0:N2}%  byte-identical ({1} files, {2:N2} GB)" -f (100*$survBytes/$totalA), $survFiles, ($survBytes/1GB))
}

# CHANGED set split by intent
Write-Host ""
Write-Host "=== CHANGED set, by category ==="
$changed = $results | Where-Object Status -eq 'changed'
$catAgg = @{}
foreach ($r in $changed) {
    if (-not $catAgg.ContainsKey($r.Category)) { $catAgg[$r.Category] = @{ Files = 0; Bytes = [int64]0 } }
    $catAgg[$r.Category].Files++; $catAgg[$r.Category].Bytes += [int64]$r.SizeB
}
[int64]$chTot = 0; foreach ($r in $changed) { $chTot += [int64]$r.SizeB }
foreach ($c in ($catAgg.Keys | Sort-Object { $catAgg[$_].Bytes } -Descending)) {
    $pct = if ($chTot -gt 0) { 100*$catAgg[$c].Bytes/$chTot } else { 0 }
    Write-Host ("  {0,5} files  {1,10:N1} MB  ({2,5:N1}%)  {3}" -f $catAgg[$c].Files, ($catAgg[$c].Bytes/1MB), $pct, $c)
}

# added/removed are usually a short, interesting list
$ar = $results | Where-Object { $_.Status -in 'added','removed' } | Sort-Object Status, Path
if ($ar) {
    Write-Host ""
    Write-Host "=== added / removed ==="
    foreach ($r in $ar) { Write-Host ("  {0,-8} {1,8:N1} KB  {2}" -f $r.Status, ((Bytes-For $r)/1KB), $r.Path) }
}

if ($Csv) {
    $results | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8
    Write-Host ""
    Write-Host "Per-file report written to $Csv"
}
