<#
.SYNOPSIS
    End-to-end, no-prompt sender for the Xbox PC pre-staged transfer experiment.

.DESCRIPTION
    Run this on PC A (the source). It will:
      1. Self-elevate to admin via UAC if not already elevated.
      2. Download PsExec on first run (from live.sysinternals.com).
      3. Re-launch itself as NT AUTHORITY\SYSTEM so it can read MSIX-protected files.
      4. Snapshot package identity (PFN sniffed from ACL of source folder).
      5. Robocopy the full game folder to -Destination with /COPYALL /B /MIR.
      6. Write transfer-summary.json next to the staged data so the receiver
         script can pick up the PFN, file count and source size without
         human typing.

.PARAMETER GameFolder
    Source path of the installed game, e.g. "F:\Games\A Short Hike".

.PARAMETER Destination
    Target path to write the staged copy to. Can be:
      - a local path on a removable drive (e.g. "E:\stage")
      - a UNC path on the receiver PC (e.g. "\\PCB\Drop")
    The game folder name is appended automatically; if Destination is
    "E:\stage" and GameFolder is "F:\Games\A Short Hike", the staged copy
    lands at "E:\stage\A Short Hike".

.EXAMPLE
    .\xbox-transfer-sender.ps1 -GameFolder "F:\Games\A Short Hike" -Destination "E:\stage"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GameFolder,
    [Parameter(Mandatory)] [string] $Destination,
    [switch] $InternalSystemPhase   # set by Invoke-AsSystem when we re-enter
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$scriptPath = $MyInvocation.MyCommand.Path
$runsDir    = Join-Path $PSScriptRoot 'runs'
$toolsDir   = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Path $runsDir  -Force | Out-Null
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

# Normalize paths
$GameFolder  = (Resolve-Path -LiteralPath $GameFolder).Path
$gameName    = Split-Path -Path $GameFolder -Leaf
$destRoot    = $Destination.TrimEnd('\','/')
$destGame    = Join-Path $destRoot $gameName

# ---------------------------------------------------------------------------
# Phase 0: ensure elevation, then re-launch as SYSTEM
# ---------------------------------------------------------------------------
if (-not $InternalSystemPhase) {
    Assert-Elevated -ScriptPath $scriptPath -ScriptArgs @(
        '-GameFolder', "`"$GameFolder`"",
        '-Destination', "`"$Destination`""
    )

    Write-Host ""
    Write-Host "=== xbox-transfer-sender ===" -ForegroundColor Green
    Write-Host "  GameFolder:  $GameFolder"
    Write-Host "  Destination: $destGame"
    Write-Host ""

    $psexec = Ensure-PsExec -ToolsDir $toolsDir
    $stamp  = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $sysLog = Join-Path $runsDir "sender-system-$stamp.log"

    $code = Invoke-AsSystem -ScriptPath $scriptPath `
        -ScriptArgs @(
            '-GameFolder', "`"$GameFolder`"",
            '-Destination', "`"$Destination`"",
            '-InternalSystemPhase'
        ) `
        -LogPath $sysLog `
        -PsExecPath $psexec

    Write-Host ""
    Write-Host "SYSTEM phase exited with code $code" -ForegroundColor Cyan
    Write-Host "Full log: $sysLog"
    if (Test-Path "$sysLog.err") {
        $errSize = (Get-Item "$sysLog.err").Length
        if ($errSize -gt 0) { Write-Host "Stderr:   $sysLog.err ($errSize bytes)" }
    }

    # Surface the summary
    $summaryPath = Join-Path $destGame 'transfer-summary.json'
    if (Test-Path -LiteralPath $summaryPath) {
        Write-Host ""
        Write-Host "Transfer summary written to: $summaryPath" -ForegroundColor Green
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        Write-Host ("  PackageFamilyName: {0}" -f $summary.PackageFamilyName)
        Write-Host ("  Files copied:      {0}" -f $summary.FilesCopied)
        Write-Host ("  Bytes copied:      {0:N0}  ({1:N1} MB)" -f $summary.BytesCopied, ($summary.BytesCopied/1MB))
        Write-Host ("  Skipped files:     {0}" -f $summary.SkippedFiles)
        Write-Host ("  Robocopy exit:     {0}" -f $summary.RobocopyExit)
    } else {
        Write-Host "WARNING: transfer-summary.json not found at $summaryPath" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "Next step: move the data to PC B and run xbox-transfer-receiver.ps1" -ForegroundColor Yellow
    exit $code
}

# ---------------------------------------------------------------------------
# Phase 1: running as SYSTEM - actually do the work
# ---------------------------------------------------------------------------
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Host "[SYSTEM phase] Identity: $identity"
if (-not (Test-IsSystem)) {
    Write-Host "WARNING: expected NT AUTHORITY\SYSTEM, got $identity" -ForegroundColor Yellow
}

if (-not (Test-Path -LiteralPath $GameFolder)) { throw "GameFolder not found: $GameFolder" }

# Sniff package family name from the source folder's ACL
$pfn = Get-SysAppIdFromAcl -Path $GameFolder
if (-not $pfn) {
    # Try the Content subfolder
    $contentDir = Join-Path $GameFolder 'Content'
    if (Test-Path -LiteralPath $contentDir) {
        $pfn = Get-SysAppIdFromAcl -Path $contentDir
    }
}
Write-Host "[SYSTEM phase] Sniffed PackageFamilyName: $pfn"

# Make sure destination root exists
if (-not (Test-Path -LiteralPath $destRoot)) {
    New-Item -ItemType Directory -Path $destRoot -Force | Out-Null
}

# Pre-scan source to count files and bytes; also check readability
Write-Host "[SYSTEM phase] Scanning source..."
$allFiles    = @(Get-ChildItem -LiteralPath $GameFolder -Recurse -File -Force -ErrorAction SilentlyContinue)
$totalBytes  = ($allFiles | Measure-Object -Sum Length).Sum
$totalCount  = $allFiles.Count
$unreadable  = @()
foreach ($f in $allFiles) {
    try {
        $fs = [System.IO.File]::Open($f.FullName,'Open','Read','ReadWrite')
        $fs.Close()
    } catch {
        $unreadable += $f.FullName
    }
}
Write-Host ("[SYSTEM phase] Files: {0}, Bytes: {1:N0} ({2:N1} MB), Unreadable: {3}" -f `
    $totalCount, $totalBytes, ($totalBytes/1MB), $unreadable.Count)

# Robocopy
$stamp     = (Get-Date).ToString('yyyyMMdd-HHmmss')
$rcLog     = Join-Path $runsDir "sender-robocopy-$stamp.log"
$rcArgs    = @(
    "`"$GameFolder`"", "`"$destGame`"", '/E','/COPYALL','/B','/DCOPY:DAT',
    '/R:1','/W:2','/MT:8','/NP','/NDL','/TEE',
    "/LOG+:$rcLog"
)
Write-Host "[SYSTEM phase] robocopy starting..."
$proc = Start-Process -FilePath 'robocopy.exe' -ArgumentList $rcArgs -NoNewWindow -PassThru -Wait
$rcExit = $proc.ExitCode
Write-Host "[SYSTEM phase] robocopy exit: $rcExit (0/1/2/3 = success)"

# Re-scan destination for verification
$destFiles   = @(Get-ChildItem -LiteralPath $destGame -Recurse -File -Force -ErrorAction SilentlyContinue)
$destBytes   = ($destFiles | Measure-Object -Sum Length).Sum
$destCount   = $destFiles.Count

$summary = [ordered]@{
    StartedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
    SenderHost        = $env:COMPUTERNAME
    Identity          = $identity
    GameFolder        = $GameFolder
    GameName          = $gameName
    Destination       = $destGame
    PackageFamilyName = $pfn
    SourceFileCount   = $totalCount
    SourceBytes       = [int64]$totalBytes
    UnreadableFiles   = $unreadable
    SkippedFiles      = $unreadable.Count
    FilesCopied       = $destCount
    BytesCopied       = [int64]$destBytes
    RobocopyExit      = $rcExit
    RobocopyLog       = $rcLog
}

$summaryPath = Join-Path $destGame 'transfer-summary.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host "[SYSTEM phase] Summary: $summaryPath"

# Also drop a copy in runs/ for the sender's own records
$senderCopy = Join-Path $runsDir "sender-summary-$stamp.json"
Copy-Item -LiteralPath $summaryPath -Destination $senderCopy -Force

if ($rcExit -ge 8) { exit $rcExit }
exit 0
