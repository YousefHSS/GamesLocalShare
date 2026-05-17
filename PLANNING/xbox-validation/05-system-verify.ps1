<#
.SYNOPSIS
    Verify that running as NT AUTHORITY\SYSTEM bypasses MSIX package-guard
    ACLs and can read files that even an elevated admin process cannot.

.DESCRIPTION
    Background: MSIX / Xbox PC gaming packages protect their executables with
    SYSAPPID-conditional ACEs that an elevated admin process CANNOT bypass,
    even with SeBackupPrivilege. The Xbox app / Gaming Services work around
    this by running as LocalSystem. For the production GamesLocalShare Xbox
    transfer feature, the file I/O side must therefore run as SYSTEM (e.g. a
    Windows service component).

    This script is the manual verification of that design assumption. It is
    NOT meant to be run directly - launch it inside a SYSTEM shell.

.PARAMETER GameFolder
    The MSIX/Xbox game install folder to probe.

.PARAMETER OutPath
    Where to write the JSON result.

.HOW TO RUN AS SYSTEM (one-time setup)
    1. Download Sysinternals PsExec:
         https://learn.microsoft.com/en-us/sysinternals/downloads/psexec
       Extract PsExec64.exe somewhere convenient.
    2. From an ELEVATED PowerShell:
         .\PsExec64.exe -accepteula -s -i powershell.exe
       A new window opens. `whoami` should print `nt authority\system`.
    3. In the SYSTEM shell:
         cd <repo>\PLANNING\xbox-validation
         .\05-system-verify.ps1 -GameFolder "F:\Games\Slay The Spire" `
                                -OutPath .\runs\system-verify.json
    4. Inspect the JSON. If `UnreadableCount` is 0 (or matches the *expected*
       protected count and ReadableCount is much higher than the admin run),
       SYSTEM bypasses the package guard - design assumption confirmed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GameFolder,
    [string] $OutPath = (Join-Path $PSScriptRoot 'runs\system-verify.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $GameFolder)) {
    throw "GameFolder not found: $GameFolder"
}

$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
Write-Host "Current identity: $me" -ForegroundColor Cyan
if ($me -notmatch 'SYSTEM') {
    Write-Warning "You are NOT running as SYSTEM. This script is meaningless unless launched via 'PsExec64 -s -i powershell.exe'."
    Write-Warning "Continue anyway? Press Ctrl+C to abort."
    Start-Sleep -Seconds 3
}

$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutPath) -Force

$readable    = 0
$unreadable  = New-Object System.Collections.Generic.List[string]
$totalBytes  = 0L
$readBytes   = 0L

foreach ($f in Get-ChildItem -LiteralPath $GameFolder -Recurse -Force -File -ErrorAction SilentlyContinue) {
    $totalBytes += $f.Length
    try {
        $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
        $null = $fs.ReadByte()
        $fs.Close()
        $readable++
        $readBytes += $f.Length
    } catch {
        $unreadable.Add($f.FullName) | Out-Null
    }
}

$result = [pscustomobject]@{
    CapturedAtUtc    = (Get-Date).ToUniversalTime().ToString('o')
    Host             = $env:COMPUTERNAME
    Identity         = $me
    GameFolder       = $GameFolder
    TotalFiles       = $readable + $unreadable.Count
    ReadableCount    = $readable
    UnreadableCount  = $unreadable.Count
    TotalBytes       = $totalBytes
    ReadableBytes    = $readBytes
    UnreadableFiles  = @($unreadable)
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutPath -Encoding UTF8

Write-Host ""
Write-Host ("Readable   : {0,5} files ({1:N0} bytes)" -f $readable, $readBytes) -ForegroundColor Green
Write-Host ("Unreadable : {0,5} files" -f $unreadable.Count) -ForegroundColor $(if ($unreadable.Count -eq 0) { 'Green' } else { 'Yellow' })
Write-Host ("Result     : {0}" -f $OutPath)
Write-Host ""
if ($unreadable.Count -eq 0) {
    Write-Host "SUCCESS: SYSTEM identity reads every file. Production transfer needs a SYSTEM-side helper." -ForegroundColor Green
} else {
    Write-Host "PARTIAL: even SYSTEM cannot read $($unreadable.Count) file(s)." -ForegroundColor Yellow
    Write-Host "  Investigate before relying on the SYSTEM-helper design." -ForegroundColor Yellow
}
