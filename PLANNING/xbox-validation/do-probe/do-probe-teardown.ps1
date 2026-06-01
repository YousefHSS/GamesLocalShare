<#
  do-probe-teardown.ps1 - remove the probe's Delivery Optimization settings.
  Run elevated on the DOWNLOADING machine.   ASCII only.
    .\do-probe-teardown.ps1
#>
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization'
foreach ($n in 'DOCacheHost','DODelayCacheServerFallbackForeground','DODelayCacheServerFallbackBackground') {
  Remove-ItemProperty -Path $key -Name $n -ErrorAction SilentlyContinue
}
Write-Host "Removed probe DO settings. Remaining values under the policy key:" -ForegroundColor Green
Get-ItemProperty -Path $key -ErrorAction SilentlyContinue | Format-List | Out-String | Write-Host
Write-Host "Restarting DoSvc..." -ForegroundColor Cyan
try { Restart-Service -Name DoSvc -Force -ErrorAction Stop } catch { Write-Host ("  (note: {0})" -f $_.Exception.Message) -ForegroundColor Yellow }
Write-Host "Done. Delivery Optimization is back to normal." -ForegroundColor Green
