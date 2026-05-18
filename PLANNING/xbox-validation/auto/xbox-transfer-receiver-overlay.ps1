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
    [Parameter(Mandatory, ParameterSetName='System')]
    [string] $SystemArgsFile
)

$ErrorActionPreference = 'Stop'
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
    $Source         = [string]$argsObj.Source
    $XboxRoot       = [string]$argsObj.XboxRoot
    $ObserveSeconds = [int]$argsObj.ObserveSeconds
    $verdictStamp   = [string]$argsObj.VerdictStamp
} else {
    # Sanitise inputs in the user/parent branch.
    $Source   = (Resolve-Path -LiteralPath $Source).Path
    $XboxRoot = $XboxRoot.TrimEnd('\','/')

    Assert-Elevated -ScriptPath $scriptPath -ScriptArgs @(
        '-Source', "`"$Source`"",
        '-XboxRoot', "`"$XboxRoot`"",
        '-ObserveSeconds', "$ObserveSeconds"
    )

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

    Write-Host "PREREQUISITES" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  1. In the Xbox app, find '$gameName' and click Install."
    Write-Host "  2. Wait ~10 seconds for the download to start, then click Pause."
    Write-Host "  3. Leave the Xbox app open (do not close it)."
    Write-Host ""
    Write-Host "Press Enter when the install is paused..." -ForegroundColor Cyan
    [void](Read-Host)

    $psexec = Ensure-PsExec -ToolsDir $toolsDir
    $stamp  = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $sysLog = Join-Path $runsDir "receiver-overlay-system-$stamp.log"
    $verdictPath = Join-Path $runsDir "receiver-overlay-verdict-$stamp.json"

    $code = Invoke-AsSystem -ScriptPath $scriptPath `
        -Params @{
            Source         = $Source
            XboxRoot       = $XboxRoot
            ObserveSeconds = $ObserveSeconds
            VerdictStamp   = $stamp
        } `
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
Write-Host ("[SYSTEM phase] Candidates pre-poll: {0}" -f ($initialCandidates -join '; '))

# Default deploy path; will be overridden if we find a GUID folder
$destGame = Join-Path $XboxRoot $gameName
if ($initialCandidates.Count -gt 0) {
    $destGame = $initialCandidates[0]
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
    # Re-search every poll - the GUID folder may appear mid-wait.
    $cands = @(Find-Destination -GameName $gameName -ContentGuid $contentGuid)
    if ($cands.Count -gt 0 -and $cands[0] -ne $destGame) {
        Write-Host ("[SYSTEM phase]   discovered candidate: {0}" -f $cands[0]) -ForegroundColor Cyan
        $destGame = $cands[0]
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

# Overlay - critical flags explained:
#   /E        recurse, include empty dirs
#   /COPY:DAT data, attributes, timestamps  (NOT ACLs - we want destination
#             to keep the ACLs Gaming Services set up, not PC A's SIDs)
#   /IS /IT   include same/tweaked - force overwrite partial files
#   /R:1 /W:2 minimal retry
#   NO /MIR   - preserve any state files Gaming Services may have placed
#   /XF       exclude our own metadata file from the overlay
if (-not $verdictStamp) { $verdictStamp = (Get-Date).ToString('yyyyMMdd-HHmmss') }
$stamp = $verdictStamp
$rcLog = Join-Path $runsDir "receiver-overlay-robocopy-$stamp.log"
$rcArgs = @(
    "`"$Source`"", "`"$destGame`"", '/E','/COPY:DAT','/DCOPY:DAT',
    '/IS','/IT','/R:1','/W:2','/MT:8','/NP','/NDL','/TEE',
    "/LOG+:$rcLog",
    '/XF','transfer-summary.json'
)
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
