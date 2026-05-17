<#
    Shared helpers for xbox-transfer-sender.ps1 and xbox-transfer-receiver.ps1.

    Provides:
      - Test-IsElevated / Assert-Elevated (with auto re-launch as admin)
      - Ensure-PsExec        (downloads PsExec64.exe from live.sysinternals.com)
      - Invoke-AsSystem      (re-launches the calling script as NT AUTHORITY\SYSTEM)
      - Get-NicBaseline / Get-NicDelta
      - Stop-XboxApp / Start-XboxApp
      - Get-XboxPackageState (lookup by PFN, returning InstallLocation + Status)
#>

$ErrorActionPreference = 'Stop'

function Test-IsElevated {
    try {
        $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $p  = New-Object System.Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Test-IsSystem {
    try {
        $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        return ($id.User.Value -eq 'S-1-5-18')
    } catch { return $false }
}

function Assert-Elevated {
    param([string]$ScriptPath, [string[]]$ScriptArgs)
    if (Test-IsElevated) {
        # Recursion guard: if we ended up here as SYSTEM without the
        # InternalSystemPhase / SystemArgsFile path being recognised, the
        # arg-passing got corrupted somehow. Bail out hard rather than
        # spawning another SYSTEM child.
        if (Test-IsSystem) {
            Write-Host "FATAL: running as NT AUTHORITY\SYSTEM in the parent branch." -ForegroundColor Red
            Write-Host "       This means the -SystemArgsFile parameter was lost. Aborting" -ForegroundColor Red
            Write-Host "       to prevent infinite SYSTEM relaunch recursion." -ForegroundColor Red
            exit 99
        }
        return
    }
    Write-Host "Not elevated - relaunching as Administrator..." -ForegroundColor Yellow
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', "`"$ScriptPath`"") + $ScriptArgs
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs | Out-Null
    exit 0
}

function Read-SystemArgs {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "SystemArgsFile not found: $Path"
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    return ($raw | ConvertFrom-Json)
}

function Ensure-PsExec {
    param([string]$ToolsDir)
    if (-not (Test-Path -LiteralPath $ToolsDir)) {
        New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    }
    $psexec = Join-Path $ToolsDir 'PsExec64.exe'
    if (Test-Path -LiteralPath $psexec) { return $psexec }
    Write-Host "Downloading PsExec64.exe from Sysinternals..." -ForegroundColor Cyan
    $url = 'https://live.sysinternals.com/PsExec64.exe'
    try {
        Invoke-WebRequest -Uri $url -OutFile $psexec -UseBasicParsing
    } catch {
        throw "Failed to download PsExec64.exe from $url : $_"
    }
    # Accept EULA preemptively in HKCU so -s doesn't prompt
    $key = 'HKCU:\Software\Sysinternals\PsExec'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name 'EulaAccepted' -Value 1 -Type DWord -Force
    return $psexec
}

function Invoke-AsSystem {
    <#
        Re-launch the calling script as NT AUTHORITY\SYSTEM via PsExec.

        Args are written to a JSON manifest and passed as a single
        -SystemArgsFile parameter. This sidesteps every command-line
        quoting pitfall (trailing backslashes, embedded quotes, spaces,
        etc.) that bit us on earlier iterations.

        Output is captured to a log file; this function tails the log
        until the SYSTEM child process exits and returns the child's
        exit code.
    #>
    param(
        [Parameter(Mandatory)] [string]    $ScriptPath,
        [Parameter(Mandatory)] [hashtable] $Params,
        [Parameter(Mandatory)] [string]    $LogPath,
        [Parameter(Mandatory)] [string]    $PsExecPath
    )

    if (Test-Path -LiteralPath $LogPath) { Remove-Item -LiteralPath $LogPath -Force }
    New-Item -ItemType File -Path $LogPath -Force | Out-Null

    # Write params to a JSON manifest next to the log.
    $manifestPath = [System.IO.Path]::ChangeExtension($LogPath, '.args.json')
    $Params | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $psArgs = @(
        '-NoProfile','-ExecutionPolicy','Bypass',
        '-File', "`"$ScriptPath`"",
        '-SystemArgsFile', "`"$manifestPath`""
    )
    $psExecArgs = @('-accepteula','-nobanner','-s','-h','powershell.exe') + $psArgs

    Write-Host "Launching SYSTEM child:" -ForegroundColor Cyan
    Write-Host "  $PsExecPath $($psExecArgs -join ' ')" -ForegroundColor DarkGray
    Write-Host "  Log: $LogPath" -ForegroundColor DarkGray

    # Redirect stdout+stderr to the log so we can tail it
    $proc = Start-Process -FilePath $PsExecPath `
        -ArgumentList $psExecArgs `
        -RedirectStandardOutput $LogPath `
        -RedirectStandardError  "$LogPath.err" `
        -NoNewWindow -PassThru

    # Tail the log while the child runs
    $lastLen = 0
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 500
        try {
            $fi = Get-Item -LiteralPath $LogPath -ErrorAction Stop
            if ($fi.Length -gt $lastLen) {
                $fs = [System.IO.File]::Open($LogPath,'Open','Read','ReadWrite')
                $null = $fs.Seek($lastLen,'Begin')
                $sr = New-Object System.IO.StreamReader($fs)
                $chunk = $sr.ReadToEnd()
                $sr.Close(); $fs.Close()
                if ($chunk) { Write-Host $chunk -NoNewline }
                $lastLen = $fi.Length
            }
        } catch { }
    }
    # Flush any final output
    try {
        $fi = Get-Item -LiteralPath $LogPath -ErrorAction Stop
        if ($fi.Length -gt $lastLen) {
            $tail = Get-Content -LiteralPath $LogPath -Raw
            $tail = $tail.Substring($lastLen)
            Write-Host $tail -NoNewline
        }
    } catch { }

    return $proc.ExitCode
}

function Get-NicBaseline {
    param([string]$InterfaceAlias)
    if (-not $InterfaceAlias) {
        $InterfaceAlias = (Get-NetAdapter | Where-Object Status -eq 'Up' |
            Sort-Object -Property LinkSpeed -Descending |
            Select-Object -First 1).Name
    }
    $s = Get-NetAdapterStatistics -Name $InterfaceAlias -ErrorAction Stop
    return [pscustomobject]@{
        InterfaceAlias = $InterfaceAlias
        ReceivedBytes  = [int64]$s.ReceivedBytes
        SentBytes      = [int64]$s.SentBytes
        Time           = (Get-Date).ToUniversalTime()
    }
}

function Get-NicDelta {
    param($Baseline)
    $s = Get-NetAdapterStatistics -Name $Baseline.InterfaceAlias -ErrorAction Stop
    $rx = [int64]$s.ReceivedBytes - $Baseline.ReceivedBytes
    $tx = [int64]$s.SentBytes     - $Baseline.SentBytes
    if ($rx -lt 0 -or $tx -lt 0) {
        return [pscustomobject]@{
            ReceivedBytes = -1; SentBytes = -1; ReceivedMB = -1
            CounterWrapped = $true
        }
    }
    return [pscustomobject]@{
        ReceivedBytes  = $rx
        SentBytes      = $tx
        ReceivedMB     = [math]::Round($rx/1MB,2)
        CounterWrapped = $false
    }
}

function Stop-XboxApp {
    $names = @('XboxPcApp','GamingServices','GameBar','XboxAppServices')
    foreach ($n in $names) {
        Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_ | Stop-Process -Force -ErrorAction Stop } catch { }
        }
    }
    Start-Sleep -Seconds 2
}

function Start-XboxApp {
    try {
        Start-Process 'explorer.exe' 'shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App' -ErrorAction Stop
    } catch {
        Start-Process 'xbox:'
    }
}

function Get-XboxPackageState {
    param([string]$PackageFamilyName)
    if (-not $PackageFamilyName) { return @{ Installed = $false; Reason = 'no PFN provided' } }
    try {
        $pkgs = @()
        $pkgs += (Get-AppxPackage -PackageTypeFilter Main -ErrorAction SilentlyContinue |
            Where-Object { $_.PackageFamilyName -eq $PackageFamilyName })
        try {
            $pkgs += (Get-AppxPackage -AllUsers -PackageTypeFilter Main -ErrorAction SilentlyContinue |
                Where-Object { $_.PackageFamilyName -eq $PackageFamilyName })
        } catch { }
        $pkgs = $pkgs | Sort-Object -Property PackageFullName -Unique
        if (-not $pkgs) { return @{ Installed = $false } }
        $p = $pkgs[0]
        return @{
            Installed       = $true
            PackageFullName = $p.PackageFullName
            InstallLocation = $p.InstallLocation
            Status          = "$($p.Status)"
            SignatureKind   = "$($p.SignatureKind)"
        }
    } catch {
        return @{ Installed = $false; Error = "$_" }
    }
}

function Get-SysAppIdFromAcl {
    param([string]$Path)
    try {
        $sddl = (Get-Acl -LiteralPath $Path -ErrorAction Stop).Sddl
    } catch { return $null }
    $m = [regex]::Matches($sddl, 'WIN://SYSAPPID\s+Contains\s+"([^"]+)"')
    if ($m.Count -gt 0) { return $m[0].Groups[1].Value }
    return $null
}
