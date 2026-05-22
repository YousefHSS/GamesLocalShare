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

    # Use -Command with *>&1 instead of -File so that ALL PowerShell
    # output streams (including stream 6 / Write-Host) are merged into
    # stdout. PsExec only captures stdout/stderr pipes; Write-Host
    # output (Information stream) is invisible to PsExec when using
    # -File, causing the log file to be empty and the parent to never
    # see progress updates.
    $cmdString = "& '$($ScriptPath.Replace("'","''"))' -SystemArgsFile '$($manifestPath.Replace("'","''"))' *>&1"
    $psArgs = @(
        '-NoProfile','-ExecutionPolicy','Bypass',
        '-Command', $cmdString
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

    # When the app launches us, stdout is a redirected pipe so
    # [Console]::Out feeds the C# callback but the console window is
    # blank. Detect this and also Write-Host so the user can follow
    # along in the visible PowerShell window.
    $pipeMode = [Console]::IsOutputRedirected

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
                if ($chunk) {
                    [Console]::Out.Write($chunk); [Console]::Out.Flush()
                    if ($pipeMode) { Write-Host $chunk -NoNewline }
                }
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
            [Console]::Out.Write($tail); [Console]::Out.Flush()
            if ($pipeMode) { Write-Host $tail -NoNewline }
        }
    } catch { }

    return $proc.ExitCode
}

function Get-NicBaseline {
    param([string]$InterfaceAlias)
    if (-not $InterfaceAlias) {
        # Exclude virtual/hypervisor adapters; prefer physical NICs
        $InterfaceAlias = (Get-NetAdapter | Where-Object {
            $_.Status -eq 'Up' -and
            $_.HardwareInterface -eq $true -and
            $_.InterfaceDescription -notmatch 'VMware|Hyper-V|Virtual|VirtualBox|WireGuard|Wintun|TAP|VPN'
        } | Sort-Object -Property LinkSpeed -Descending | Select-Object -First 1).Name
    }
    try {
        $s = Get-NetAdapterStatistics -Name $InterfaceAlias -ErrorAction Stop
    } catch {
        # Fallback: use all physical adapters combined via CIM
        $s = Get-CimInstance -ClassName Win32_PerfFormattedData_Tcpip_NetworkInterface -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch 'Loopback|VMware|Hyper-V|Virtual' } |
            Measure-Object -Property BytesReceivedPerSec -Sum |
            Select-Object -ExpandProperty Sum
        if (-not $s) { $s = 0 }
        return [pscustomobject]@{
            InterfaceAlias = 'Fallback-AllPhysical'
            ReceivedBytes  = [int64]$s
            SentBytes      = 0
            Time           = (Get-Date).ToUniversalTime()
        }
    }
    return [pscustomobject]@{
        InterfaceAlias = $InterfaceAlias
        ReceivedBytes  = [int64]$s.ReceivedBytes
        SentBytes      = [int64]$s.SentBytes
        Time           = (Get-Date).ToUniversalTime()
    }
}

function Get-NicDelta {
    param($Baseline)
    if ($Baseline.InterfaceAlias -eq 'Fallback-AllPhysical') {
        $rx = Get-CimInstance -ClassName Win32_PerfFormattedData_Tcpip_NetworkInterface -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch 'Loopback|VMware|Hyper-V|Virtual' } |
            Measure-Object -Property BytesReceivedPerSec -Sum |
            Select-Object -ExpandProperty Sum
        if (-not $rx) { $rx = 0 }
        return [pscustomobject]@{
            ReceivedBytes  = [int64]$rx
            SentBytes      = 0
            ReceivedMB     = [math]::Round([int64]$rx/1MB,2)
            CounterWrapped = $false
        }
    }
    try {
        $s = Get-NetAdapterStatistics -Name $Baseline.InterfaceAlias -ErrorAction Stop
    } catch {
        return [pscustomobject]@{
            ReceivedBytes = -1; SentBytes = -1; ReceivedMB = -1
            CounterWrapped = $true
        }
    }
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

function Copy-ProtectedFilesViaPackage {
    <#
        Copy MSIXVC-protected executables out of an installed game by running
        the copy from inside the game's own package context.

        clipsp decrypts protected files for any process carrying the game's
        package identity, so a helper launched via Invoke-CommandInDesktopPackage
        reads them as valid plaintext executables - something even
        NT AUTHORITY\SYSTEM with backup privilege cannot do.

        Returns a [pscustomobject] with:
          Ok     - $true only if every requested file was copied with a valid
                   'MZ' (4d5a) executable header
          Reason - short status string
          Files  - per-file results (Path / Copied / Header / Size / Error)
    #>
    param(
        [Parameter(Mandatory)] [string]   $PackageFamilyName,
        [Parameter(Mandatory)] [string]   $GameFolder,
        [Parameter(Mandatory)] [string]   $DestGame,
        [Parameter(Mandatory)] [string[]] $RelativePaths,
        [Parameter(Mandatory)] [string]   $RunsDir
    )

    if (-not (Get-Command Invoke-CommandInDesktopPackage -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'Invoke-CommandInDesktopPackage not available'; Files = @() }
    }

    # Find the package and any AppId - package identity (not the specific
    # app) is what grants clipsp decryption.
    $pkg = Get-AppxPackage -PackageTypeFilter Main -ErrorAction SilentlyContinue |
        Where-Object { $_.PackageFamilyName -eq $PackageFamilyName } | Select-Object -First 1
    if (-not $pkg) {
        return [pscustomobject]@{ Ok = $false; Reason = "package not installed: $PackageFamilyName"; Files = @() }
    }
    $appId = $null
    try {
        $manifest = Get-AppxPackageManifest -Package $pkg.PackageFullName
        $appId = ($manifest.Package.Applications.Application | Select-Object -First 1).Id
    } catch { }
    if (-not $appId) {
        return [pscustomobject]@{ Ok = $false; Reason = 'could not determine AppId'; Files = @() }
    }

    $stamp      = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $helperPath = Join-Path $RunsDir "pkg-copy-helper-$stamp.ps1"
    $argsPath   = Join-Path $RunsDir "pkg-copy-args-$stamp.json"
    $resultPath = Join-Path $RunsDir "pkg-copy-result-$stamp.json"

    @{
        GameFolder = $GameFolder
        DestGame   = $DestGame
        Files      = $RelativePaths
        ResultPath = $resultPath
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $argsPath -Encoding UTF8

    # Helper script - runs inside the package context.
    $helper = @'
param([Parameter(Mandatory)][string]$ArgsFile)
$ErrorActionPreference = 'SilentlyContinue'
$a = Get-Content -LiteralPath $ArgsFile -Raw | ConvertFrom-Json
$res = @()
foreach ($rel in @($a.Files)) {
    $src = Join-Path $a.GameFolder $rel
    $dst = Join-Path $a.DestGame   $rel
    $e = [ordered]@{ Path = $rel; Copied = $false; Header = ''; Size = 0; Error = '' }
    try {
        $fs  = [System.IO.File]::Open($src, 'Open', 'Read', 'ReadWrite')
        $len = $fs.Length
        $buf = New-Object byte[] $len
        $off = 0
        while ($off -lt $len) {
            $n = $fs.Read($buf, $off, $len - $off)
            if ($n -le 0) { break }
            $off += $n
        }
        $fs.Close()
        $dir = Split-Path -Parent $dst
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        [System.IO.File]::WriteAllBytes($dst, $buf)
        if ($len -ge 2) { $e.Header = ('{0:x2}{1:x2}' -f $buf[0], $buf[1]) }
        $e.Size   = $len
        $e.Copied = $true
    } catch {
        $e.Error = "$_"
    }
    $res += [pscustomobject]$e
}
[pscustomobject]@{
    Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    Files    = $res
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $a.ResultPath -Encoding UTF8
'@
    Set-Content -LiteralPath $helperPath -Value $helper -Encoding UTF8
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }

    $ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    try {
        Invoke-CommandInDesktopPackage -PackageFamilyName $PackageFamilyName -AppId $appId `
            -Command $ps `
            -Args "-NoProfile -ExecutionPolicy Bypass -File `"$helperPath`" -ArgsFile `"$argsPath`"" `
            -ErrorAction Stop
    } catch {
        return [pscustomobject]@{ Ok = $false; Reason = "Invoke-CommandInDesktopPackage failed: $_"; Files = @() }
    }

    # The launched process is asynchronous - poll for its result file.
    $deadline = (Get-Date).AddSeconds(240)
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $resultPath)) {
        Start-Sleep -Milliseconds 1000
    }
    if (-not (Test-Path -LiteralPath $resultPath)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'package-context copy timed out (240s)'; Files = @() }
    }

    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $files  = @($result.Files)
    $bad    = @($files | Where-Object { -not $_.Copied -or $_.Header -ne '4d5a' })
    $allOk  = ($files.Count -eq $RelativePaths.Count) -and ($bad.Count -eq 0)
    return [pscustomobject]@{
        Ok       = [bool]$allOk
        Reason   = if ($allOk) { 'ok' } else { 'one or more files not decrypted' }
        Identity = $result.Identity
        Files    = $files
    }
}
