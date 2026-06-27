<#
  probe-rawread.ps1 - Part-1 feasibility probe for rebuilding a .msixvc from an installed game.

  Question: are the installed content files stored ENCRYPTED at rest (ciphertext on disk, the
  filter driver decrypts on read), and can we READ that ciphertext by reading the raw volume
  underneath the filter?

  Method: for a target file, hash the first N bytes read the NORMAL way (through the filter ->
  decrypted) vs the SAME bytes read RAW from the volume (\\.\<vol> at the file's physical
  cluster). DIFFER => at-rest encryption confirmed AND raw ciphertext is reachable (Part 1 OK).
  SAME  => file is plaintext on disk (no at-rest encryption for it) -> nothing to reconstruct.

  Run ELEVATED (raw volume read needs admin). ASCII only. PS 5.1.
#>
param(
  [string]$File = "F:\Games\Clone Drone in the Danger Zone\Content\UnityPlayer.dll",
  [int]$Bytes = 65536
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated (raw volume read needs admin)." -ForegroundColor Red; return }
if (-not (Test-Path -LiteralPath $File)) { Write-Host "ERROR: file not found: $File" -ForegroundColor Red; return }

function Hash16($bytes, $count) {
  $sha = New-Object System.Security.Cryptography.SHA256Managed
  ([BitConverter]::ToString($sha.ComputeHash($bytes, 0, $count)) -replace '-','').Substring(0,16)
}
function NonZero($bytes, $count) { $c = 0; for ($i=0; $i -lt $count; $i++) { if ($bytes[$i] -ne 0) { $c++ } }; $c }

Write-Host "=== Part-1 raw-read probe ===" -ForegroundColor Cyan
Write-Host ("File : {0}" -f $File)
Write-Host ("Bytes: {0}" -f $Bytes)

# --- normal (through-filter) read ---
$fn = [System.IO.File]::Open($File, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$bufN = New-Object byte[] $Bytes
$nN = $fn.Read($bufN, 0, $Bytes); $fn.Close()
$hN = Hash16 $bufN $nN
Write-Host ("`nNORMAL read: {0} bytes  sha256[0:16]={1}  firstbytes={2}" -f $nN, $hN, ([BitConverter]::ToString($bufN,0,8)))

# --- volume + cluster size ---
$vol = (Split-Path $File -Qualifier)   # e.g. "F:"
$ntfs = & fsutil fsinfo ntfsinfo $vol
$cluster = [int]([regex]::Match(($ntfs -join "`n"), 'Bytes Per Cluster\s*:\s*([0-9]+)').Groups[1].Value)
if ($cluster -le 0) { $cluster = 4096 }
Write-Host ("`nVolume {0}  bytes/cluster={1}" -f $vol, $cluster)

# --- physical extents of the file ---
$ext = & fsutil file queryextents "$File"
$line0 = ($ext | Select-String -Pattern 'LCN' | Select-Object -First 1).ToString()
Write-Host ("First extent line: {0}" -f $line0.Trim())
# parse "VCN: 0x.. Clusters: 0x.. LCN: 0x.."
$lcnHex = ([regex]::Match($line0, 'LCN:\s*0x([0-9a-fA-F]+)')).Groups[1].Value
if (-not $lcnHex) { Write-Host "ERROR: could not parse LCN from queryextents." -ForegroundColor Red; return }
$lcn = [Convert]::ToInt64($lcnHex, 16)
$physOff = [int64]$lcn * [int64]$cluster
Write-Host ("First extent LCN=0x{0} (={1})  -> physical offset {2} bytes" -f $lcnHex, $lcn, $physOff)

# --- raw volume read at the physical offset (aligned to cluster) ---
$h = [System.IO.File]::Open("\\.\$vol", [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$h.Position = $physOff
$bufR = New-Object byte[] $Bytes
$nR = 0
while ($nR -lt $Bytes) { $r = $h.Read($bufR, $nR, $Bytes - $nR); if ($r -le 0) { break }; $nR += $r }
$h.Close()
$hR = Hash16 $bufR $nR
$nz = NonZero $bufR $nR
Write-Host ("RAW    read: {0} bytes  sha256[0:16]={1}  firstbytes={2}  nonzero={3}" -f $nR, $hR, ([BitConverter]::ToString($bufR,0,8)), $nz)

Write-Host ""
if ($hN -eq $hR) {
  Write-Host "RESULT: SAME -> on-disk bytes already match decrypted bytes. File is PLAINTEXT at rest." -ForegroundColor Yellow
  Write-Host "        (No at-rest encryption for this file; nothing to 'recover' as ciphertext.)" -ForegroundColor Yellow
} else {
  Write-Host "RESULT: DIFFERENT -> on-disk bytes are CIPHERTEXT (filter decrypts on normal read)." -ForegroundColor Green
  Write-Host "        At-rest encryption confirmed AND raw ciphertext is reachable -> Part 1 FEASIBLE." -ForegroundColor Green
}