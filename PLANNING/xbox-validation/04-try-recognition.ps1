<#
.SYNOPSIS
    Run the four escalating Xbox-app recognition triggers and record bytes
    downloaded at each step.

.DESCRIPTION
    Walks the operator through (interactively) each of:
      1. Passive   - open Xbox app, observe.
      2. Repair    - Xbox app "Verify and Repair" on the title.
      3. Register  - Add-AppxPackage -Register against AppxManifest.xml.
      4. Install   - Xbox app "Install" fallback.

    For each step, NIC byte counters are sampled before and after, the
    operator is prompted with what to do in the Xbox app, and a JSON entry
    is written to .\runs\recognition-<timestamp>.json.

.PARAMETER GameFolder
    Deployed game folder on PC B, e.g. "D:\XboxGames\Hi-Fi Rush".

.PARAMETER PackageFamilyName
    Optional. From baseline.json. Used to query Get-AppxPackage status
    before/after each step.

.PARAMETER InterfaceAlias
    Optional NIC alias to scope the byte counters (recommended).

.EXAMPLE
    .\04-try-recognition.ps1 -GameFolder "D:\XboxGames\Hi-Fi Rush" `
        -PackageFamilyName "BethesdaSoftworks.HiFiRush_3275kfvn8vcwc" `
        -InterfaceAlias "Wi-Fi"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GameFolder,
    [string] $PackageFamilyName,
    [string] $InterfaceAlias,
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
    Write-Host "WARNING: This session is NOT elevated." -ForegroundColor DarkYellow
    Write-Host "Step 3 (Add-AppxPackage -Register) and reliable Xbox-app interaction" -ForegroundColor DarkYellow
    Write-Host "typically need admin. You can continue, but expect partial results." -ForegroundColor DarkYellow
    Write-Host ""
}

if (-not (Test-Path -LiteralPath $GameFolder)) {
    throw "GameFolder not found: $GameFolder"
}

$null = New-Item -ItemType Directory -Path $LogDir -Force
$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$resultsPath = Join-Path $LogDir "recognition-$timestamp.json"

function Get-NicBytes {
    param([string] $Alias)
    if ($Alias) {
        $s = Get-NetAdapterStatistics -Name $Alias -ErrorAction Stop
        return [pscustomobject]@{ Received = [int64]$s.ReceivedBytes; Sent = [int64]$s.SentBytes }
    }
    $stats = Get-NetAdapter -Physical -ErrorAction Stop |
        Where-Object Status -eq 'Up' |
        ForEach-Object { Get-NetAdapterStatistics -Name $_.Name -ErrorAction SilentlyContinue }
    [pscustomobject]@{
        Received = [int64](($stats | Measure-Object ReceivedBytes -Sum).Sum)
        Sent     = [int64](($stats | Measure-Object SentBytes     -Sum).Sum)
    }
}

function Get-PackageState {
    param([string] $Pfn)
    if (-not $Pfn) { return $null }
    try {
        $pkg = Get-AppxPackage -Name ($Pfn -split '_')[0] -ErrorAction SilentlyContinue |
            Where-Object PackageFamilyName -ieq $Pfn |
            Select-Object -First 1
        if (-not $pkg) { return [pscustomobject]@{ Installed = $false } }
        return [pscustomobject]@{
            Installed        = $true
            PackageFullName  = $pkg.PackageFullName
            Version          = $pkg.Version.ToString()
            InstallLocation  = $pkg.InstallLocation
            Status           = $pkg.Status.ToString()
            SignatureKind    = $pkg.SignatureKind.ToString()
        }
    } catch {
        return [pscustomobject]@{ Installed = $false; Error = $_.Exception.Message }
    }
}

function Get-FolderBytes {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [int64]0 }
    $sum = (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return [int64]0 }
    [int64]$sum
}

function Run-Step {
    param(
        [string]   $Name,
        [string]   $Description,
        [scriptblock] $Action
    )

    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host (" STEP: {0}" -f $Name) -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host $Description
    Write-Host ""

    $beforeNic    = Get-NicBytes -Alias $InterfaceAlias
    $beforePkg    = Get-PackageState -Pfn $PackageFamilyName
    $beforeBytes  = Get-FolderBytes -Path $GameFolder
    $beforeTime   = Get-Date

    $actionOutput = $null
    $actionError  = $null
    try {
        $actionOutput = & $Action
    } catch {
        $actionError = $_.Exception.Message
        Write-Warning "Step action error: $actionError"
    }

    Read-Host "Press ENTER after the Xbox-app side of this step has finished (or you give up)"

    $afterNic   = Get-NicBytes -Alias $InterfaceAlias
    $afterPkg   = Get-PackageState -Pfn $PackageFamilyName
    $afterBytes = Get-FolderBytes -Path $GameFolder
    $afterTime  = Get-Date

    $deltaRx = $afterNic.Received - $beforeNic.Received
    $deltaTx = $afterNic.Sent     - $beforeNic.Sent
    $deltaDisk = $afterBytes - $beforeBytes
    $elapsed = ($afterTime - $beforeTime).TotalSeconds

    $playable = Read-Host "Is the game launchable from the Xbox app now? [y/N]"
    $notes    = Read-Host "Free-form notes (one line)"

    $entry = [pscustomobject]@{
        Step              = $Name
        Description       = $Description
        StartedAtUtc      = $beforeTime.ToUniversalTime().ToString('o')
        EndedAtUtc        = $afterTime.ToUniversalTime().ToString('o')
        ElapsedSeconds    = [math]::Round($elapsed, 1)
        ReceivedBytes     = $deltaRx
        ReceivedMB        = [math]::Round($deltaRx / 1MB, 2)
        SentBytes         = $deltaTx
        DiskDeltaBytes    = $deltaDisk
        BeforePackage     = $beforePkg
        AfterPackage      = $afterPkg
        ActionOutput      = $actionOutput
        ActionError       = $actionError
        PlayableAfter     = ($playable -match '^[Yy]')
        Notes             = $notes
    }

    Write-Host ("  Bytes received during step : {0:N0} ({1:N2} MB)" -f $deltaRx, $entry.ReceivedMB)
    Write-Host ("  Folder size delta          : {0:N0} bytes" -f $deltaDisk)
    Write-Host ("  Package installed (after)  : {0}" -f $afterPkg.Installed)

    return $entry
}

$entries = New-Object System.Collections.Generic.List[object]

# --- Step 1: Passive --------------------------------------------------------
$entries.Add( (Run-Step `
    -Name 'passive' `
    -Description "Open the Xbox app and navigate to the game's library entry. Do NOT click Install or Repair. Just observe whether it shows up as Installed / has a Play button." `
    -Action {
        Start-Process -FilePath 'explorer.exe' -ArgumentList 'shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!App' -ErrorAction SilentlyContinue | Out-Null
        return 'Xbox app launched (passive observation only).'
    }) )

# --- Step 2: Repair / Verify -----------------------------------------------
$entries.Add( (Run-Step `
    -Name 'repair' `
    -Description "In the Xbox app, open the game's options menu and choose 'Manage' > 'Files' > 'Verify and Repair' (wording varies). Let it run to completion." `
    -Action {
        return 'Operator-driven Xbox-app Repair.'
    }) )

# --- Step 3: Add-AppxPackage -Register --------------------------------------
$manifestPath = $null
foreach ($candidate in @(
    (Join-Path $GameFolder 'Content\AppxManifest.xml'),
    (Join-Path $GameFolder 'AppxManifest.xml')
)) {
    if (Test-Path -LiteralPath $candidate) { $manifestPath = $candidate; break }
}

$entries.Add( (Run-Step `
    -Name 'register' `
    -Description ("Run Add-AppxPackage -Register against the deployed AppxManifest.xml`nManifest: {0}" -f $(if ($manifestPath) { $manifestPath } else { '(not found)' })) `
    -Action {
        if (-not $manifestPath) { throw "No AppxManifest.xml found under $GameFolder" }
        $cmd = "Add-AppxPackage -Register `"$manifestPath`" -DisableDevelopmentMode"
        Write-Host "Running: $cmd" -ForegroundColor DarkGray
        Add-AppxPackage -Register $manifestPath -DisableDevelopmentMode
        return $cmd
    }) )

# --- Step 4: Install fallback ----------------------------------------------
$entries.Add( (Run-Step `
    -Name 'install-fallback' `
    -Description "In the Xbox app, click the 'Install' button on the game. Measure the actual bytes downloaded (compare against the baseline total size). If it pulls the full payload, that's the H3 worst-case." `
    -Action {
        return 'Operator-driven Xbox-app Install.'
    }) )

# --- Save -------------------------------------------------------------------
$report = [pscustomobject]@{
    StartedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
    Host              = $env:COMPUTERNAME
    GameFolder        = $GameFolder
    PackageFamilyName = $PackageFamilyName
    InterfaceAlias    = $InterfaceAlias
    Steps             = $entries
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultsPath -Encoding UTF8

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " Summary" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
foreach ($e in $entries) {
    $status = if ($e.PlayableAfter) { 'PLAYABLE' } else { '-' }
    Write-Host ("{0,-18}  rx={1,8:N2} MB  diskDelta={2,12:N0} B  {3}  {4}" -f `
        $e.Step, $e.ReceivedMB, $e.DiskDeltaBytes, $status, $e.Notes)
}
Write-Host ""
Write-Host "Full JSON: $resultsPath" -ForegroundColor Green
Write-Host "Next: copy the numbers into results-template.md and pick H1/H2/H3." -ForegroundColor Cyan
