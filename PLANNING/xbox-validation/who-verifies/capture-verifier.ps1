<#
  capture-verifier.ps1 - Determine WHO hash-verifies MSIXVC content during a download.

  Two suspects:
    - gamingservices.exe  (user-mode downloader; hashes each downloaded page vs the
      in-header tree BEFORE committing -> rejects bad bytes as they arrive)
    - xvdd.sys (Xvdd)     (kernel XVD driver; verifies page hashes at READ/mount time,
      i.e. at launch / Verify -- NOT during download, because the volume isn't mounted yet)

  Method: capture the Microsoft-Gaming-Services + license + AppX ETW providers to an .etl
  while a download verification FAILS (use the Path B zeroed-content cache as the trigger -
  it forces an immediate, repeatable rejection). Simultaneously sample CPU so we can attribute
  the SHA-256 work to user-mode (gamingservices.exe) vs kernel (System/xvdd).

  DECISION RULE:
    - Mismatch/integrity events emitted by PID=gamingservices.exe on Microsoft-Gaming-Services,
      AND the CPU delta during the loop is dominated by gamingservices.exe USER time
        => the DOWNLOADER verifies during download (driver not involved yet).
    - No GS events, failure correlates with kernel/Xvdd and high System privileged time
        => the DRIVER is the gate.

  Run ELEVATED. Windows PowerShell 5.1. ASCII only.

    .\capture-verifier.ps1 -Start      # arm trace + CPU sampler, then trigger the install
    .\capture-verifier.ps1 -Stop       # stop, parse the .etl, print who emitted what + CPU
#>
param(
  [switch]$Start,
  [switch]$Stop,
  [string]$OutDir = "$PSScriptRoot\out",
  [string]$Session = "XbVerify"
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (ETW trace sessions need admin)." -ForegroundColor Red; return }
if (-not $Start -and -not $Stop) { Write-Host "Pass -Start or -Stop." -ForegroundColor Yellow; return }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$etl     = Join-Path $OutDir 'gaming.etl'
$cpuCsv  = Join-Path $OutDir 'cpu.csv'
$stopFlg = Join-Path $OutDir 'STOP.flag'
$baseJson= Join-Path $OutDir 'cpu-baseline.json'

# Provider GUIDs (from logman query providers on this machine)
$providers = @(
  '{BC1BDB57-71A2-581A-147B-E0B49474A2D4}',  # Microsoft-Gaming-Services
  '{6E0DF32C-7F11-54F7-E8EE-5AD4032727CE}',  # Microsoft-Client-License-Flexible-Platform (ClipSP)
  '{3F471139-ACB7-4A01-B7A7-FF5DA4BA2D43}'   # Microsoft-Windows-AppXDeployment-Server
)

if ($Start) {
  # clean any prior session / flag
  & logman stop $Session -ets 2>$null | Out-Null
  if (Test-Path $stopFlg) { Remove-Item $stopFlg -Force }
  if (Test-Path $etl)     { Remove-Item $etl -Force }

  Write-Host "1) Creating ETW session '$Session' -> $etl" -ForegroundColor Cyan
  # IMPORTANT: attach ALL providers atomically via a provider file (-pf). Using repeated
  # `logman update -p` REPLACES the single provider instead of appending, so only the last
  # one ends up captured. -pf is the only reliable multi-provider path with logman.
  $pf = Join-Path $OutDir 'providers.txt'
  ($providers | ForEach-Object { "$_ 0xffffffffffffffff 0xff" }) | Set-Content -LiteralPath $pf -Encoding ASCII
  & logman create trace $Session -ets -o $etl -ow -nb 16 256 -bs 1024 -pf $pf | Out-Null
  Write-Host "   providers requested:" -ForegroundColor Gray
  $providers | ForEach-Object { Write-Host "     $_" -ForegroundColor Gray }
  Write-Host "   --- logman confirms these are attached to the running session: ---" -ForegroundColor Gray
  (& logman query $Session -ets) | Select-String '\{[0-9A-Fa-f-]+\}' | ForEach-Object { Write-Host ("     {0}" -f $_.ToString().Trim()) -ForegroundColor DarkGray }

  # CPU baseline snapshot for gamingservices/net (user vs privileged seconds)
  $snap = @{}
  foreach ($n in 'gamingservices','gamingservicesnet') {
    $p = Get-Process -Name $n -ErrorAction SilentlyContinue
    if ($p) { $snap[$n] = @{ User = $p.UserProcessorTime.TotalSeconds; Priv = $p.PrivilegedProcessorTime.TotalSeconds; t = (Get-Date).ToString('o') } }
  }
  $snap | ConvertTo-Json | Set-Content -LiteralPath $baseJson -Encoding ASCII

  # background CPU sampler: kernel-wide privileged time + GS process %; stops when STOP.flag appears
  Write-Host "2) Starting CPU sampler -> $cpuCsv" -ForegroundColor Cyan
  $job = Start-Job -ScriptBlock {
    param($csv,$flag)
    "time,pct_total,pct_priv,gs_pct,gsnet_pct,system_pct" | Set-Content -LiteralPath $csv -Encoding ASCII
    while (-not (Test-Path $flag)) {
      try {
        $c = Get-Counter '\Processor(_Total)\% Processor Time','\Processor(_Total)\% Privileged Time',
                         '\Process(gamingservices)\% Processor Time','\Process(gamingservicesnet)\% Processor Time',
                         '\Process(System)\% Processor Time' -ErrorAction SilentlyContinue
        $v = $c.CounterSamples
        $g = { param($i) if ($v.Count -gt $i) { '{0:N1}' -f $v[$i].CookedValue } else { '' } }
        ("{0},{1},{2},{3},{4},{5}" -f (Get-Date).ToString('HH:mm:ss'), (& $g 0), (& $g 1), (& $g 2), (& $g 3), (& $g 4)) |
          Add-Content -LiteralPath $csv -Encoding ASCII
      } catch {}
      Start-Sleep -Milliseconds 1000
    }
  } -ArgumentList $cpuCsv,$stopFlg
  $job.Id | Set-Content -LiteralPath (Join-Path $OutDir 'job.id') -Encoding ASCII

  Write-Host ""
  Write-Host "ARMED. Now trigger the verification:" -ForegroundColor Yellow
  Write-Host "  - Path B zeroed cache must be in place (reconstruct\pathb-zero-content.ps1)," -ForegroundColor Gray
  Write-Host "    proxy running (lan-cache\xbox-cache-proxy.ps1 -NoOrigin), hosts redirected." -ForegroundColor Gray
  Write-Host "  - Xbox app -> Install Clone Drone. Let it reach the error (13.6 MB of 1.7 GB)." -ForegroundColor Gray
  Write-Host "  - Then run:  .\capture-verifier.ps1 -Stop" -ForegroundColor Gray
  return
}

if ($Stop) {
  Write-Host "1) Stopping CPU sampler..." -ForegroundColor Cyan
  Set-Content -LiteralPath $stopFlg -Value 'stop' -Encoding ASCII
  Start-Sleep -Seconds 2
  if (Test-Path (Join-Path $OutDir 'job.id')) {
    $jid = [int](Get-Content (Join-Path $OutDir 'job.id'))
    Get-Job -Id $jid -ErrorAction SilentlyContinue | Stop-Job -ErrorAction SilentlyContinue
    Get-Job -Id $jid -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue
  }

  Write-Host "2) Stopping ETW session '$Session'..." -ForegroundColor Cyan
  & logman stop $Session -ets 2>$null | Out-Null

  # --- CPU attribution: GS user vs privileged seconds consumed during the window ---
  Write-Host ""
  Write-Host "=== CPU attribution (gamingservices delta over the capture window) ===" -ForegroundColor Cyan
  if (Test-Path $baseJson) {
    $base = Get-Content $baseJson -Raw | ConvertFrom-Json
    foreach ($n in 'gamingservices','gamingservicesnet') {
      $p = Get-Process -Name $n -ErrorAction SilentlyContinue
      if ($p -and $base.$n) {
        $du = $p.UserProcessorTime.TotalSeconds - $base.$n.User
        $dp = $p.PrivilegedProcessorTime.TotalSeconds - $base.$n.Priv
        Write-Host ("  {0}: +{1:N2}s USER cpu, +{2:N2}s KERNEL cpu" -f $n, $du, $dp) -ForegroundColor White
      }
    }
    Write-Host "  (Heavy USER seconds on gamingservices => it is the one hashing.)" -ForegroundColor Gray
  }
  if (Test-Path $cpuCsv) {
    $rows = Import-Csv $cpuCsv
    if ($rows) {
      $avg = { param($col) ($rows | Measure-Object -Property $col -Average -ErrorAction SilentlyContinue).Average }
      $mx  = { param($col) ($rows | Measure-Object -Property $col -Maximum -ErrorAction SilentlyContinue).Maximum }
      Write-Host ("  samples={0}  GS%(avg/max)={1:N0}/{2:N0}  System%(avg/max)={3:N0}/{4:N0}  priv%(avg)={5:N0}" -f `
        $rows.Count, (& $avg 'gs_pct'), (& $mx 'gs_pct'), (& $avg 'system_pct'), (& $mx 'system_pct'), (& $avg 'pct_priv')) -ForegroundColor White
      Write-Host "  full CPU trace: $cpuCsv" -ForegroundColor Gray
    }
  }

  # --- ETW events: who emitted integrity/verify/mismatch messages ---
  Write-Host ""
  Write-Host "=== ETW per-provider summary (tracerpt - confirms which providers actually fired) ===" -ForegroundColor Cyan
  $sum = Join-Path $OutDir 'summary.txt'
  $dmp = Join-Path $OutDir 'dump.xml'
  & tracerpt $etl -o $dmp -of XML -summary $sum -y 2>&1 | Out-Null
  if (Test-Path $sum) { Get-Content $sum | Where-Object { $_ -match '\{|Event Count|Total Events' } | Out-Host }
  # grep the raw XML dump for integrity/verify strings + the Gaming-Services provider GUID
  if (Test-Path $dmp) {
    Write-Host "  --- raw XML lines matching integrity/verify/hash + nearby provider/PID ---" -ForegroundColor Yellow
    $rxXml = 'hash|integrit|verif|mismat|corrupt|tamper|signature|invalid|BC1BDB57|6E0DF32C'
    $hitsXml = Select-String -LiteralPath $dmp -Pattern $rxXml -ErrorAction SilentlyContinue
    if ($hitsXml) { $hitsXml | Select-Object -First 60 | ForEach-Object { Write-Host ("    {0}: {1}" -f $_.LineNumber, ($_.Line.Trim())) -ForegroundColor DarkGray } }
    else { Write-Host "    (no integrity/Gaming-Services matches in XML dump)" -ForegroundColor DarkGray }
    Write-Host ("  full XML dump: {0}" -f $dmp) -ForegroundColor Gray
  }

  Write-Host ""
  Write-Host "=== ETW events (Get-WinEvent render of $etl) ===" -ForegroundColor Cyan
  $evts = @()
  try { $evts = Get-WinEvent -Path $etl -Oldest -ErrorAction Stop } catch { Write-Host ("  Get-WinEvent failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow }
  if ($evts.Count -gt 0) {
    Write-Host ("  total events: {0}" -f $evts.Count) -ForegroundColor Gray
    Write-Host "  --- events per provider/PID ---" -ForegroundColor Gray
    $evts | Group-Object ProviderName, ProcessId | Sort-Object Count -Descending |
      Select-Object -First 20 Count, Name | Format-Table -AutoSize | Out-Host

    $rx = 'hash|integrit|verif|mismat|corrupt|tamper|signature|block|invalid|fail|reject'
    $hits = $evts | Where-Object { $_.Message -match $rx }
    Write-Host ("  --- {0} events matching '{1}' ---" -f $hits.Count, $rx) -ForegroundColor Yellow
    $hits | Select-Object -First 40 TimeCreated, ProviderName, ProcessId, Id, @{n='Msg';e={ ($_.Message -replace '\s+',' ').Substring(0,[Math]::Min(160,($_.Message -replace '\s+',' ').Length)) }} |
      Format-Table -AutoSize -Wrap | Out-Host
    $txt = Join-Path $OutDir 'events-matched.txt'
    $hits | Select-Object TimeCreated, ProviderName, ProcessId, Id, Message | Format-List | Out-File -LiteralPath $txt -Encoding utf8
    Write-Host ("  full matched events written to: {0}" -f $txt) -ForegroundColor Gray
  } else {
    Write-Host "  No events rendered by Get-WinEvent. Fallback dump:" -ForegroundColor Yellow
    $xml = Join-Path $OutDir 'gaming.xml'
    & tracerpt $etl -o $xml -of XML -y 2>$null | Out-Null
    Write-Host ("  Raw ETW XML: {0} (search for the emitting PID + integrity strings)" -f $xml) -ForegroundColor Gray
  }

  Write-Host ""
  Write-Host "Map PIDs: gamingservices=$((Get-Process gamingservices -ErrorAction SilentlyContinue).Id)  gamingservicesnet=$((Get-Process gamingservicesnet -ErrorAction SilentlyContinue).Id)" -ForegroundColor Cyan
  return
}
