<#
.SYNOPSIS
    Receiver variant: overlay pre-staged files onto an Xbox-initiated,
    paused install, then measure the cost of Resume.

.DESCRIPTION
    The first receiver script proved that just dropping files into
    C:\XboxGames\<Game>\ is invisible to the Xbox app because the package
    is not in the Microsoft.GamingServices StateRepository.

    This variant uses Gaming Services itself to create the StateRepository
    row. The user starts the install in the Xbox app and pauses it
    immediately; this script then overlays our complete, pre-staged bytes
    over the partial download, after which the user clicks Resume. If
    Resume completes with little or no network traffic, we have evidence
    that overlay-on-paused-install is a viable transfer strategy.

    Manual steps (the only two clicks needed):
      1. In the Xbox app on PC B, click Install on the title.
      2. Wait ~10 seconds, then click Pause. Leave the Xbox app open.
      3. Run this script. It will prompt for Enter to confirm step 2 is done.
      4. When the script tells you, click Resume in the Xbox app.

.PARAMETER Source
    Path to the staged copy produced by xbox-transfer-sender.ps1. Must
    contain transfer-summary.json.

.PARAMETER ObserveSeconds
    How long to watch the NIC after you click Resume. Default 300 (5 min).

.EXAMPLE
    .\xbox-transfer-receiver-overlay.ps1 -Source "E:\stage\Stardew Valley"
#>
[CmdletBinding(DefaultParameterSetName='User')]
param(
    [Parameter(Mandatory, ParameterSetName='User')]
    [string] $Source,
    [Parameter(ParameterSetName='User')]
    [string] $XboxRoot       = 'C:\XboxGames',
    [Parameter(ParameterSetName='User')]
    [int]    $ObserveSeconds = 300,
    [Parameter(ParameterSetName='User')]
    [switch] $Force,
    [Parameter(ParameterSetName='User')]
    [switch] $AutoConfirm,
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

# If we're the SYSTEM child, load params from the JSON manifest and
# fall through to the SYSTEM phase.
if ($PSCmdlet.ParameterSetName -eq 'System') {
    $argsObj        = Read-SystemArgs -Path $SystemArgsFile
    $Source         = ([string]$argsObj.Source).TrimEnd('\','/')
    $XboxRoot       = ([string]$argsObj.XboxRoot).TrimEnd('\','/')
    $ObserveSeconds = [int]$argsObj.ObserveSeconds
    $verdictStamp   = [string]$argsObj.VerdictStamp
    $Force          = [bool]$argsObj.Force
} else {
    # Sanitise inputs in the user/parent branch.
    $Source   = (Resolve-Path -LiteralPath $Source).Path
    $XboxRoot = $XboxRoot.TrimEnd('\','/')

    $scriptArgs = @(
        '-Source', "`"$Source`"",
        '-XboxRoot', "`"$XboxRoot`"",
        '-ObserveSeconds', "$ObserveSeconds"
    )
    if ($Force) { $scriptArgs += '-Force' }

    Assert-Elevated -ScriptPath $scriptPath -ScriptArgs $scriptArgs

    # Machine-readable status line for the C# host (Write-Host is invisible
    # when stdout is redirected; [Console]::Out goes through the pipe).
    [Console]::Out.WriteLine("[STATUS] Validating staged source..."); [Console]::Out.Flush()

    Write-Host ""
    Write-Host "=== xbox-transfer-receiver-overlay ===" -ForegroundColor Green
    Write-Host "  Source:         $Source"
    Write-Host "  XboxRoot:       $XboxRoot"
    Write-Host "  ObserveSeconds: $ObserveSeconds"
    Write-Host ""

    $summaryPath = Join-Path $Source 'transfer-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        throw "transfer-summary.json not found in $Source"
    }
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $gameName = $summary.GameName
    $pfn      = $summary.PackageFamilyName

    # Refuse to proceed if the sender flagged this stage as incomplete.
    # IntegrityOk is missing on stages produced before the integrity check
    # was added; in that case fall back to the legacy SkippedFiles count.
    $hasIntegrityField = $summary.PSObject.Properties.Name -contains 'IntegrityOk'
    $integrityBad      = $false
    if ($hasIntegrityField) {
        $integrityBad = -not [bool]$summary.IntegrityOk
    } elseif ($summary.SkippedFiles -gt 0) {
        $integrityBad = $true
    }
    if ($integrityBad) {
        Write-Host ""
        Write-Host "==============================================================" -ForegroundColor Red
        Write-Host " STAGED COPY IS INCOMPLETE" -ForegroundColor Red
        Write-Host "==============================================================" -ForegroundColor Red
        Write-Host ""
        Write-Host "transfer-summary.json reports the staged copy is missing or" -ForegroundColor Yellow
        Write-Host "has mismatched files. Running the overlay anyway would leave" -ForegroundColor Yellow
        Write-Host "the receiver's install in a corrupt state and trigger a full" -ForegroundColor Yellow
        Write-Host "re-download via the 'Repair' flow." -ForegroundColor Yellow
        Write-Host ""
        if ($summary.MissingFiles -and $summary.MissingFiles.Count -gt 0) {
            Write-Host "Missing files ($($summary.MissingFiles.Count)):" -ForegroundColor Red
            $summary.MissingFiles | Select-Object -First 10 | ForEach-Object {
                Write-Host "  - $_" -ForegroundColor Yellow
            }
            if ($summary.MissingFiles.Count -gt 10) {
                Write-Host "  ... and $($summary.MissingFiles.Count - 10) more" -ForegroundColor Yellow
            }
            Write-Host ""
        }
        if ($summary.UnreadableFiles -and $summary.UnreadableFiles.Count -gt 0) {
            Write-Host "Unreadable on sender ($($summary.UnreadableFiles.Count)):" -ForegroundColor Red
            $summary.UnreadableFiles | Select-Object -First 10 | ForEach-Object {
                Write-Host "  - $_" -ForegroundColor Yellow
            }
            Write-Host ""
        }
        if ($Force) {
            Write-Host "--Force specified - proceeding anyway. Use at your own risk." -ForegroundColor Yellow
            Write-Host ""
        } else {
            Write-Host "Re-run xbox-transfer-sender.ps1 on the sender PC after:" -ForegroundColor Cyan
            Write-Host "  1. Closing the Xbox app completely." -ForegroundColor Cyan
            Write-Host "  2. Making sure the game is not running." -ForegroundColor Cyan
            Write-Host "  3. (Optional) Stop-Service GamingServices,GamingServicesNet -Force" -ForegroundColor Cyan
            Write-Host ""
            Write-Host "Or use -Force to proceed anyway (may result in corrupt install)." -ForegroundColor Yellow
            Write-Host ""
            exit 11
        }
    }

    if (-not $AutoConfirm) {
        [Console]::Out.WriteLine("[STATUS] Source validated. Waiting for user confirmation..."); [Console]::Out.Flush()
        Write-Host "PREREQUISITES" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  1. In the Xbox app, find '$gameName' and click Install."
        Write-Host "  2. Wait ~10 seconds for the download to start, then click Pause."
        Write-Host "  3. Leave the Xbox app open (do not close it)."
        Write-Host ""
        Write-Host "Press Enter when the install is paused..." -ForegroundColor Cyan
        [void](Read-Host)
    } else {
        Write-Host "AutoConfirm: skipping pause prompt (confirmed via UI)." -ForegroundColor Cyan
    }

    [Console]::Out.WriteLine("[STATUS] Preparing PsExec..."); [Console]::Out.Flush()
    $psexec = Ensure-PsExec -ToolsDir $toolsDir
    $stamp  = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $sysLog = Join-Path $runsDir "receiver-overlay-system-$stamp.log"
    $verdictPath = Join-Path $runsDir "receiver-overlay-verdict-$stamp.json"

    $systemParams = @{
        Source         = $Source
        XboxRoot       = $XboxRoot
        ObserveSeconds = $ObserveSeconds
        VerdictStamp   = $stamp
    }
    if ($Force) { $systemParams.Force = $true }

    [Console]::Out.WriteLine("[STATUS] Launching SYSTEM child process..."); [Console]::Out.Flush()
    $code = Invoke-AsSystem -ScriptPath $scriptPath `
        -Params $systemParams `
        -LogPath $sysLog `
        -PsExecPath $psexec

    Write-Host ""
    Write-Host "SYSTEM phase exited with code $code" -ForegroundColor Cyan

    if (Test-Path -LiteralPath $verdictPath) {
        $v = Get-Content -LiteralPath $verdictPath -Raw | ConvertFrom-Json
        Write-Host ""
        Write-Host "=== VERDICT ===" -ForegroundColor Green
        Write-Host ("  Hypothesis:        {0}" -f $v.Hypothesis) -ForegroundColor Yellow
        Write-Host ("  Before overlay:    Installed={0}  Status={1}" -f $v.PreOverlayState.Installed, $v.PreOverlayState.Status)
        Write-Host ("  After overlay:     Installed={0}  Status={1}" -f $v.PostOverlayState.Installed, $v.PostOverlayState.Status)
        Write-Host ("  Final state:       Installed={0}  Status={1}" -f $v.FinalState.Installed, $v.FinalState.Status)
        Write-Host ("  NIC rx during obs: {0:N1} MB" -f $v.ObservedReceivedMB)
        Write-Host ("  Source bytes:      {0:N1} MB" -f ($v.SourceBytes/1MB))
        Write-Host ("  Verdict file:      {0}" -f $verdictPath)
    } else {
        Write-Host ""
        Write-Host "No verdict file produced for this run." -ForegroundColor Red
        Write-Host "  Expected: $verdictPath" -ForegroundColor Red
        Write-Host "  SYSTEM log: $sysLog" -ForegroundColor Red
        if (Test-Path "$sysLog.err") {
            $errSize = (Get-Item "$sysLog.err").Length
            if ($errSize -gt 0) {
                Write-Host "  Stderr ($errSize bytes):" -ForegroundColor Red
                Get-Content -LiteralPath "$sysLog.err" -Tail 20 | ForEach-Object {
                    Write-Host "    $_" -ForegroundColor DarkRed
                }
            }
        }
    }
    exit $code
}

# ---------------------------------------------------------------------------
# SYSTEM phase
# ---------------------------------------------------------------------------
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Host "[SYSTEM phase] Identity: $identity"

$summary  = Get-Content -LiteralPath (Join-Path $Source 'transfer-summary.json') -Raw | ConvertFrom-Json
$gameName = $summary.GameName
$pfn      = $summary.PackageFamilyName
$srcBytes = [int64]$summary.SourceBytes

# Sniff the content GUID from a .xvi file in our source, e.g.:
#   807C7D6A-409F-48BE-8190-30B09BAF7CD4.xvi
# Gaming Services names the in-progress download folder after this GUID,
# only renaming it to the friendly title after install completes.
$contentGuid = $null
$xviFile = Get-ChildItem -LiteralPath $Source -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.xvi' } | Select-Object -First 1
if ($xviFile) {
    $contentGuid = $xviFile.BaseName  # e.g. "807C7D6A-409F-48BE-8190-30B09BAF7CD4"
}

Write-Host "[SYSTEM phase] GameName:     $gameName"
Write-Host "[SYSTEM phase] PFN:          $pfn"
Write-Host "[SYSTEM phase] Content GUID: $contentGuid"
Write-Host ("[SYSTEM phase] Source:       {0}  ({1:N1} MB / {2} files)" -f $Source, ($srcBytes/1MB), $summary.SourceFileCount)

# Locate the actual destination folder. Gaming Services may have used:
#   1. <XboxRoot>\<gameName>           (post-rename / older flow)
#   2. <XboxRoot>\<contentGuid>        (in-progress download, before rename)
# Search all drives' XboxGames\ for either name.
function Find-Destination {
    param([string]$GameName, [string]$ContentGuid)
    $candidates = @()
    foreach ($d in (Get-PSDrive -PSProvider FileSystem)) {
        $xg = Join-Path $d.Root 'XboxGames'
        if (-not (Test-Path -LiteralPath $xg)) { continue }
        $byName = Join-Path $xg $GameName
        if (Test-Path -LiteralPath $byName) { $candidates += $byName }
        if ($ContentGuid) {
            $byGuid = Join-Path $xg $ContentGuid
            if (Test-Path -LiteralPath $byGuid) { $candidates += $byGuid }
        }
    }
    return $candidates
}

$initialCandidates = @(Find-Destination -GameName $gameName -ContentGuid $contentGuid)

# Also check the user-provided XboxRoot directly (e.g., F:\Games instead of F:\XboxGames)
if (Test-Path -LiteralPath $XboxRoot) {
    $byName = Join-Path $XboxRoot $gameName
    if (Test-Path -LiteralPath $byName) { $initialCandidates += $byName }
    if ($contentGuid) {
        $byGuid = Join-Path $XboxRoot $contentGuid
        if (Test-Path -LiteralPath $byGuid) { $initialCandidates += $byGuid }
    }
}

Write-Host ("[SYSTEM phase] Candidates pre-poll: {0}" -f ($initialCandidates -join '; '))

# Default deploy path; prefer whichever candidate already has files on disk
# (the GUID folder is filled first; the friendly-name folder may be empty)
$destGame = Join-Path $XboxRoot $gameName
if ($initialCandidates.Count -gt 0) {
    $best = $initialCandidates | ForEach-Object {
        $n = @(Get-ChildItem -LiteralPath $_ -Recurse -File -Force -ErrorAction SilentlyContinue).Count
        [pscustomobject]@{ Path = $_; Files = $n }
    } | Sort-Object Files -Descending | Select-Object -First 1
    $destGame = if ($best.Files -gt 0) { $best.Path } else { $initialCandidates[0] }
}
Write-Host "[SYSTEM phase] Deploy:       $destGame"

# Snapshot state BEFORE overlay
$preState = Get-XboxPackageState -PackageFamilyName $pfn
Write-Host ("[SYSTEM phase] Pre-overlay: Installed={0}  Status={1}" -f $preState.Installed, $preState.Status)
if ($preState.InstallLocation) {
    Write-Host ("[SYSTEM phase]              InstallLocation={0}" -f $preState.InstallLocation)
}

function Get-DestStats {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @{ Files = 0; Bytes = 0 } }
    $f = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
    return @{
        Files = $f.Count
        Bytes = ($f | Measure-Object -Sum Length).Sum
    }
}

# Poll for file materialization - Gaming Services often takes 30-60s
# after pressing Install before any bytes hit disk. Re-search for
# candidate folders each iteration so we catch the GUID folder even if
# it appears late.
Write-Host "[SYSTEM phase] Polling for in-progress install (up to 90s)..."
$pollStart   = Get-Date
$pollTimeout = New-TimeSpan -Seconds 90
$stats = Get-DestStats -Path $destGame
while ($stats.Files -eq 0 -and ((Get-Date) - $pollStart) -lt $pollTimeout) {
    Start-Sleep -Seconds 3
    # Re-search every poll - check ALL candidates and switch to whichever has files.
    $cands = @(Find-Destination -GameName $gameName -ContentGuid $contentGuid) + @($destGame) |
        Select-Object -Unique
    $bestCand = $cands | ForEach-Object {
        $s = Get-DestStats -Path $_
        [pscustomobject]@{ Path = $_; Files = $s.Files; Bytes = $s.Bytes }
    } | Sort-Object Files -Descending | Select-Object -First 1
    if ($bestCand.Files -gt 0 -and $bestCand.Path -ne $destGame) {
        Write-Host ("[SYSTEM phase]   switching to active candidate: {0}" -f $bestCand.Path) -ForegroundColor Cyan
        $destGame = $bestCand.Path
    }
    $stats = Get-DestStats -Path $destGame
    $elapsed = [int]((Get-Date) - $pollStart).TotalSeconds
    Write-Host ("[SYSTEM phase]   t+{0,2}s  dest={1}  files={2}  bytes={3:N0}" -f $elapsed, $destGame, $stats.Files, $stats.Bytes)
}
$preDestFiles = @()
$preDestBytes = $stats.Bytes
if (Test-Path -LiteralPath $destGame) {
    $preDestFiles = @(Get-ChildItem -LiteralPath $destGame -Recurse -File -Force -ErrorAction SilentlyContinue)
}
Write-Host ""
Write-Host ("[SYSTEM phase] Final destination:    {0}" -f $destGame) -ForegroundColor Green
Write-Host ("[SYSTEM phase] On disk pre-overlay:  {0} files, {1:N1} MB" -f $preDestFiles.Count, ($preDestBytes/1MB))
if ($preDestFiles.Count -gt 0 -and $preDestFiles.Count -le 30) {
    Write-Host "[SYSTEM phase] Pre-overlay contents:"
    $preDestFiles | Sort-Object Length -Descending | ForEach-Object {
        $rel = $_.FullName.Substring($destGame.Length).TrimStart('\','/')
        Write-Host ("    {0,12:N0}  {1}" -f $_.Length, $rel) -ForegroundColor DarkGray
    }
}

if ($preDestFiles.Count -eq 0) {
    # Broad scan: look for any XboxGames\* folder modified in the last 15 min
    Write-Host ""
    Write-Host "[SYSTEM phase] Scanning all drives for recent XboxGames installs..." -ForegroundColor Yellow
    $cutoff = (Get-Date).AddMinutes(-15)
    $hits = @()
    foreach ($d in (Get-PSDrive -PSProvider FileSystem)) {
        $xg = Join-Path $d.Root 'XboxGames'
        if (-not (Test-Path -LiteralPath $xg)) { continue }
        Get-ChildItem -LiteralPath $xg -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
            $folder = $_.FullName
            $lastWrite = $_.LastWriteTime
            $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Force -ErrorAction SilentlyContinue)
            $recentFile = $files | Where-Object { $_.LastWriteTime -gt $cutoff } | Select-Object -First 1
            $size = ($files | Measure-Object -Sum Length).Sum
            if ($lastWrite -gt $cutoff -or $recentFile) {
                $hits += [pscustomobject]@{
                    Path        = $folder
                    LastWrite   = $lastWrite
                    Files       = $files.Count
                    BytesGB     = [math]::Round($size/1GB, 2)
                    HasRecentFile = [bool]$recentFile
                }
            }
        }
    }
    if ($hits.Count -gt 0) {
        Write-Host "[SYSTEM phase] Found these recent XboxGames folders:" -ForegroundColor Yellow
        $hits | ForEach-Object {
            Write-Host ("    {0}    LastWrite={1:HH:mm:ss}    Files={2}    {3:N2} GB    Recent={4}" -f `
                $_.Path, $_.LastWrite, $_.Files, $_.BytesGB, $_.HasRecentFile) -ForegroundColor Yellow
        }
        Write-Host ""
        Write-Host "    If one of those is your Silksong install, re-run with:" -ForegroundColor Yellow
        Write-Host ("    -XboxRoot '<that drive>:\XboxGames'  (and possibly --GameName matching the actual folder name)") -ForegroundColor Yellow
    } else {
        Write-Host "[SYSTEM phase] No recent XboxGames\* folders found on any drive." -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "ABORTING: $destGame is empty after pause." -ForegroundColor Red
    Write-Host "" -ForegroundColor Red
    Write-Host "This means Gaming Services is NOT installing this title under" -ForegroundColor Red
    Write-Host "$XboxRoot. Two likely reasons:" -ForegroundColor Red
    Write-Host "  1. The title is plain MSIX (not MSIXVC-encrypted), so it installs" -ForegroundColor Red
    Write-Host "     into C:\Program Files\WindowsApps\ - the overlay strategy here" -ForegroundColor Red
    Write-Host "     does not apply. Pick an MSIXVC title (one whose 'Manage > Files'" -ForegroundColor Red
    Write-Host "     menu in the Xbox app shows an 'Install drive' picker)." -ForegroundColor Red
    Write-Host "     Suggestions: Pentiment (~7 GB), Hi-Fi Rush (~12 GB), Grounded." -ForegroundColor Red
    Write-Host "  2. You picked a different drive when installing. Re-run with" -ForegroundColor Red
    Write-Host "     -XboxRoot pointing at <Drive>:\XboxGames\." -ForegroundColor Red
    Write-Host ""
    # Probe other drives for an XboxGames\<gameName> folder so we can hint
    foreach ($d in (Get-PSDrive -PSProvider FileSystem)) {
        $candidate = Join-Path "$($d.Root)XboxGames" $gameName
        if (Test-Path -LiteralPath $candidate) {
            $sz = (Get-ChildItem -LiteralPath $candidate -Recurse -File -Force -ErrorAction SilentlyContinue |
                   Measure-Object -Sum Length).Sum
            Write-Host ("    Found candidate at: {0}  ({1:N1} MB)" -f $candidate, ($sz/1MB)) -ForegroundColor Yellow
        }
    }
    # And check WindowsApps to confirm it's a plain MSIX
    $waCandidate = Get-ChildItem 'C:\Program Files\WindowsApps' -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*$($pfn.Split('_')[0])*" } |
        Select-Object -First 3
    if ($waCandidate) {
        Write-Host "    The package appears to be plain MSIX:" -ForegroundColor Yellow
        $waCandidate | ForEach-Object { Write-Host ("      {0}" -f $_.FullName) -ForegroundColor Yellow }
    }
    exit 2
}

# ---------------------------------------------------------------------------
# Verify receiver-provided executables BEFORE overlaying.
# The sender could not read these MSIXVC-protected EXEs, so they were
# excluded from the stage; the receiver's own Gaming Services must download
# them. This must happen during the genuine (pre-overlay) Install, because
# once we overlay the sender's .xvi - which marks every block "downloaded" -
# Resume will only finalize and will NOT fetch anything more. So if these
# EXEs are not on disk yet, abort before the overlay and let the user
# continue the real download.
# ---------------------------------------------------------------------------
$receiverProvided = @()
if ($summary.PSObject.Properties.Name -contains 'ReceiverProvidedFiles') {
    $receiverProvided = @($summary.ReceiverProvidedFiles)
}
if ($receiverProvided.Count -gt 0) {
    Write-Host ""
    Write-Host ("[SYSTEM phase] Verifying {0} receiver-provided executable(s) before overlay..." -f $receiverProvided.Count)
    $badExes = @()
    foreach ($rp in $receiverProvided) {
        $rel = [string]$rp.Path
        $exp = [int64]$rp.Size
        $p   = Join-Path $destGame $rel
        $reason = $null
        if (-not (Test-Path -LiteralPath $p)) {
            $reason = 'not downloaded yet'
        } else {
            $fi = Get-Item -LiteralPath $p -Force
            if ($exp -gt 0 -and $fi.Length -ne $exp) {
                $reason = ("size {0:N0}, expected {1:N0}" -f $fi.Length, $exp)
            } else {
                try {
                    $fs = [System.IO.File]::OpenRead($p)
                    $b0 = $fs.ReadByte(); $b1 = $fs.ReadByte()
                    $fs.Close()
                    if (-not ($b0 -eq 0x4D -and $b1 -eq 0x5A)) {
                        $reason = 'not a valid executable (no MZ header)'
                    }
                } catch {
                    $reason = "unreadable: $_"
                }
            }
        }
        if ($reason) {
            Write-Host ("[SYSTEM phase]   BAD  {0}  ({1})" -f $rel, $reason) -ForegroundColor Red
            $badExes += $rel
        } else {
            Write-Host ("[SYSTEM phase]   OK   {0}" -f $rel) -ForegroundColor Green
        }
    }
    if ($badExes.Count -gt 0) {
        Write-Host ""
        Write-Host "==============================================================" -ForegroundColor Red
        Write-Host " RECEIVER-PROVIDED EXECUTABLES ARE NOT READY" -ForegroundColor Red
        Write-Host "==============================================================" -ForegroundColor Red
        Write-Host ""
        Write-Host "The sender could not read these executables, so the receiver's" -ForegroundColor Yellow
        Write-Host "Xbox app must download them. The install was paused before they" -ForegroundColor Yellow
        Write-Host "finished downloading:" -ForegroundColor Yellow
        $badExes | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
        Write-Host ""
        Write-Host "No overlay has been applied yet, so the install is still genuine." -ForegroundColor Cyan
        Write-Host "REMEDIATION:" -ForegroundColor Cyan
        Write-Host "  1. Click Resume in the Xbox app." -ForegroundColor Cyan
        Write-Host "  2. Let the download run - watch the install size grow." -ForegroundColor Cyan
        Write-Host "  3. Click Pause again once it has progressed a few hundred MB." -ForegroundColor Cyan
        Write-Host "  4. Re-run this script. Repeat until all executables show OK." -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Use -Force to overlay anyway (the install will likely be corrupt)." -ForegroundColor Yellow
        Write-Host "==============================================================" -ForegroundColor Red
        if ($Force) {
            Write-Host "--Force specified - proceeding with overlay despite bad executables." -ForegroundColor Yellow
        } else {
            exit 12
        }
    } else {
        Write-Host "[SYSTEM phase] All receiver-provided executables present and valid." -ForegroundColor Green
    }
}

# Overlay - critical flags explained:
#   /E        recurse, include empty dirs
#   /COPY:DAT data, attributes, timestamps  (NOT ACLs - we want destination
#             to keep the ACLs Gaming Services set up, not PC A's SIDs)
#   /IS /IT   include same/tweaked - force overwrite partial files
#   /R:1 /W:2 minimal retry
#   NO /MIR   - preserve any state files Gaming Services may have placed
#   /XF       exclude our own metadata file from the overlay
#
# MSIXVC metadata files (.xvi/.xct/.xvs/.smd):
#   The source's .xvi marks all blocks as "downloaded", which tells Gaming
#   Services to finalize the install rather than re-download everything.
#   We MUST include them when game versions match.  If versions differ the
#   .xvi sizes will be different, which caused the 13 GB re-download before.
#   Check sizes first; exclude them only on a mismatch to avoid corruption.
if (-not $verdictStamp) { $verdictStamp = (Get-Date).ToString('yyyyMMdd-HHmmss') }
$stamp = $verdictStamp

$msixvcExcludes = @()
if ($contentGuid) {
    $srcXvi      = Join-Path $Source "$contentGuid.xvi"
    $destXvi     = Join-Path $destGame "$contentGuid.xvi"
    $srcXviItem  = Get-Item -LiteralPath $srcXvi  -Force -ErrorAction SilentlyContinue
    $destXviItem = Get-Item -LiteralPath $destXvi -Force -ErrorAction SilentlyContinue
    if ($srcXviItem -and $destXviItem) {
        $srcSize  = $srcXviItem.Length
        $destSize = $destXviItem.Length
        if ($srcSize -ne $destSize) {
            Write-Host ""
            Write-Host "WARNING: .xvi size mismatch - game versions differ!" -ForegroundColor Red
            Write-Host ("  Source .xvi : {0} bytes  (sender game version)" -f $srcSize) -ForegroundColor Yellow
            Write-Host ("  Dest   .xvi : {0} bytes  (receiver downloading newer version)" -f $destSize) -ForegroundColor Yellow
            Write-Host "  Excluding MSIXVC metadata from overlay to avoid block-map corruption." -ForegroundColor Yellow
            Write-Host "  ACTION: Update the sender's game to the latest version, re-stage, then retry." -ForegroundColor Cyan
            Write-Host ""
            $msixvcExcludes = @('/XF','*.xvi','/XF','*.xct','/XF','*.xvs','/XF','*.smd','/XF','*.xsp')
        } else {
            Write-Host ("[SYSTEM phase] .xvi size match ({0} bytes) - same version, including MSIXVC metadata in overlay." -f $srcSize) -ForegroundColor Green
        }
    } elseif (-not $srcXviItem) {
        Write-Host "[SYSTEM phase] WARNING: source stage has no .xvi file." -ForegroundColor Yellow
        Write-Host "  The sender's installed game folder does not contain MSIXVC metadata." -ForegroundColor Yellow
        Write-Host "  Without the source .xvi, Gaming Services will NOT recognize the overlaid" -ForegroundColor Yellow
        Write-Host "  blocks as downloaded and will continue the full CDN download." -ForegroundColor Yellow
        Write-Host "  See PLANNING docs for how to locate and add the .xvi to the stage." -ForegroundColor Cyan
    }
}

$rcLog = Join-Path $runsDir "receiver-overlay-robocopy-$stamp.log"
$rcArgs = @(
    "`"$Source`"", "`"$destGame`"", '/E','/COPY:DAT','/DCOPY:DAT',
    '/IS','/IT','/R:1','/W:2','/MT:8','/NP','/NDL','/TEE',
    "/LOG+:$rcLog",
    '/XF','transfer-summary.json'
) + $msixvcExcludes
Write-Host "[SYSTEM phase] Overlay robocopy starting..."
$proc = Start-Process -FilePath 'robocopy.exe' -ArgumentList $rcArgs -NoNewWindow -PassThru -Wait
$rcExit = $proc.ExitCode
Write-Host "[SYSTEM phase] Overlay robocopy exit: $rcExit"

# Reset ACLs so files inherit from the parent folder. Without this, the AppX
# deployment engine cannot read our overlaid files during MRTDataPopulated,
# causing 0x80070005 (ACCESS_DENIED).
Write-Host "[SYSTEM phase] Resetting ACLs (icacls /reset /T)..."
$icaclsProc = Start-Process -FilePath 'icacls.exe' -ArgumentList @("`"$destGame`"", '/reset', '/T', '/Q', '/C') -NoNewWindow -PassThru -Wait
Write-Host "[SYSTEM phase] icacls exit: $($icaclsProc.ExitCode)"

# Snapshot state AFTER overlay
$postState = Get-XboxPackageState -PackageFamilyName $pfn
$postDestFiles = @(Get-ChildItem -LiteralPath $destGame -Recurse -File -Force -ErrorAction SilentlyContinue)
$postDestBytes = ($postDestFiles | Measure-Object -Sum Length).Sum
Write-Host ("[SYSTEM phase] Post-overlay: Installed={0}  Status={1}" -f $postState.Installed, $postState.Status)
Write-Host ("[SYSTEM phase] On disk post-overlay: {0} files, {1:N1} MB" -f $postDestFiles.Count, ($postDestBytes/1MB))

# Baseline NIC and prompt user to click Resume
$baseline = Get-NicBaseline -InterfaceAlias ''
Write-Host ""
Write-Host "[SYSTEM phase] ============================================" -ForegroundColor Yellow
Write-Host "[SYSTEM phase] NOW: click Resume in the Xbox app." -ForegroundColor Yellow
Write-Host "[SYSTEM phase] ============================================" -ForegroundColor Yellow
Write-Host ("[SYSTEM phase] NIC baseline on '{0}': rx={1:N0}" -f $baseline.InterfaceAlias, $baseline.ReceivedBytes)
Write-Host ("[SYSTEM phase] Observing for {0} seconds..." -f $ObserveSeconds)

$samples = @()
$elapsed = 0
$step    = 15
while ($elapsed -lt $ObserveSeconds) {
    Start-Sleep -Seconds $step
    $elapsed += $step
    $d  = Get-NicDelta -Baseline $baseline
    $st = Get-XboxPackageState -PackageFamilyName $pfn
    $samples += [pscustomobject]@{
        ElapsedSeconds  = $elapsed
        ReceivedMB      = $d.ReceivedMB
        PackageInstalled= $st.Installed
        PackageStatus   = $st.Status
        InstallLocation = $st.InstallLocation
        CounterWrapped  = $d.CounterWrapped
    }
    Write-Host ("[SYSTEM phase]   t+{0,4}s  rx={1,8:N1} MB  installed={2,-5}  status={3}" -f `
        $elapsed, $d.ReceivedMB, $st.Installed, $st.Status)

    # Short-circuit: if installed AND traffic has been near-zero for a while, exit early
    if ($st.Installed -and $d.ReceivedMB -lt 50 -and $elapsed -ge 60) {
        Write-Host "[SYSTEM phase] Package became Installed with minimal traffic - exiting observe early."
        break
    }
}

$finalDelta = Get-NicDelta -Baseline $baseline
$finalState = Get-XboxPackageState -PackageFamilyName $pfn

$hypothesis = 'INCONCLUSIVE'
if ($finalDelta.CounterWrapped) {
    $hypothesis = 'INCONCLUSIVE'
} elseif ($finalState.Installed) {
    $rx = $finalDelta.ReceivedBytes
    if     ($rx -lt 100MB)                 { $hypothesis = 'H1_FULL_SKIP' }
    elseif ($rx -lt ($srcBytes * 0.8))     { $hypothesis = 'H2_DELTA' }
    else                                   { $hypothesis = 'H3_FULL_REDOWNLOAD' }
} else {
    if ($finalDelta.ReceivedBytes -ge ($srcBytes * 0.8)) {
        $hypothesis = 'H3_FULL_REDOWNLOAD'
    } elseif ($finalDelta.ReceivedBytes -lt 50MB) {
        $hypothesis = 'STILL_PAUSED_OR_FAILED'
    } else {
        $hypothesis = 'PARTIAL_PROGRESS'
    }
}

$verdict = [ordered]@{
    StartedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
    ReceiverHost       = $env:COMPUTERNAME
    Identity           = $identity
    Mode               = 'overlay-on-paused-install'
    Source             = $Source
    Deploy             = $destGame
    GameName           = $gameName
    PackageFamilyName  = $pfn
    SourceBytes        = $srcBytes
    SourceFileCount    = $summary.SourceFileCount
    PreOverlayState    = $preState
    PreOverlayFiles    = $preDestFiles.Count
    PreOverlayBytes    = [int64]$preDestBytes
    PostOverlayState   = $postState
    PostOverlayFiles   = $postDestFiles.Count
    PostOverlayBytes   = [int64]$postDestBytes
    RobocopyExit       = $rcExit
    Baseline           = $baseline
    ObserveSeconds     = $ObserveSeconds
    Samples            = $samples
    FinalState         = $finalState
    FinalDelta         = $finalDelta
    ObservedReceivedMB = $finalDelta.ReceivedMB
    Hypothesis         = $hypothesis
}

$verdictPath = Join-Path $runsDir "receiver-overlay-verdict-$stamp.json"
$verdict | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verdictPath -Encoding UTF8
Write-Host ""
Write-Host "[SYSTEM phase] Verdict: $hypothesis" -ForegroundColor Yellow
Write-Host "[SYSTEM phase] Written: $verdictPath"
exit 0
