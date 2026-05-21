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
[CmdletBinding(DefaultParameterSetName='User')]
param(
    [Parameter(Mandatory, ParameterSetName='User')]
    [string] $GameFolder,
    [Parameter(Mandatory, ParameterSetName='User')]
    [string] $Destination,
    [Parameter(ParameterSetName='User')]
    [switch] $Force,
    [Parameter(Mandatory, ParameterSetName='System')]
    [string] $SystemArgsFile
)

$ErrorActionPreference = 'Stop'
Get-ChildItem -Path $PSScriptRoot -Filter '*.ps1' -ErrorAction SilentlyContinue | ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
. (Join-Path $PSScriptRoot '_common.ps1')

$scriptPath = $MyInvocation.MyCommand.Path
$runsDir    = Join-Path $PSScriptRoot 'runs'
$toolsDir   = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Path $runsDir  -Force | Out-Null
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

# Resolve params from JSON manifest if we're the SYSTEM child.
if ($PSCmdlet.ParameterSetName -eq 'System') {
    $argsObj     = Read-SystemArgs -Path $SystemArgsFile
    $GameFolder  = [string]$argsObj.GameFolder
    $Destination = [string]$argsObj.Destination
    $Force       = [bool]$argsObj.Force
}

# Normalize paths (strip trailing slashes to avoid quoting issues - even
# though we no longer interpolate, keep the inputs clean).
$GameFolder  = (Resolve-Path -LiteralPath $GameFolder).Path
$gameName    = Split-Path -Path $GameFolder -Leaf
$destRoot    = $Destination.TrimEnd('\','/')
$destGame    = Join-Path $destRoot $gameName

# ---------------------------------------------------------------------------
# Phase 0: ensure elevation, then re-launch as SYSTEM
# ---------------------------------------------------------------------------
if ($PSCmdlet.ParameterSetName -eq 'User') {
    Assert-Elevated -ScriptPath $scriptPath -ScriptArgs @(
        '-GameFolder', "`"$GameFolder`"",
        '-Destination', "`"$destRoot`""
        if ($Force) { '-Force' }
    )

    Write-Host ""
    Write-Host "=== xbox-transfer-sender ===" -ForegroundColor Green
    Write-Host "  GameFolder:  $GameFolder"
    Write-Host "  Destination: $destGame"
    Write-Host ""

    $psexec = Ensure-PsExec -ToolsDir $toolsDir
    $stamp  = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $sysLog = Join-Path $runsDir "sender-system-$stamp.log"

    $systemParams = @{
        GameFolder  = $GameFolder
        Destination = $destRoot
    }
    if ($Force) { $systemParams.Force = $true }

    $code = Invoke-AsSystem -ScriptPath $scriptPath `
        -Params $systemParams `
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
        if ($summary.ReceiverProvidedFiles -and @($summary.ReceiverProvidedFiles).Count -gt 0) {
            Write-Host ("  Receiver downloads: {0} excluded executable(s)" -f @($summary.ReceiverProvidedFiles).Count) -ForegroundColor Yellow
        }
        Write-Host ("  Integrity OK:      {0}" -f $summary.IntegrityOk)
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

# ---------------------------------------------------------------------------
# Stop Gaming Services so they release exclusive locks on game executables.
# GamingServices holds .exe files open with no-share access; backup privilege
# bypasses ACL checks but cannot override a sharing lock (ERROR 5).
# We record which services were actually running so we can restore them after.
# ---------------------------------------------------------------------------
$gsServiceNames = @('GamingServicesNet', 'GamingServices')
$stoppedServices = @()
Write-Host "[SYSTEM phase] Stopping Gaming Services to release file locks..."
foreach ($svcName in $gsServiceNames) {
    $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq 'Running') {
        try {
            Stop-Service -Name $svcName -Force -ErrorAction Stop
            $stoppedServices += $svcName
            Write-Host ("[SYSTEM phase]   Stopped: {0}" -f $svcName)
        } catch {
            Write-Host ("[SYSTEM phase]   WARNING: could not stop {0}: {1}" -f $svcName, $_) -ForegroundColor Yellow
        }
    } else {
        Write-Host ("[SYSTEM phase]   Already stopped: {0}" -f $svcName) -ForegroundColor DarkGray
    }
}
Start-Sleep -Seconds 2

# ---------------------------------------------------------------------------
# NOTE: MSIXVC-protected executables cannot be read in plaintext by any
# process other than the licensed game. clipsp is not a Filter Manager
# minifilter (fltmc detach/unload return FLT_FILTER_NOT_FOUND), and
# disabling any decrypting filter would only yield ciphertext. Instead we
# detect unreadable executables during the source scan below, exclude them
# from the stage, and let the receiver's own Gaming Services download them
# during Install.
# ---------------------------------------------------------------------------

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
# Exclude executables the sender cannot read - the receiver downloads them.
if ($unreadable.Count -gt 0) {
    $rcArgs += '/XF'
    foreach ($u in $unreadable) { $rcArgs += "`"$u`"" }
}
Write-Host "[SYSTEM phase] robocopy starting..."
$proc = Start-Process -FilePath 'robocopy.exe' -ArgumentList $rcArgs -NoNewWindow -PassThru -Wait
$rcExit = $proc.ExitCode
Write-Host "[SYSTEM phase] robocopy exit: $rcExit (0/1/2/3 = success)"

# ---------------------------------------------------------------------------
# Remove stale copies of excluded (receiver-provided) files. A previous run
# may have staged these executables as encrypted garbage; since the overlay
# on the receiver runs without /MIR, leaving them here would clobber the
# receiver's freshly downloaded, valid copies.
# ---------------------------------------------------------------------------
$staleRemoved = @()
foreach ($u in $unreadable) {
    $rel  = $u.Substring($GameFolder.Length).TrimStart('\','/')
    $dest = Join-Path $destGame $rel
    if (Test-Path -LiteralPath $dest) {
        try {
            Remove-Item -LiteralPath $dest -Force -ErrorAction Stop
            $staleRemoved += $rel
        } catch {
            Write-Host ("[SYSTEM phase]   WARNING: could not remove stale {0}: {1}" -f $rel, $_) -ForegroundColor Yellow
        }
    }
}
if ($staleRemoved.Count -gt 0) {
    Write-Host ("[SYSTEM phase] Removed {0} stale excluded file(s) from the stage." -f $staleRemoved.Count) -ForegroundColor Yellow
}

if ($unreadable.Count -gt 0) {
    Write-Host ""
    Write-Host ("[SYSTEM phase] {0} protected executable(s) could not be read on this PC and" -f $unreadable.Count) -ForegroundColor Yellow
    Write-Host "[SYSTEM phase] were EXCLUDED from the stage. The receiver's Xbox app must" -ForegroundColor Yellow
    Write-Host "[SYSTEM phase] download these during Install before pausing:" -ForegroundColor Yellow
    foreach ($u in $unreadable) {
        Write-Host ("[SYSTEM phase]   - {0}" -f $u.Substring($GameFolder.Length).TrimStart('\','/')) -ForegroundColor DarkYellow
    }
}

# Re-scan destination for verification
$destFiles   = @(Get-ChildItem -LiteralPath $destGame -Recurse -File -Force -ErrorAction SilentlyContinue)
$destBytes   = ($destFiles | Measure-Object -Sum Length).Sum
$destCount   = $destFiles.Count

# ---------------------------------------------------------------------------
# Integrity check: compare every source file against the destination
# Detects files that robocopy silently failed to copy (e.g. SYSAPPID-locked
# executables) which would otherwise produce a corrupt staged copy.
# ---------------------------------------------------------------------------
Write-Host "[SYSTEM phase] Verifying staged copy integrity..."
# Files deliberately excluded from the stage (sender could not read them);
# their absence at the destination is expected, not an integrity failure.
$excludedRel = @{}
foreach ($u in $unreadable) {
    $excludedRel[$u.Substring($GameFolder.Length).TrimStart('\','/')] = $true
}
$missingFiles  = @()
$mismatchFiles = @()
foreach ($f in $allFiles) {
    $rel  = $f.FullName.Substring($GameFolder.Length).TrimStart('\','/')
    if ($excludedRel.ContainsKey($rel)) { continue }
    $dest = Join-Path $destGame $rel
    if (-not (Test-Path -LiteralPath $dest)) {
        $missingFiles += $rel
    } else {
        $dInfo = Get-Item -LiteralPath $dest -Force
        if ($dInfo.Length -ne $f.Length) {
            $mismatchFiles += [pscustomobject]@{
                Path       = $rel
                SourceSize = $f.Length
                DestSize   = $dInfo.Length
            }
        }
    }
}
Write-Host ("[SYSTEM phase] Integrity: {0} missing, {1} size-mismatch, robocopy exit {2}" -f $missingFiles.Count, $mismatchFiles.Count, $rcExit)

$integrityOk = ($missingFiles.Count -eq 0 -and $mismatchFiles.Count -eq 0 -and $rcExit -lt 8)

if (-not $integrityOk) {
    Write-Host ""
    Write-Host "==============================================================" -ForegroundColor Red
    Write-Host " STAGED COPY IS INCOMPLETE - DO NOT USE FOR OVERLAY!" -ForegroundColor Red
    Write-Host "==============================================================" -ForegroundColor Red
    if ($missingFiles.Count -gt 0) {
        Write-Host ""
        Write-Host "Files MISSING from staged copy ($($missingFiles.Count)):" -ForegroundColor Red
        $missingFiles | Select-Object -First 20 | ForEach-Object {
            Write-Host "  - $_" -ForegroundColor Yellow
        }
        if ($missingFiles.Count -gt 20) {
            Write-Host "  ... and $($missingFiles.Count - 20) more" -ForegroundColor Yellow
        }
    }
    if ($mismatchFiles.Count -gt 0) {
        Write-Host ""
        Write-Host "Files with SIZE MISMATCH ($($mismatchFiles.Count)):" -ForegroundColor Red
        $mismatchFiles | Select-Object -First 20 | ForEach-Object {
            Write-Host ("  - {0}  (src={1:N0}  dst={2:N0})" -f $_.Path, $_.SourceSize, $_.DestSize) -ForegroundColor Yellow
        }
    }
    Write-Host ""
    Write-Host "ROOT CAUSE: These files are likely locked by the Xbox app, the" -ForegroundColor Yellow
    Write-Host "running game process, or have SYSAPPID-conditional ACLs that even" -ForegroundColor Yellow
    Write-Host "SYSTEM cannot read while another process holds them open." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "REMEDIATION (on the sender PC):" -ForegroundColor Cyan
    Write-Host "  1. Make sure the game is NOT running." -ForegroundColor Cyan
    Write-Host "  2. Close the Xbox app completely." -ForegroundColor Cyan
    Write-Host "  3. Delete the incomplete staged folder:" -ForegroundColor Cyan
    Write-Host ("       Remove-Item -Recurse -Force `"{0}`"" -f $destGame) -ForegroundColor DarkCyan
    Write-Host "  4. Re-run xbox-transfer-sender.ps1 with the same arguments." -ForegroundColor Cyan
    Write-Host "     (Gaming Services will be stopped automatically.)" -ForegroundColor DarkCyan
    Write-Host ""
    Write-Host "transfer-summary.json will be written with IntegrityOk=false so" -ForegroundColor Yellow
    Write-Host "the receiver script can refuse this stage." -ForegroundColor Yellow
    Write-Host "==============================================================" -ForegroundColor Red
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Restore Gaming Services that were stopped before the copy.
# ---------------------------------------------------------------------------
if ($stoppedServices.Count -gt 0) {
    Write-Host "[SYSTEM phase] Restoring Gaming Services..."
    foreach ($svcName in $stoppedServices) {
        try {
            Start-Service -Name $svcName -ErrorAction Stop
            Write-Host ("[SYSTEM phase]   Started: {0}" -f $svcName)
        } catch {
            Write-Host ("[SYSTEM phase]   WARNING: could not restart {0}: {1}" -f $svcName, $_) -ForegroundColor Yellow
        }
    }
}

# Receiver-provided files: executables excluded from the stage that the
# receiver's Gaming Services must download itself. Includes the expected
# size so the receiver can verify the download finished.
$receiverProvided = @()
foreach ($u in $unreadable) {
    $fi  = $allFiles | Where-Object { $_.FullName -eq $u } | Select-Object -First 1
    $rel = $u.Substring($GameFolder.Length).TrimStart('\','/')
    $receiverProvided += [pscustomobject]@{
        Path = $rel
        Size = if ($fi) { [int64]$fi.Length } else { [int64]0 }
    }
}

$summary = [ordered]@{
    StartedAtUtc          = (Get-Date).ToUniversalTime().ToString('o')
    SenderHost            = $env:COMPUTERNAME
    Identity              = $identity
    GameFolder            = $GameFolder
    GameName              = $gameName
    Destination           = $destGame
    PackageFamilyName     = $pfn
    SourceFileCount       = $totalCount
    SourceBytes           = [int64]$totalBytes
    UnreadableFiles       = $unreadable
    SkippedFiles          = $unreadable.Count
    ReceiverProvidedFiles = $receiverProvided
    StaleRemovedFiles     = $staleRemoved
    FilesCopied           = $destCount
    BytesCopied           = [int64]$destBytes
    RobocopyExit          = $rcExit
    RobocopyLog           = $rcLog
    IntegrityOk           = $integrityOk
    MissingFiles          = $missingFiles
    MismatchFiles         = $mismatchFiles
}

$summaryPath = Join-Path $destGame 'transfer-summary.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Host "[SYSTEM phase] Summary: $summaryPath"

# Also drop a copy in runs/ for the sender's own records
$senderCopy = Join-Path $runsDir "sender-summary-$stamp.json"
Copy-Item -LiteralPath $summaryPath -Destination $senderCopy -Force

if ($rcExit -ge 8) { exit $rcExit }
if (-not $integrityOk) {
    if ($Force) {
        Write-Host ""
        Write-Host "WARNING: --Force specified - proceeding despite incomplete integrity check" -ForegroundColor Yellow
        Write-Host "The staged copy may be corrupt. Use at your own risk." -ForegroundColor Yellow
        Write-Host ""
    } else {
        exit 10
    }
}
exit 0
