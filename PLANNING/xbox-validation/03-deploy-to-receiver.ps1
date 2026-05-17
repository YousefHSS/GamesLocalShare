<#
.SYNOPSIS
    Deploy a staged Xbox PC game folder onto PC B's XboxGames directory.

.DESCRIPTION
    Stops the Xbox app / Gaming Services processes (with consent), then
    robocopies the staged game folder into <DestRoot>\<GameName>. By default
    the destination must NOT pre-exist (we want a clean baseline). Pass
    -AllowExisting to overwrite, -Mirror to use /MIR.

.PARAMETER StagedFolder
    The per-game folder produced by 02-stage-copy.ps1, e.g. "E:\stage\Hi-Fi Rush".

.PARAMETER DestRoot
    The XboxGames root on PC B, e.g. "D:\XboxGames".

.PARAMETER AllowExisting
    Permit deploying into an existing destination folder.

.PARAMETER Mirror
    Use /MIR (destructive). Off by default.

.PARAMETER SkipStopXbox
    Do not attempt to stop Xbox / Gaming Services processes.

.EXAMPLE
    .\03-deploy-to-receiver.ps1 -StagedFolder "E:\stage\Hi-Fi Rush" -DestRoot "D:\XboxGames"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $StagedFolder,
    [Parameter(Mandatory)] [string] $DestRoot,
    [switch] $AllowExisting,
    [switch] $Mirror,
    [switch] $SkipStopXbox,
    [switch] $SkipElevationCheck,
    [string] $LogDir = (Join-Path $PSScriptRoot 'runs')
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
    Write-Host "Writing into XboxGames/WindowsApps with the source ACLs (/B /COPYALL)" -ForegroundColor Red
    Write-Host "requires SeBackupPrivilege + SeRestorePrivilege, only granted to admin." -ForegroundColor Red
    Write-Host ""
    Write-Host "Re-run from an elevated PowerShell (Run as Administrator)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Pass -SkipElevationCheck to attempt the copy anyway." -ForegroundColor DarkGray
    if (-not $SkipElevationCheck) { exit 1 }
}

if (-not (Test-Path -LiteralPath $StagedFolder)) {
    throw "StagedFolder not found: $StagedFolder"
}

$gameName = Split-Path -Path $StagedFolder -Leaf
$dest     = Join-Path $DestRoot $gameName

if ((Test-Path -LiteralPath $dest) -and -not $AllowExisting) {
    throw "Destination already exists: $dest. Re-run with -AllowExisting if intended."
}

if (-not (Test-Path -LiteralPath $DestRoot)) {
    Write-Host "Creating destination root: $DestRoot" -ForegroundColor Yellow
    $null = New-Item -ItemType Directory -Path $DestRoot -Force
}
$null = New-Item -ItemType Directory -Path $dest   -Force
$null = New-Item -ItemType Directory -Path $LogDir -Force

if (-not $SkipStopXbox) {
    $procNames = @('XboxPcApp', 'XboxApp', 'GamingServices', 'GamingServicesNet', 'gamebar', 'GameBarPresenceWriter')
    $running = Get-Process -Name $procNames -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping Xbox / Gaming Services processes:" -ForegroundColor Yellow
        $running | Select-Object Id, ProcessName | Format-Table | Out-Host
        $confirm = Read-Host "Proceed with Stop-Process? [y/N]"
        if ($confirm -match '^[Yy]') {
            $running | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        } else {
            Write-Host "Skipped stopping processes (your call)." -ForegroundColor DarkYellow
        }
    }
}

$timestamp   = (Get-Date).ToString('yyyyMMdd-HHmmss')
$logPath     = Join-Path $LogDir "deploy-$timestamp.log"
$summaryPath = Join-Path $LogDir "deploy-$timestamp.json"

$copyFlag = if ($Mirror) { '/MIR' } else { '/E' }
$robocopyArgs = @(
    "`"$StagedFolder`"",
    "`"$dest`"",
    $copyFlag,
    '/B',
    '/COPYALL',
    '/R:1',
    '/W:1',
    '/NP',
    '/MT:16',
    "/LOG:`"$logPath`""
)

Write-Host "Deploying '$StagedFolder' -> '$dest'" -ForegroundColor Cyan
$proc = Start-Process -FilePath 'robocopy.exe' -ArgumentList $robocopyArgs -NoNewWindow -Wait -PassThru
$exit = $proc.ExitCode
$success = ($exit -lt 8)

$destFiles = Get-ChildItem -LiteralPath $dest -Recurse -Force -File -ErrorAction SilentlyContinue
$destBytes = ($destFiles | Measure-Object -Property Length -Sum).Sum
$destCount = $destFiles.Count

# Sample ACL on the deployed Content folder so we know whether we need to fix
# permissions before the Xbox app can read them.
$contentPath = Join-Path $dest 'Content'
$aclSddl = $null
if (Test-Path -LiteralPath $contentPath) {
    try { $aclSddl = (Get-Acl -LiteralPath $contentPath).Sddl } catch { $aclSddl = "ERROR: $($_.Exception.Message)" }
}

$summary = [pscustomobject]@{
    StartedAtUtc   = (Get-Date).ToUniversalTime().ToString('o')
    Host           = $env:COMPUTERNAME
    Source         = $StagedFolder
    Destination    = $dest
    Mirror         = [bool]$Mirror
    AllowExisting  = [bool]$AllowExisting
    RobocopyExit   = $exit
    Success        = $success
    DestBytes      = $destBytes
    DestFileCount  = $destCount
    ContentAclSddl = $aclSddl
    LogPath        = $logPath
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ""
Write-Host ("Robocopy exit code : {0} ({1})" -f $exit, $(if ($success) { 'OK' } else { 'FAILED' })) -ForegroundColor $(if ($success) { 'Green' } else { 'Red' })
Write-Host ("Destination size   : {0:N0} bytes ({1:N2} GB) across {2:N0} files" -f $destBytes, ($destBytes / 1GB), $destCount)
Write-Host ("Log                : {0}" -f $logPath)
Write-Host ("Summary            : {0}" -f $summaryPath)
Write-Host ""
Write-Host "Next: run 04-try-recognition.ps1 on this PC." -ForegroundColor Cyan

if (-not $success) { exit $exit }
