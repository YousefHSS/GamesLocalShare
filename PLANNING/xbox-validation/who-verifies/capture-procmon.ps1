<#
  capture-procmon.ps1 - Definitively identify WHICH PROCESS reads/writes the MSIXVC content
  during a download and aborts at the verification boundary (offset 14,303,232).

  ETW (Microsoft-Gaming-Services) emitted nothing usable, so we observe file I/O directly.
  ProcMon doesn't depend on the component choosing to log anything: it shows every CreateFile/
  ReadFile/WriteFile with PROCESS, OFFSET, LENGTH, and RESULT. The process whose content read/
  write stops at ~14.3 MB is the verifier; reads BEFORE a mount means user-mode gamingservices.exe,
  reads via the volume/Xvdd means the kernel driver.

  Run ELEVATED (ProcMon loads a driver). Windows PowerShell 5.1. ASCII only.

    .\capture-procmon.ps1 -Start      # begin headless capture
    .\capture-procmon.ps1 -Stop       # stop, export CSV, auto-grep the Clone Drone rows
#>
param(
  [switch]$Start,
  [switch]$Stop,
  [string]$Procmon = "C:\Users\SIGMA\Downloads\ProcessMonitor\Procmon64.exe",
  [string]$OutDir  = "$PSScriptRoot\out",
  # match the content-id GUID / package family / container extension in the Path column
  [string]$Match   = '368B2C2C|CloneDrone|\.msixvc|\.xvc'
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (ProcMon loads a driver)." -ForegroundColor Red; return }
if (-not (Test-Path $Procmon)) { Write-Host ("ERROR: ProcMon not found at {0}" -f $Procmon) -ForegroundColor Red; return }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$pml = Join-Path $OutDir 'procmon.pml'
$csv = Join-Path $OutDir 'procmon.csv'

if ($Start) {
  & $Procmon /Terminate 2>$null | Out-Null   # ensure no stale instance
  if (Test-Path $pml) { Remove-Item $pml -Force -ErrorAction SilentlyContinue }
  Write-Host "Starting headless ProcMon capture -> $pml" -ForegroundColor Cyan
  Start-Process -FilePath $Procmon -ArgumentList @('/AcceptEula','/Quiet','/Minimized','/BackingFile', $pml)
  Start-Sleep -Seconds 2
  Write-Host "CAPTURING. Keep the window SHORT:" -ForegroundColor Yellow
  Write-Host "  1) Xbox app -> Install Clone Drone." -ForegroundColor Gray
  Write-Host "  2) Wait for the 'Error - 13.6 MB of 1.7 GB'." -ForegroundColor Gray
  Write-Host "  3) Immediately run:  .\capture-procmon.ps1 -Stop" -ForegroundColor Gray
  return
}

if ($Stop) {
  Write-Host "1) Stopping ProcMon..." -ForegroundColor Cyan
  & $Procmon /Terminate | Out-Null
  Start-Sleep -Seconds 2
  Write-Host "2) Exporting CSV (this can take a moment)..." -ForegroundColor Cyan
  & $Procmon /OpenLog $pml /SaveAs $csv | Out-Null
  if (-not (Test-Path $csv)) { Write-Host "ERROR: CSV not produced." -ForegroundColor Red; return }
  $sizeMB = [int]((Get-Item $csv).Length/1MB)
  Write-Host ("   CSV ready ({0} MB): {1}" -f $sizeMB, $csv) -ForegroundColor Gray

  Write-Host ""
  Write-Host ("=== rows touching the package (Path matches '{0}') ===" -f $Match) -ForegroundColor Cyan
  # stream the CSV; keep only rows whose Path matches, parse the few columns we need
  $rows = Import-Csv -LiteralPath $csv | Where-Object { $_.Path -match $Match }
  Write-Host ("   matched rows: {0}" -f $rows.Count) -ForegroundColor Gray
  if ($rows.Count -eq 0) {
    Write-Host "   No matching rows. Widen -Match or confirm the install actually ran during capture." -ForegroundColor Yellow
    return
  }

  Write-Host "`n--- processes that touched the content, by operation ---" -ForegroundColor Cyan
  $rows | Group-Object 'Process Name','Operation' | Sort-Object Count -Descending |
    Select-Object Count, Name | Format-Table -AutoSize | Out-Host

  Write-Host "--- distinct processes (the verifier is whoever reads the content back) ---" -ForegroundColor Cyan
  $rows | Group-Object 'Process Name' | Sort-Object Count -Descending |
    Select-Object Count, @{n='Process';e={$_.Name}}, @{n='PIDs';e={ ($_.Group.PID | Sort-Object -Unique) -join ',' }} |
    Format-Table -AutoSize | Out-Host

  Write-Host "--- non-SUCCESS results on the content (the rejection) ---" -ForegroundColor Yellow
  $rows | Where-Object { $_.Result -and $_.Result -ne 'SUCCESS' } |
    Select-Object 'Time of Day','Process Name',PID,Operation,Result,
      @{n='Detail';e={ ($_.Detail -replace '\s+',' ').Substring(0,[Math]::Min(90,($_.Detail -replace '\s+',' ').Length)) }} |
    Select-Object -First 40 | Format-Table -AutoSize -Wrap | Out-Host

  Write-Host "--- highest file OFFSET each process reached on the content (boundary = 14,303,232) ---" -ForegroundColor Cyan
  $rows | Where-Object { $_.Detail -match 'Offset:\s*([\d,]+)' } | ForEach-Object {
    $o = [int64](($_.Detail -replace '.*Offset:\s*([\d,]+).*','$1') -replace ',','')
    [pscustomobject]@{ Proc=$_.'Process Name'; Op=$_.Operation; Offset=$o }
  } | Group-Object Proc | ForEach-Object {
    $mx = ($_.Group | Measure-Object Offset -Maximum).Maximum
    Write-Host ("   {0,-22} max offset = {1:N0}" -f $_.Name, $mx) -ForegroundColor White
  }

  $matchedCsv = Join-Path $OutDir 'procmon-matched.csv'
  $rows | Export-Csv -LiteralPath $matchedCsv -NoTypeInformation
  Write-Host ("`nFull matched rows: {0}" -f $matchedCsv) -ForegroundColor Gray
  Write-Host "For MODULE-level (which DLL hashes): open procmon.pml in the GUI, find the content" -ForegroundColor Gray
  Write-Host "read that stops at offset 14,303,232, right-click -> Stack." -ForegroundColor Gray
  return
}
