param([Parameter(Mandatory)][string]$ContentDir)
$files = @(
  'Clone Drone in the Danger Zone.exe',
  'gamelaunchhelper.exe',
  'UnityCrashHandler64.exe',
  'UnityPlayer.dll',
  'Clone Drone in the Danger Zone_Data\Managed\Assembly-CSharp.dll',
  'Clone Drone in the Danger Zone_Data\Managed\mscorlib.dll',
  'MicrosoftGame.Config',
  'appxmanifest.xml'
)
foreach ($f in $files) {
  $p = Join-Path $ContentDir $f
  if (-not (Test-Path -LiteralPath $p)) { Write-Host ("MISSING  " + $f); continue }
  try {
    $fs = [System.IO.File]::Open($p, 'Open', 'Read', 'ReadWrite')
    $b = New-Object byte[] 16
    [void]$fs.Read($b, 0, 16)
    $fs.Close()
    $hex = ($b | ForEach-Object { $_.ToString('x2') }) -join ' '
    $asc = -join ($b | ForEach-Object { if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '.' } })
    $len = (Get-Item -LiteralPath $p).Length
    $name = $f.Split('\')[-1]
    $isMZ = ($b[0] -eq 0x4D -and $b[1] -eq 0x5A)
    Write-Host ("{0,-32} {1,11}  MZ={2,-5}  {3}  |{4}|" -f $name, $len, $isMZ, $hex, $asc)
  } catch {
    Write-Host ("READ-ERR " + $f + "  ::  " + $_.Exception.Message)
  }
}
