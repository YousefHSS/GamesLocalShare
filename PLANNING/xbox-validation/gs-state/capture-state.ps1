# Capture Gaming Services trust-state for one package, for before/after-Verify diffing.
# ASCII only. Run elevated (some Content/metadata may be ACL'd).
#   .\capture-state.ps1 -Tag overlay
#   .\capture-state.ps1 -Tag verified
param(
  [Parameter(Mandatory=$true)][string]$Tag,
  [string]$GameDir = "F:\Games\Clone Drone in the Danger Zone",
  [string]$ContentGuid = "368B2C2C-C6E2-4472-ACDA-52A5F18A1D51",
  [string]$VolGuid = "{98053395-E234-4CF1-BF1E-3D8FBAF84537}",
  [string]$OutRoot = "F:\Documents\GamesLocalShare\PLANNING\xbox-validation\gs-state"
)

$ErrorActionPreference = 'Continue'
$dst = Join-Path $OutRoot $Tag
New-Item -ItemType Directory -Force -Path $dst | Out-Null
$report = Join-Path $dst "baseline.txt"
$lines = New-Object System.Collections.Generic.List[string]
function L([string]$s){ $lines.Add($s); Write-Output $s }

L "GS TRUST-STATE CAPTURE"
L ("Tag:     {0}" -f $Tag)
L ("When:    {0}" -f (Get-Date -Format o))
L ("GameDir: {0}" -f $GameDir)
L ("Content: {0}  Vol: {1}" -f $ContentGuid,$VolGuid)
L ("="*70)

# --- registry export (full GamingServices tree) ---
& reg export "HKLM\SOFTWARE\Microsoft\GamingServices" (Join-Path $dst "gamingservices.reg") /y | Out-Null
L ("reg export exit: {0}" -f $LASTEXITCODE)

# --- key per-package registry values ---
L ""
L "## PackageRepository Metadata ##"
$mp = "HKLM:\SOFTWARE\Microsoft\GamingServices\PackageRepository\Metadata\$VolGuid#{$ContentGuid}"
(Get-ItemProperty $mp -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object {$_.Name -notlike 'PS*'} | ForEach-Object { L ("  {0} = {1}" -f $_.Name,$_.Value) }
L "## Store ContentId ##"
(Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\GamingServices\Store\ContentId\{$ContentGuid}" -ErrorAction SilentlyContinue).PSObject.Properties | Where-Object {$_.Name -notlike 'PS*'} | ForEach-Object { L ("  {0} = {1}" -f $_.Name,$_.Value) }
L "## StreamingSummaries (sessions referencing this package) ##"
Get-ChildItem "HKLM:\SOFTWARE\Microsoft\GamingServices\StreamingSummaries" -ErrorAction SilentlyContinue | ForEach-Object {
  $gp = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
  $gp.PSObject.Properties | Where-Object { $_.Name -like "*$ContentGuid*" } | ForEach-Object { L ("  {0}\{1} = {2}" -f (Split-Path $gp.PSPath -Leaf),$_.Name,$_.Value) }
}
L "## StreamingCheckpoints / Tracking / Requests child counts ##"
foreach($k in 'StreamingCheckpoints','StreamingTracking','StreamingRequests'){ $n=(Get-ChildItem "HKLM:\SOFTWARE\Microsoft\GamingServices\$k" -ErrorAction SilentlyContinue | Measure-Object).Count; L ("  {0} = {1} children" -f $k,$n) }

# --- on-disk metadata files (hidden) ---
L ""
L "## Metadata files (size + SHA256) ##"
Get-ChildItem $GameDir -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$ContentGuid*" } | Sort-Object Name | ForEach-Object {
  $h = (Get-FileHash $_.FullName -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
  L ("  {0,12}  {1}  {2}" -f $_.Length,$h,$_.Name)
}

# --- .xvi residency bitmap as hex (small) ---
$xvi = Join-Path $GameDir "$ContentGuid.xvi"
if(Test-Path $xvi){
  $b = [System.IO.File]::ReadAllBytes($xvi)
  $hex = ($b | ForEach-Object { $_.ToString('x2') }) -join ''
  L ""
  L ("## .xvi ({0} bytes) hex ##" -f $b.Length)
  L $hex
}

# --- content sample (detects whether Verify rewrites content bytes / re-encrypts) ---
L ""
L "## Content sample (readable? size + SHA256) ##"
$content = Join-Path $GameDir "Content"
$sample = @(
  "Clone Drone in the Danger Zone.exe",
  "UnityCrashHandler64.exe",
  "gamelaunchhelper.exe",
  "UnityPlayer.dll",
  "MicrosoftGame.Config",
  "Clone Drone in the Danger Zone_Data\Managed\Assembly-CSharp.dll",
  "Clone Drone in the Danger Zone_Data\globalgamemanagers",
  "Clone Drone in the Danger Zone_Data\level0"
)
foreach($rel in $sample){
  $fp = Join-Path $content $rel
  if(Test-Path $fp){
    try {
      $fi = Get-Item $fp -Force -ErrorAction Stop
      $h = (Get-FileHash $fp -Algorithm SHA256 -ErrorAction Stop).Hash
      L ("  readable  {0,10}  {1}  {2}" -f $fi.Length,$h,$rel)
    } catch {
      $len = try { (Get-Item $fp -Force).Length } catch { '?' }
      L ("  UNREADABLE {0,10}  ACCESS-DENIED/PROTECTED  {1}" -f $len,$rel)
    }
  } else { L ("  MISSING                                                              {0}" -f $rel) }
}

$total = (Get-ChildItem $content -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum)
L ""
L ("## Content totals: {0} files, {1:N0} bytes ##" -f $total.Count,$total.Sum)

Set-Content -Path $report -Value $lines -Encoding ASCII
Write-Output ""
Write-Output ("Wrote {0}" -f $report)
