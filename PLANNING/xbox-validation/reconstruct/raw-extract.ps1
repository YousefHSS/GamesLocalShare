<#
  raw-extract.ps1 - read a file's TRUE on-disk bytes (raw from the volume, under the filter)
  and write them to an output file. Handles multiple extents; truncates to real file size.
  Run ELEVATED. ASCII only. PS 5.1.
    .\raw-extract.ps1 -File "F:\...\x.smd" -Out "F:\out\x.smd"
#>
param(
  [Parameter(Mandatory=$true)][string]$File,
  [Parameter(Mandatory=$true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "ERROR: run elevated." -ForegroundColor Red; return }

$fsz = [System.IO.File]::Open($File, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite); $size = $fsz.Length; $fsz.Close()
$vol = (Split-Path $File -Qualifier)
$ntfs = & fsutil fsinfo ntfsinfo $vol
$cluster = [int]([regex]::Match(($ntfs -join "`n"), 'Bytes Per Cluster\s*:\s*([0-9]+)').Groups[1].Value)
if ($cluster -le 0) { $cluster = 4096 }

# parse extents: lines "VCN: 0x..  Clusters: 0x..  LCN: 0x.."
$ext = & fsutil file queryextents "$File"
$extents = @()
foreach ($l in $ext) {
  $m = [regex]::Match($l.ToString(), 'VCN:\s*0x([0-9a-fA-F]+)\s+Clusters:\s*0x([0-9a-fA-F]+)\s+LCN:\s*0x([0-9a-fA-F]+)')
  if ($m.Success) {
    $extents += [pscustomobject]@{
      VCN      = [Convert]::ToInt64($m.Groups[1].Value,16)
      Clusters = [Convert]::ToInt64($m.Groups[2].Value,16)
      LCN      = [Convert]::ToInt64($m.Groups[3].Value,16)
    }
  }
}
if ($extents.Count -eq 0) { Write-Host "ERROR: no extents (file may be MFT-resident); cannot raw-read." -ForegroundColor Red; return }

New-Item -ItemType Directory -Force -Path (Split-Path $Out -Parent) | Out-Null
$volH = [System.IO.File]::Open("\\.\$vol", [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$outFs = [System.IO.File]::Create($Out)
$written = [int64]0
try {
  foreach ($e in ($extents | Sort-Object VCN)) {
    $volH.Position = [int64]$e.LCN * [int64]$cluster
    $toRead = [int64]$e.Clusters * [int64]$cluster
    $buf = New-Object byte[] ([int][Math]::Min($toRead, 1048576))
    $rem = $toRead
    while ($rem -gt 0) {
      $chunk = [int][Math]::Min($buf.Length, $rem)
      $n = $volH.Read($buf, 0, $chunk)
      if ($n -le 0) { break }
      $outFs.Write($buf, 0, $n); $written += $n; $rem -= $n
    }
  }
} finally { $volH.Close(); $outFs.Close() }

# truncate to real file size (last cluster has slack)
if ($written -gt $size) {
  $fs = [System.IO.File]::Open($Out, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write); $fs.SetLength($size); $fs.Close()
}
Write-Host ("raw-extracted {0} -> {1}  ({2} bytes, {3} extents)" -f $File, $Out, $size, $extents.Count) -ForegroundColor Green