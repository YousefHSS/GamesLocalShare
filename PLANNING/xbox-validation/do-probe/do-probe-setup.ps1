<#
  do-probe-setup.ps1 - point this PC's Delivery Optimization at our probe cache.
  Run elevated on the DOWNLOADING machine (e.g. the laptop), NOT the proxy machine.

    .\do-probe-setup.ps1 -ProxyIP 192.168.1.50

  Sets:
    DOCacheHost                          = <ProxyIP>     (use this cache server)
    DODelayCacheServerFallbackForeground = 70            (wait 70s before giving up to CDN; game installs are foreground)
    DODelayCacheServerFallbackBackground = 70
  Then restarts DoSvc so the policy is picked up.
  Reverse it later with do-probe-teardown.ps1.   ASCII only.
#>
param(
  [Parameter(Mandatory=$true)][string]$ProxyIP,
  [int]$FallbackDelaySeconds = 70
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$key = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization'
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name 'DOCacheHost' -Value $ProxyIP -PropertyType String -Force | Out-Null
New-ItemProperty -Path $key -Name 'DODelayCacheServerFallbackForeground' -Value $FallbackDelaySeconds -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name 'DODelayCacheServerFallbackBackground' -Value $FallbackDelaySeconds -PropertyType DWord -Force | Out-Null

Write-Host "Set Delivery Optimization policy:" -ForegroundColor Green
Get-ItemProperty -Path $key | Select-Object DOCacheHost,DODelayCacheServerFallbackForeground,DODelayCacheServerFallbackBackground | Format-List | Out-String | Write-Host

Write-Host "Restarting DoSvc..." -ForegroundColor Cyan
try { Restart-Service -Name DoSvc -Force -ErrorAction Stop } catch { Write-Host ("  (DoSvc restart note: {0})" -f $_.Exception.Message) -ForegroundColor Yellow }
try { Get-DeliveryOptimizationStatus | Out-Null } catch {}

Write-Host ""
Write-Host "Done. Now: make sure the proxy is running on $ProxyIP:80, then install a small game" -ForegroundColor Green
Write-Host "from the Xbox app on THIS PC and watch the proxy log for game-content requests." -ForegroundColor Green
Write-Host "When finished, run do-probe-teardown.ps1 to remove these settings." -ForegroundColor Green
