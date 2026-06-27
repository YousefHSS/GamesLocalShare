<#
  xbox-cache-hosts.ps1 - point Xbox content hostnames at the LAN cache (or remove that).
  Run ELEVATED on the DOWNLOADING PC. Edits %SystemRoot%\System32\drivers\etc\hosts.

  Add :    .\xbox-cache-hosts.ps1 -ProxyIP 192.168.1.244
  Remove:  .\xbox-cache-hosts.ps1 -Remove

  Adds lines between BEGIN/END markers so removal is clean. ASCII only. PS 5.1 compatible.
#>
param(
  [string]$ProxyIP,
  [string[]]$Hosts = @('assets1.xboxlive.com'),
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (editing the hosts file needs admin)." -ForegroundColor Red; return }
if (-not $Remove -and -not $ProxyIP) { Write-Host "ERROR: pass -ProxyIP <cache IP> (or -Remove)." -ForegroundColor Red; return }

$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$begin = '# BEGIN xbox-lan-cache'
$end   = '# END xbox-lan-cache'

$content = if (Test-Path $hostsPath) { Get-Content -LiteralPath $hostsPath } else { @() }

# strip any existing managed block
$out = New-Object System.Collections.Generic.List[string]
$inBlock = $false
foreach ($line in $content) {
  if ($line -eq $begin) { $inBlock = $true; continue }
  if ($line -eq $end)   { $inBlock = $false; continue }
  if (-not $inBlock)    { $out.Add($line) }
}

if (-not $Remove) {
  $out.Add($begin)
  foreach ($h in $Hosts) { $out.Add(("{0}`t{1}" -f $ProxyIP, $h)) }
  $out.Add($end)
}

Set-Content -LiteralPath $hostsPath -Value $out -Encoding ASCII
ipconfig /flushdns | Out-Null

if ($Remove) {
  Write-Host "Removed xbox-lan-cache hosts entries; DNS cache flushed." -ForegroundColor Green
} else {
  Write-Host ("Pointed these hosts at {0} (DNS cache flushed):" -f $ProxyIP) -ForegroundColor Green
  foreach ($h in $Hosts) { Write-Host ("   {0} -> {1}" -f $h, $ProxyIP) }
  Write-Host "Reverse later with: .\xbox-cache-hosts.ps1 -Remove" -ForegroundColor Cyan
}
