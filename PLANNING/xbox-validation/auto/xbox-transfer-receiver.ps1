<#
.SYNOPSIS
    End-to-end, no-prompt receiver for the Xbox PC pre-staged transfer experiment.

.DESCRIPTION
    Run this on PC B (the destination). It will:
      1. Self-elevate to admin via UAC if not already elevated.
      2. Download PsExec on first run.
      3. Re-launch as NT AUTHORITY\SYSTEM so it can write ACL-protected files.
      4. Read transfer-summary.json from -Source to learn PackageFamilyName
         and source byte count.
      5. Robocopy -Source into C:\XboxGames\<GameName>\.
      6. Kill the Xbox app, baseline the NIC byte counter, restart the Xbox
         app, and measure traffic for -ObserveSeconds (default 180).
      7. Probe Get-AppxPackage for the PFN to see if the package became
         registered.
      8. Emit a verdict JSON in runs\ with one of:
            H1_FULL_SKIP      (PFN registered, NIC rx < 100 MB)
            H2_DELTA          (PFN registered, 100 MB <= NIC rx < 80% of source bytes)
            H3_FULL_REDOWNLOAD(NIC rx >= 80% of source bytes)
            NOT_DETECTED      (no PFN registration, no significant download)
            INCONCLUSIVE      (any other combination, e.g. NIC wrap)

.PARAMETER Source
    Path to the staged copy produced by xbox-transfer-sender.ps1. Should
    contain transfer-summary.json at its root.

.PARAMETER XboxRoot
    Where to deploy the game. Defaults to C:\XboxGames. The script appends
    the GameName from the summary.

.PARAMETER ObserveSeconds
    How long to watch the NIC after restarting the Xbox app. Default 180.

.EXAMPLE
    .\xbox-transfer-receiver.ps1 -Source "E:\stage\A Short Hike"
#>
[CmdletBinding(DefaultParameterSetName='User')]
param(
    [Parameter(Mandatory, ParameterSetName='User')]
    [string] $Source,
    [Parameter(ParameterSetName='User')]
    [string] $XboxRoot       = 'C:\XboxGames',
    [Parameter(ParameterSetName='User')]
    [int]    $ObserveSeconds = 180,
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

if ($PSCmdlet.ParameterSetName -eq 'System') {
    $argsObj        = Read-SystemArgs -Path $SystemArgsFile
    $Source         = [string]$argsObj.Source
    $XboxRoot       = [string]$argsObj.XboxRoot
    $ObserveSeconds = [int]$argsObj.ObserveSeconds
    $verdictStamp   = [string]$argsObj.VerdictStamp
} else {
    $Source   = (Resolve-Path -LiteralPath $Source).Path
    $XboxRoot = $XboxRoot.TrimEnd('\','/')
}

if ($PSCmdlet.ParameterSetName -eq 'User') {
    Assert-Elevated -ScriptPath $scriptPath -ScriptArgs @(
        '-Source', "`"$Source`"",
        '-XboxRoot', "`"$XboxRoot`"",
        '-ObserveSeconds', "$ObserveSeconds"
    )

    Write-Host ""
    Write-Host "=== xbox-transfer-receiver ===" -ForegroundColor Green
    Write-Host "  Source:         $Source"
    Write-Host "  XboxRoot:       $XboxRoot"
    Write-Host "  ObserveSeconds: $ObserveSeconds"
    Write-Host ""

    $summaryPath = Join-Path $Source 'transfer-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        throw "transfer-summary.json not found in $Source - did the sender complete?"
    }

    $psexec = Ensure-PsExec -ToolsDir $toolsDir
    $stamp  = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $sysLog = Join-Path $runsDir "receiver-system-$stamp.log"
    $verdictPath = Join-Path $runsDir "receiver-verdict-$stamp.json"

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
    Write-Host "Full log: $sysLog"

    if (Test-Path -LiteralPath $verdictPath) {
        Write-Host ""
        Write-Host "=== VERDICT ===" -ForegroundColor Green
        $v = Get-Content -LiteralPath $verdictPath -Raw | ConvertFrom-Json
        Write-Host ("  Hypothesis:        {0}" -f $v.Hypothesis) -ForegroundColor Yellow
        Write-Host ("  PFN registered:    {0}" -f $v.PackageRegistered)
        Write-Host ("  NIC rx during obs: {0:N1} MB" -f $v.ObservedReceivedMB)
        Write-Host ("  Source bytes:      {0:N1} MB" -f ($v.SourceBytes/1MB))
        Write-Host ("  Verdict file:      {0}" -f $verdictPath)
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

$summaryPath = Join-Path $Source 'transfer-summary.json'
$summary     = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$gameName    = $summary.GameName
$pfn         = $summary.PackageFamilyName
$srcBytes    = [int64]$summary.SourceBytes
$destGame    = Join-Path $XboxRoot $gameName

Write-Host "[SYSTEM phase] GameName: $gameName"
Write-Host "[SYSTEM phase] PFN:      $pfn"
Write-Host ("[SYSTEM phase] Source:   {0}  ({1:N1} MB across {2} files)" -f $Source, ($srcBytes/1MB), $summary.SourceFileCount)
Write-Host "[SYSTEM phase] Deploy:   $destGame"

# Make sure XboxRoot exists
if (-not (Test-Path -LiteralPath $XboxRoot)) {
    New-Item -ItemType Directory -Path $XboxRoot -Force | Out-Null
}

# State before deploy
$preState = Get-XboxPackageState -PackageFamilyName $pfn
Write-Host ("[SYSTEM phase] Pre-deploy package state: Installed={0}" -f $preState.Installed)

# Stop Xbox app so it doesn't fight with us during the copy
Stop-XboxApp

# Robocopy from -Source to $destGame
if (-not $verdictStamp) { $verdictStamp = (Get-Date).ToString('yyyyMMdd-HHmmss') }
$stamp = $verdictStamp
$rcLog = Join-Path $runsDir "receiver-robocopy-$stamp.log"
$rcArgs = @(
    "`"$Source`"", "`"$destGame`"", '/E','/COPYALL','/B','/DCOPY:DAT',
    '/R:1','/W:2','/MT:8','/NP','/NDL','/TEE',
    "/LOG+:$rcLog",
    '/XF', 'transfer-summary.json'
)
Write-Host "[SYSTEM phase] robocopy starting..."
$proc = Start-Process -FilePath 'robocopy.exe' -ArgumentList $rcArgs -NoNewWindow -PassThru -Wait
$rcExit = $proc.ExitCode
Write-Host "[SYSTEM phase] robocopy exit: $rcExit"

# Confirm files on disk
$destFiles = @(Get-ChildItem -LiteralPath $destGame -Recurse -File -Force -ErrorAction SilentlyContinue)
$destBytes = ($destFiles | Measure-Object -Sum Length).Sum
Write-Host ("[SYSTEM phase] On disk after deploy: {0} files, {1:N1} MB" -f $destFiles.Count, ($destBytes/1MB))

# Baseline NIC
$baseline = Get-NicBaseline -InterfaceAlias ''
Write-Host ("[SYSTEM phase] NIC baseline on '{0}': rx={1:N0} tx={2:N0}" -f `
    $baseline.InterfaceAlias, $baseline.ReceivedBytes, $baseline.SentBytes)

# Start Xbox app
Write-Host "[SYSTEM phase] Starting Xbox app..."
Start-XboxApp
Write-Host ("[SYSTEM phase] Observing for {0} seconds..." -f $ObserveSeconds)

# Sample every 15s so we get a curve, not just an endpoint
$samples = @()
$elapsed = 0
$step    = 15
while ($elapsed -lt $ObserveSeconds) {
    Start-Sleep -Seconds $step
    $elapsed += $step
    $d = Get-NicDelta -Baseline $baseline
    $st = Get-XboxPackageState -PackageFamilyName $pfn
    $samples += [pscustomobject]@{
        ElapsedSeconds  = $elapsed
        ReceivedMB      = $d.ReceivedMB
        PackageInstalled= $st.Installed
        InstallLocation = $st.InstallLocation
        CounterWrapped  = $d.CounterWrapped
    }
    Write-Host ("[SYSTEM phase]   t+{0,3}s  rx={1,8:N1} MB  registered={2}" -f $elapsed, $d.ReceivedMB, $st.Installed)
}

$finalDelta = Get-NicDelta -Baseline $baseline
$finalState = Get-XboxPackageState -PackageFamilyName $pfn

# Determine verdict
$hypothesis = 'INCONCLUSIVE'
if ($finalDelta.CounterWrapped) {
    $hypothesis = 'INCONCLUSIVE'
} elseif ($finalState.Installed) {
    $rxBytes = $finalDelta.ReceivedBytes
    if     ($rxBytes -lt 100MB)               { $hypothesis = 'H1_FULL_SKIP' }
    elseif ($rxBytes -lt ($srcBytes * 0.8))   { $hypothesis = 'H2_DELTA' }
    else                                       { $hypothesis = 'H3_FULL_REDOWNLOAD' }
} else {
    if ($finalDelta.ReceivedBytes -ge ($srcBytes * 0.8)) {
        $hypothesis = 'H3_FULL_REDOWNLOAD'
    } else {
        $hypothesis = 'NOT_DETECTED'
    }
}

$verdict = [ordered]@{
    StartedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
    ReceiverHost      = $env:COMPUTERNAME
    Identity          = $identity
    Source            = $Source
    Deploy            = $destGame
    GameName          = $gameName
    PackageFamilyName = $pfn
    SourceBytes       = $srcBytes
    SourceFileCount   = $summary.SourceFileCount
    DeployedBytes     = [int64]$destBytes
    DeployedFileCount = $destFiles.Count
    RobocopyExit      = $rcExit
    Baseline          = $baseline
    ObserveSeconds    = $ObserveSeconds
    Samples           = $samples
    FinalDelta        = $finalDelta
    PackageRegistered = $finalState.Installed
    PackageInstallLoc = $finalState.InstallLocation
    PackageStatus     = $finalState.Status
    ObservedReceivedMB= $finalDelta.ReceivedMB
    Hypothesis        = $hypothesis
}

$verdictPath = Join-Path $runsDir "receiver-verdict-$stamp.json"
$verdict | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verdictPath -Encoding UTF8
Write-Host ""
Write-Host "[SYSTEM phase] Verdict: $hypothesis" -ForegroundColor Yellow
Write-Host "[SYSTEM phase] Written: $verdictPath"

exit 0
