<#
  capture-stacks.ps1 - MODULE-level proof of who computes the MSIXVC content hashes.

  ProcMon can't show this (its hash input is socket memory, not a file read; and its CLI
  export drops stacks). So we use xperf CPU-sampled stackwalks during a GENUINE Path A
  install (serve the real cached bytes -> GS hashes the full ~1.7 GB -> a long, easy-to-
  sample hashing window, unlike the failing run which aborts at the first page).

  READ THE RESULT:
    stacks rooted in gamingservices.exe -> bcrypt.dll / bcryptprimitives.dll (SymCrypt)
        => USER-MODE GamingServices computes the SHA-256. (expected)
    stacks in System -> xvdd.sys / cng.sys / ksecdd.sys
        => the KERNEL driver hashes.

  PREREQ for a genuine Path A run:
    reconstruct\pathb-zero-content.ps1 -Restore   (puts the genuine full .msixvc back)
    proxy running (xbox-cache-proxy.ps1 -NoOrigin) + all content hosts redirected.
    Then the Xbox install pulls genuine bytes from the LAN cache and hashes them all.

  Run ELEVATED (xperf kernel logger). Windows PowerShell 5.1. ASCII only.
    .\capture-stacks.ps1 -Start     # begin xperf stackwalk
    .\capture-stacks.ps1 -Stop      # stop, dump stacks, grep crypto frames
#>
param(
  [switch]$Start,
  [switch]$Stop,
  [string]$Xperf  = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe",
  [string]$OutDir = "$PSScriptRoot\out",
  [switch]$NoSymbols   # skip MS symbol server (faster; you still get MODULE names, just not functions)
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (xperf kernel logger needs admin)." -ForegroundColor Red; return }
if (-not (Test-Path $Xperf)) { Write-Host ("ERROR: xperf not found at {0}" -f $Xperf) -ForegroundColor Red; return }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$etl    = Join-Path $OutDir 'stacks.etl'
$stacks = Join-Path $OutDir 'stacks.txt'

# PS 5.1: redirecting a native exe's stderr (2>$null) wraps lines as ErrorRecords and,
# under ErrorActionPreference=Stop, halts the script. So run xperf with EAP=Continue and
# NO stderr redirection -- benign messages (e.g. "no session to stop") just print.
function Invoke-Xperf {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { & $Xperf @args } finally { $ErrorActionPreference = $old }
}

if ($Start) {
  Invoke-Xperf -stop | Out-Null   # clear any prior kernel session (benign if none)
  # better user-mode kernel-stack fidelity
  try { Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'DisablePagingExecutive' 1 -ErrorAction Stop } catch {}
  Write-Host "Starting xperf CPU-sampled stackwalk..." -ForegroundColor Cyan
  # big buffers so we don't drop events; PROC_THREAD+LOADER needed to attribute frames to modules
  Invoke-Xperf -on PROC_THREAD+LOADER+PROFILE -stackwalk Profile -buffersize 1024 -minbuffers 256 -maxbuffers 1024
  Write-Host ""
  Write-Host "CAPTURING. Now:" -ForegroundColor Yellow
  Write-Host "  1) Make sure genuine content is in the cache:  reconstruct\pathb-zero-content.ps1 -Restore" -ForegroundColor Gray
  Write-Host "  2) Xbox app -> Install Clone Drone (pulls genuine bytes from the LAN cache)." -ForegroundColor Gray
  Write-Host "  3) Let it run ~30-60s while it downloads+hashes (watch the progress climb)." -ForegroundColor Gray
  Write-Host "  4) Run:  .\capture-stacks.ps1 -Stop" -ForegroundColor Gray
  return
}

if ($Stop) {
  Write-Host "1) Stopping xperf -> $etl" -ForegroundColor Cyan
  Invoke-Xperf -stop -d $etl | Out-Null
  if (-not (Test-Path $etl)) { Write-Host "ERROR: no ETL produced." -ForegroundColor Red; return }

  # keep xperf scratch off the small C: drive; local-only symbols = module names, no slow download
  $env:TEMP = "$OutDir\xperftmp"; $env:TMP = $env:TEMP
  New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
  if ($NoSymbols) {
    $env:_NT_SYMBOL_PATH = "$OutDir\symbols"   # module!offset only, offline
    Write-Host "2) Symbols: LOCAL ONLY (module names, no functions)." -ForegroundColor Gray
  } else {
    $env:_NT_SYMBOL_PATH = "srv*$OutDir\symbols*https://msdl.microsoft.com/download/symbols"
    Write-Host "2) Symbols: MS server (functions resolve; first run downloads, needs internet)." -ForegroundColor Gray
  }

  Write-Host "3) Dumping symbolized stacks -> $stacks ..." -ForegroundColor Cyan
  # MUST pass -tle -tti or xperf aborts on lost events / time inversions. -symbols before -i.
  Invoke-Xperf -tle -tti -symbols -i $etl -o $stacks | Out-Null
  if (-not (Test-Path $stacks) -or (Get-Item $stacks).Length -eq 0) { Write-Host "ERROR: stack dump failed/empty." -ForegroundColor Red; return }
  Write-Host ("   dump ready: {0} MB" -f [int]((Get-Item $stacks).Length/1MB)) -ForegroundColor Gray

  # WHO was executing SHA when sampled: filter SampledProfile lines whose stack hit a crypto
  # module, then tally by process. Use piped findstr (fast C) to avoid loading 400MB into PS.
  Write-Host "`n=== processes executing SHA (bcryptprimitives/symcrypt/cng/ksecdd) when sampled ===" -ForegroundColor Cyan
  $shaTmp = Join-Path $OutDir 'sha-samples.txt'
  & cmd /c "findstr /C:`"SampledProfile`" `"$stacks`" | findstr /I /C:`"bcryptprimitives`" /C:`"symcrypt`" /C:`"cng.sys`" /C:`"ksecdd`" > `"$shaTmp`""
  $rows = @(Get-Content $shaTmp -ErrorAction SilentlyContinue)
  Write-Host ("   crypto samples: {0}" -f $rows.Count) -ForegroundColor Gray
  $rows | ForEach-Object { if ($_ -match 'SampledProfile,\s*\d+,\s*([^,]+\(\d+\))') { $matches[1].Trim() } } |
    Group-Object | Sort-Object Count -Descending | Select-Object -First 15 Count,@{n='Process';e={$_.Name}} |
    Format-Table -AutoSize | Out-Host
  Write-Host "  -> the dominant process here is the content-hash verifier (ignore wintrust/Authenticode noise:" -ForegroundColor Gray
  Write-Host "     a real content verifier shows HUNDREDS of samples during an active 1.7GB install)." -ForegroundColor Gray
  Write-Host "`nFull stack dump: $stacks   crypto samples: $shaTmp" -ForegroundColor Gray
  return
}
