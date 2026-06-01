# Executable-aware fingerprint for an MSIXVC install.
# Records, for the content-protected executables AND a few control files:
#   exists / size / readable? / MZ header? / SHA256 (when readable)
# A genuine Store install keeps the protected EXEs encrypted-at-rest
# (Access Denied to a normal reader); our overlay leaves them plaintext MZ.
# This is the file-level signal the streaming-metadata fingerprint missed.
#
# Usage (run as Administrator):
#   .\fp-exes.ps1 -GameDir "F:\Games\Clone Drone in the Danger Zone" -Out fp-exes-clean.txt
#   .\fp-exes.ps1 -GameDir "C:\XboxGames\Clone Drone in the Danger Zone" -Out fp-exes-overlay.txt
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDir,
    [string]$Out,
    # Relative-to-GameDir paths. Defaults = Clone Drone's protected EXEs + controls.
    [string[]]$Protected = @(
        'Content\Clone Drone in the Danger Zone.exe',
        'Content\UnityCrashHandler64.exe'
    ),
    [string[]]$Controls = @(
        'Content\gamelaunchhelper.exe',
        'Content\UnityPlayer.dll',
        'Content\Clone Drone in the Danger Zone_Data\Managed\Assembly-CSharp.dll',
        'Content\MicrosoftGame.Config'
    )
)

$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
$L = New-Object System.Collections.Generic.List[string]
function W($s){ $L.Add([string]$s); Write-Host $s }

W "EXE-AWARE FINGERPRINT"
W ("Host:    " + $env:COMPUTERNAME)
W ("When:    " + (Get-Date).ToString('o'))
W ("GameDir: " + $GameDir)
W "==================================================================="

function Probe($rel, $tag){
    $p = Join-Path $GameDir $rel
    $name = $rel.Split('\')[-1]
    if (-not (Test-Path -LiteralPath $p)) { W ("  [{0}] {1,-40} MISSING" -f $tag,$name); return }
    $len = (Get-Item -LiteralPath $p -Force).Length
    $readable = $false; $mz = $false; $sha = ''; $err = ''
    try {
        $fs = [System.IO.File]::Open($p,'Open','Read','ReadWrite')
        $hdr = New-Object byte[] 2
        [void]$fs.Read($hdr,0,2)
        $mz = ($hdr[0] -eq 0x4D -and $hdr[1] -eq 0x5A)
        $fs.Position = 0
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $sha = ([BitConverter]::ToString($sha256.ComputeHash($fs))).Replace('-','')
        $fs.Close()
        $readable = $true
    } catch { $err = $_.Exception.Message }
    if ($readable) {
        W ("  [{0}] {1,-40} {2,11} bytes  readable  MZ={3,-5} sha={4}" -f $tag,$name,$len,$mz,$sha)
    } else {
        $short = if ($err -match 'denied') { 'ACCESS-DENIED (protected at rest)' } else { $err }
        W ("  [{0}] {1,-40} {2,11} bytes  UNREADABLE  {3}" -f $tag,$name,$len,$short)
    }
}

W ""
W "## Protected executables (the transfer-sensitive files) ##"
foreach($f in $Protected){ Probe $f 'PROT' }
W ""
W "## Control files (should be identical/readable in both) ##"
foreach($f in $Controls){ Probe $f 'CTRL' }

if ($Out) {
    [System.IO.File]::WriteAllLines($Out, $L)
    Write-Host ("`nWrote " + $Out + " (" + $L.Count + " lines)") -ForegroundColor Green
}
