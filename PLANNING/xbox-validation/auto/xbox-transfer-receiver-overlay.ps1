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
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $XboxRoot       = 'C:\XboxGames',
    [int]    $ObserveSeconds = 300,
    [switch] $InternalSystemPhase
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$scriptPath = $MyInvocation.MyCommand.Path
$runsDir    = Join-Path $PSScriptRoot 'runs'
$toolsDir   = Join-Path $PSScriptRoot 'tools'
New-Item -ItemType Directory -Path $runsDir  -Force | Out-Null
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

$Source = (Resolve-Path -LiteralPath $Source).Path

if (-not $InternalSystemPhase) {
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

    $code = Invoke-AsSystem -ScriptPath $scriptPath `
        -ScriptArgs @(
            '-Source', "`"$Source`"",
            '-XboxRoot', "`"$XboxRoot`"",
            '-ObserveSeconds', "$ObserveSeconds",
            '-InternalSystemPhase'
        ) `
        -LogPath $sysLog `
        -PsExecPath $psexec

    Write-Host ""
    Write-Host "SYSTEM phase exited with code $code" -ForegroundColor Cyan

    $verdictGlob = Join-Path $runsDir 'receiver-overlay-verdict-*.json'
    $latest = Get-ChildItem -Path $verdictGlob -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        $v = Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
        Write-Host ""
        Write-Host "=== VERDICT ===" -ForegroundColor Green
        Write-Host ("  Hypothesis:        {0}" -f $v.Hypothesis) -ForegroundColor Yellow
        Write-Host ("  Before overlay:    Installed={0}  Status={1}" -f $v.PreOverlayState.Installed, $v.PreOverlayState.Status)
        Write-Host ("  After overlay:     Installed={0}  Status={1}" -f $v.PostOverlayState.Installed, $v.PostOverlayState.Status)
        Write-Host ("  Final state:       Installed={0}  Status={1}" -f $v.FinalState.Installed, $v.FinalState.Status)
        Write-Host ("  NIC rx during obs: {0:N1} MB" -f $v.ObservedReceivedMB)
        Write-Host ("  Source bytes:      {0:N1} MB" -f ($v.SourceBytes/1MB))
        Write-Host ("  Verdict file:      {0}" -f $latest.FullName)
    } else {
        Write-Host "No verdict file produced - check SYSTEM log: $sysLog" -ForegroundColor Red
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
$destGame = Join-Path $XboxRoot $gameName

Write-Host "[SYSTEM phase] GameName: $gameName"
Write-Host "[SYSTEM phase] PFN:      $pfn"
Write-Host ("[SYSTEM phase] Source:   {0}  ({1:N1} MB / {2} files)" -f $Source, ($srcBytes/1MB), $summary.SourceFileCount)
Write-Host "[SYSTEM phase] Deploy:   $destGame"

# Snapshot state BEFORE overlay
$preState = Get-XboxPackageState -PackageFamilyName $pfn
Write-Host ("[SYSTEM phase] Pre-overlay: Installed={0}  Status={1}" -f $preState.Installed, $preState.Status)
if ($preState.InstallLocation) {
    Write-Host ("[SYSTEM phase]              InstallLocation={0}" -f $preState.InstallLocation)
}

$preDestFiles = @()
$preDestBytes = 0
if (Test-Path -LiteralPath $destGame) {
    $preDestFiles = @(Get-ChildItem -LiteralPath $destGame -Recurse -File -Force -ErrorAction SilentlyContinue)
    $preDestBytes = ($preDestFiles | Measure-Object -Sum Length).Sum
}
Write-Host ("[SYSTEM phase] On disk pre-overlay: {0} files, {1:N1} MB" -f $preDestFiles.Count, ($preDestBytes/1MB))

if ($preDestFiles.Count -eq 0) {
    Write-Host ""
    Write-Host "WARNING: destination is empty. Gaming Services may not have started" -ForegroundColor Yellow
    Write-Host "the download yet. The overlay will create the folder but the Xbox" -ForegroundColor Yellow
    Write-Host "app likely has no StateRepository row to attach to." -ForegroundColor Yellow
    Write-Host ""
}

# Overlay - critical flags explained:
#   /E        recurse, include empty dirs
#   /COPY:DAT data, attributes, timestamps  (NOT ACLs - we want destination
#             to keep the ACLs Gaming Services set up, not PC A's SIDs)
#   /IS /IT   include same/tweaked - force overwrite partial files
#   /R:1 /W:2 minimal retry
#   NO /MIR   - preserve any state files Gaming Services may have placed
#   /XF       exclude our own metadata file from the overlay
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
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
