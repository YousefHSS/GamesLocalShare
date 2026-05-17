<#
.SYNOPSIS
    Stage an Xbox PC game folder from PC A to an external location using robocopy.

.DESCRIPTION
    Runs robocopy with /B (backup semantics) and /COPYALL so ACLs, timestamps and
    attributes are preserved. By default we COPY only (no mirror); pass -Mirror
    to use /MIR if the staging destination already exists from a previous run.

.PARAMETER GameFolder
    Source path, e.g. "D:\XboxGames\Hi-Fi Rush".

.PARAMETER StagingRoot
    Parent destination directory (e.g. external SSD). The game folder name is
    appended automatically, so passing "E:\stage" with GameFolder
    "D:\XboxGames\Hi-Fi Rush" produces "E:\stage\Hi-Fi Rush".

.PARAMETER Mirror
    Use /MIR instead of /E. Destructive on the destination - off by default.

.PARAMETER LogDir
    Where to write the robocopy log + summary JSON.

.EXAMPLE
    .\02-stage-copy.ps1 -GameFolder "D:\XboxGames\Hi-Fi Rush" -StagingRoot "E:\stage"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GameFolder,
    [Parameter(Mandatory)] [string] $StagingRoot,
    [switch]                $Mirror,
    [switch]                $SkipElevationCheck,
    [switch]                $SkipLockedFiles,
    [string]                $LogDir = (Join-Path $PSScriptRoot 'runs')
)

$ErrorActionPreference = 'Stop'

function Test-IsElevated {
    try {
        $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
        return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

if (-not (Test-IsElevated)) {
    Write-Host "ERROR: This session is NOT elevated." -ForegroundColor Red
    Write-Host "Xbox PC / MSIX gaming packages contain files with SYSAPPID-conditional" -ForegroundColor Red
    Write-Host "ACLs that block reads from non-admin users. robocopy /B needs" -ForegroundColor Red
    Write-Host "SeBackupPrivilege, which is only granted to elevated processes." -ForegroundColor Red
    Write-Host ""
    Write-Host "Re-run from an elevated PowerShell (Run as Administrator)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Pass -SkipElevationCheck to attempt the copy anyway (locked .exes will fail)." -ForegroundColor DarkGray
    if (-not $SkipElevationCheck) { exit 1 }
}

if (-not (Test-Path -LiteralPath $GameFolder)) {
    throw "GameFolder not found: $GameFolder"
}

$gameName = Split-Path -Path $GameFolder -Leaf
$dest     = Join-Path $StagingRoot $gameName
$null     = New-Item -ItemType Directory -Path $dest    -Force
$null     = New-Item -ItemType Directory -Path $LogDir  -Force

$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$logPath   = Join-Path $LogDir "stage-$timestamp.log"
$summaryPath = Join-Path $LogDir "stage-$timestamp.json"

$copyFlag = if ($Mirror) { '/MIR' } else { '/E' }

# Pre-scan for unreadable files (MSIX-protected exes that even admin + /B
# can't read; would need NT AUTHORITY\SYSTEM). When -SkipLockedFiles is set,
# add them to robocopy's /XF exclude list so the run finishes cleanly.
$unreadable = New-Object System.Collections.Generic.List[string]
if ($SkipLockedFiles) {
    Write-Host "Pre-scanning for unreadable files..." -ForegroundColor Cyan
    foreach ($f in Get-ChildItem -LiteralPath $GameFolder -Recurse -Force -File -ErrorAction SilentlyContinue) {
        try {
            $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            $null = $fs.ReadByte()
            $fs.Close()
        } catch {
            $unreadable.Add($f.FullName) | Out-Null
        }
    }
    Write-Host ("  Unreadable: {0} file(s)" -f $unreadable.Count) -ForegroundColor DarkYellow
    if ($unreadable.Count -gt 0) {
        $unreadableListPath = Join-Path $LogDir "stage-$timestamp.skipped.txt"
        $unreadable | Set-Content -LiteralPath $unreadableListPath -Encoding UTF8
        Write-Host ("  Skipped list: {0}" -f $unreadableListPath) -ForegroundColor DarkGray
    }
}

# /B   = backup mode (needs admin) - bypasses DACLs (but NOT MSIX package guards)
# /COPYALL = copy Data + Attributes + Timestamps + NTFS ACLs + Owner + Auditing
# /R:1 /W:1 = retry once, wait 1s on errors instead of robocopy's defaults
# /NP  = no per-file progress (keeps log readable)
# /MT:16 = multi-threaded
$robocopyArgs = New-Object System.Collections.Generic.List[string]
$robocopyArgs.Add("`"$GameFolder`"") | Out-Null
$robocopyArgs.Add("`"$dest`"")       | Out-Null
$robocopyArgs.Add($copyFlag)         | Out-Null
$robocopyArgs.Add('/B')              | Out-Null
$robocopyArgs.Add('/COPYALL')        | Out-Null
if ($unreadable.Count -gt 0) {
    $robocopyArgs.Add('/XF') | Out-Null
    foreach ($u in $unreadable) { $robocopyArgs.Add("`"$u`"") | Out-Null }
}
$robocopyArgs.Add('/R:1')  | Out-Null
$robocopyArgs.Add('/W:1')  | Out-Null
$robocopyArgs.Add('/NP')   | Out-Null
$robocopyArgs.Add('/MT:16')| Out-Null
$robocopyArgs.Add("/LOG:`"$logPath`"") | Out-Null

Write-Host "Staging '$GameFolder' -> '$dest'" -ForegroundColor Cyan
Write-Host "robocopy $($robocopyArgs -join ' ')" -ForegroundColor DarkGray

$proc = Start-Process -FilePath 'robocopy.exe' -ArgumentList $robocopyArgs -NoNewWindow -Wait -PassThru
$exit = $proc.ExitCode

# Robocopy exit codes: 0-7 are success-ish, 8+ indicate failure.
$success = ($exit -lt 8)

# Measure resulting destination size.
$destFiles = Get-ChildItem -LiteralPath $dest -Recurse -Force -File -ErrorAction SilentlyContinue
$destBytes = ($destFiles | Measure-Object -Property Length -Sum).Sum
$destCount = $destFiles.Count

$summary = [pscustomobject]@{
    StartedAtUtc    = (Get-Date).ToUniversalTime().ToString('o')
    Host            = $env:COMPUTERNAME
    Source          = $GameFolder
    Destination     = $dest
    Mirror          = [bool]$Mirror
    SkipLockedFiles = [bool]$SkipLockedFiles
    SkippedCount    = $unreadable.Count
    SkippedFiles    = @($unreadable)
    RobocopyExit    = $exit
    Success         = $success
    DestBytes       = $destBytes
    DestFileCount   = $destCount
    LogPath         = $logPath
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
Write-Host ("Robocopy exit code : {0} ({1})" -f $exit, $(if ($success) { 'OK' } else { 'FAILED' })) -ForegroundColor $(if ($success) { 'Green' } else { 'Red' })
Write-Host ("Destination size   : {0:N0} bytes ({1:N2} GB) across {2:N0} files" -f $destBytes, ($destBytes / 1GB), $destCount)
Write-Host ("Log                : {0}" -f $logPath)
Write-Host ("Summary            : {0}" -f $summaryPath)

if (-not $success) {
    exit $exit
}
