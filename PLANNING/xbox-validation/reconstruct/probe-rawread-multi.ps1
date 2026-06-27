<#
  probe-rawread-multi.ps1 - map which installed files are PLAINTEXT vs ENCRYPTED at rest.
  For each target: normal (through-filter) read of first N bytes vs RAW volume read of the same
  physical bytes. SAME => plaintext at rest. DIFFERENT => ciphertext at rest. Also flags files
  whose normal read is ACCESS-DENIED (protected). Run ELEVATED. ASCII only. PS 5.1.
#>
param(
  [string]$GameDir = "F:\Games\Clone Drone in the Danger Zone",
  [int]$Bytes = 65536
)
$ErrorActionPreference = 'Continue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$vol = (Split-Path $GameDir -Qualifier)
$ntfs = & fsutil fsinfo ntfsinfo $vol
$cluster = [int]([regex]::Match(($ntfs -join "`n"), 'Bytes Per Cluster\s*:\s*([0-9]+)').Groups[1].Value)
if ($cluster -le 0) { $cluster = 4096 }

$sha = New-Object System.Security.Cryptography.SHA256Managed
function HashHex($b,$n){ if($n -le 0){return '----------------'}; ([BitConverter]::ToString($sha.ComputeHash($b,0,$n)) -replace '-','').Substring(0,16) }

$targets = @(
  "Content\UnityPlayer.dll",
  "Content\Clone Drone in the Danger Zone_Data\resources.resource",
  "Content\Clone Drone in the Danger Zone_Data\sharedassets1.assets",
  "Content\Clone Drone in the Danger Zone_Data\globalgamemanagers",
  "Content\Clone Drone in the Danger Zone_Data\Managed\Assembly-CSharp.dll",
  "Content\Clone Drone in the Danger Zone.exe",
  "Content\UnityCrashHandler64.exe",
  "368B2C2C-C6E2-4472-ACDA-52A5F18A1D51",
  "368B2C2C-C6E2-4472-ACDA-52A5F18A1D51.smd",
  "368B2C2C-C6E2-4472-ACDA-52A5F18A1D51.xvi"
)

$volH = [System.IO.File]::Open("\\.\$vol", [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
Write-Host ("{0,-52} {1,-8} {2,-16} {3,-16} {4,-8} {5}" -f 'file','normal','normalHash','rawHash','match','rawNonZero/firstbytes') -ForegroundColor Cyan
foreach ($rel in $targets) {
  $f = Join-Path $GameDir $rel
  $name = if ($rel.Length -gt 50) { '...' + $rel.Substring($rel.Length-47) } else { $rel }
  if (-not (Test-Path -LiteralPath $f)) { Write-Host ("{0,-52} MISSING" -f $name) -ForegroundColor DarkGray; continue }

  # normal read
  $nOk = $true; $bN = New-Object byte[] $Bytes; $nN = 0
  try { $fn = [System.IO.File]::Open($f, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite); $nN = $fn.Read($bN,0,$Bytes); $fn.Close() }
  catch { $nOk = $false }
  $hN = if ($nOk) { HashHex $bN $nN } else { 'ACCESS-DENIED' }

  # raw read from first extent
  $ext = & fsutil file queryextents "$f" 2>$null
  $line0 = ($ext | Select-String -Pattern 'LCN' | Select-Object -First 1)
  $hR = 'no-extent'; $nz = 0; $fb = ''
  if ($line0) {
    $lcnHex = ([regex]::Match($line0.ToString(), 'LCN:\s*0x([0-9a-fA-F]+)')).Groups[1].Value
    if ($lcnHex) {
      $off = [int64]([Convert]::ToInt64($lcnHex,16)) * [int64]$cluster
      try {
        $volH.Position = $off
        $bR = New-Object byte[] $Bytes; $nR = 0
        while ($nR -lt $Bytes) { $r = $volH.Read($bR,$nR,$Bytes-$nR); if ($r -le 0){break}; $nR += $r }
        $hR = HashHex $bR $nR
        for ($i=0;$i -lt $nR;$i++){ if($bR[$i] -ne 0){$nz++} }
        $fb = [BitConverter]::ToString($bR,0,8)
      } catch { $hR = 'RAW-ERR' }
    }
  }
  $match = if ($nOk -and $hN -eq $hR) { 'SAME' } elseif ($nOk) { 'DIFF' } else { '-' }
  $color = if ($match -eq 'SAME') { 'Yellow' } elseif ($match -eq 'DIFF') { 'Green' } else { 'Gray' }
  Write-Host ("{0,-52} {1,-8} {2,-16} {3,-16} {4,-8} {5} {6}" -f $name, $(if($nOk){'ok'}else{'DENIED'}), $hN, $hR, $match, $nz, $fb) -ForegroundColor $color
}
$volH.Close()
Write-Host "`nSAME=plaintext at rest, DIFF=ciphertext at rest, DENIED=protected (cannot normal-read)" -ForegroundColor Cyan