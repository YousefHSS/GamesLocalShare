<#
  hashtree-oracle-check.ps1 — is a package's own hash tree usable as a VERIFICATION ORACLE?

  This is the gate on the whole cross-version reuse idea, and it needs exactly ONE package —
  no old version, no waiting for an update. Run it first.

  THE QUESTION

  Today a captured skeleton is only promoted when the rebuild is proven byte-identical to the
  genuine package (rebuilt SHA == the gsha recorded at capture). That proof needs the WHOLE
  genuine package, which is precisely the download we are trying to avoid on an update. So any
  design that fills gaps from local data (installed files, an older skeleton, anything) can
  never pass that gate.

  There is one possible replacement: the package carries Microsoft's own signed hash tree, and
  that tree commits to the ENCRYPTED ciphertext (XBOX-TRANSFER-MASTER-FINDINGS.md, proven
  2026-06-19 via `xvdtool -i`: header flags are `Encrypted` + `Data integrity enabled (uses hash
  tree)`). Since `--restore` already reproduces genuine ciphertext byte-for-byte from installed
  files + CIK, locally-produced bytes could be checked page-by-page against that tree instead —
  no full package needed.

  That only works if the tree actually functions as an oracle. The streaming-skeletonizer spike
  left this open: `-eu` on cached packages logged "XVC Data Hash Valid: False" on packages that
  were otherwise fine, and nobody followed it up. This script settles it.

  THE METHOD (the discriminator)

    1. Ask xvdtool to report the package's hash state.        -> pristine verdict
    2. Flip ONE byte deep in the user-data region.
    3. Ask again.                                             -> corrupted verdict
    4. Restore the byte.

  Reading the result:

    pristine VALID, corrupted INVALID -> ORACLE WORKS. The tree notices wrong bytes, so it can
                                         verify locally-produced ranges. Cross-version reuse is
                                         worth designing; proceed to fetch-cdn-package.ps1 +
                                         diff-packages.ps1 to size the win.
    both INVALID                      -> NOT USABLE as shipped. Matches the spike's observation.
                                         Either the check is broken/not implemented for XVC, or
                                         it needs key material it isn't getting. Nothing can be
                                         verified without the full package until that is fixed in
                                         the xvdtool fork — the reuse design is BLOCKED, and that
                                         includes merging an old skeleton into a new capture.
    both VALID                        -> the check is not looking at the data it claims to (it
                                         did not notice deliberate corruption). Useless as an
                                         oracle; treat as blocked, same as above.
    no hash line at all               -> the shipped XVDTool.exe does not report it. Needs a
                                         verify entry point added in the fork (D:\xvdtool). The
                                         full --help output is captured in the report so you can
                                         see what it does offer.

  SAFETY

  The byte flip is done IN PLACE by default and restored in a finally block, then read back and
  confirmed. The original offset and byte value are printed BEFORE the write and written to the
  report, so a crash mid-run is repairable by hand. Pass -WorkCopy <path> to copy the package
  first and corrupt the copy instead (needs free space equal to the package). Pass -InfoOnly to
  do no writing at all.

  USAGE
    hashtree-oracle-check.ps1 -Package <pkg.msixvc> [-CikFolder <dir>] [-XvdTool <XVDTool.exe>]
                              [-CorruptOffset <n>] [-WorkCopy <path>] [-InfoOnly]
                              [-InfoArgs -i] [-OutFile <report.txt>]

  Defaults: -XvdTool  <repo>\tools\xvdtool\XVDTool.exe
            -CikFolder %LOCALAPPDATA%\GamesLocalShare\xbox-skeleton\cik  (if it exists)
            -OutFile   .\hashtree-oracle-<package>-<timestamp>.txt

  Exit codes: 0 oracle works | 1 error | 2 bad args | 3 blocked (unusable) | 4 inconclusive
#>
param(
  [Parameter(Mandatory)][string]$Package,
  [string]$XvdTool,
  [string]$CikFolder,
  [long]$CorruptOffset = -1,
  [string]$WorkCopy,
  [switch]$InfoOnly,
  [string[]]$InfoArgs,
  [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$report = New-Object System.Text.StringBuilder

function Say([string]$line) {
  Write-Host $line
  [void]$report.AppendLine($line)
}
# NB: $ErrorActionPreference is Stop, which makes Write-Error terminate on the spot — the exit
# code after it would never run. Report failures through this instead.
function Fail([string]$message, [int]$code) {
  Say ''
  Say ("ERROR: " + $message)
  try { Set-Content -LiteralPath $OutFile -Value $report.ToString() } catch { }
  exit $code
}
function Section([string]$title) {
  Say ''
  Say ('== ' + $title + ' ' + ('=' * [Math]::Max(0, 68 - $title.Length)))
}

# ---- locate inputs ---------------------------------------------------------

if (-not (Test-Path -LiteralPath $Package)) { Fail "Package not found: $Package" 2 }
$Package = (Resolve-Path -LiteralPath $Package).Path

if (-not $XvdTool) {
  # <repo>\PLANNING\xbox-validation\update-reuse\ -> <repo>\tools\xvdtool\XVDTool.exe
  $repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
  $XvdTool = Join-Path $repo 'tools\xvdtool\XVDTool.exe'
}
if (-not (Test-Path -LiteralPath $XvdTool)) {
  Fail "XVDTool.exe not found: $XvdTool  (pass -XvdTool <path>)" 2
}
$XvdTool = (Resolve-Path -LiteralPath $XvdTool).Path

if (-not $CikFolder) {
  $c = Join-Path $env:LOCALAPPDATA 'GamesLocalShare\xbox-skeleton\cik'
  if (Test-Path -LiteralPath $c) { $CikFolder = $c }
}
if ($CikFolder -and -not (Test-Path -LiteralPath $CikFolder)) {
  Fail "CIK folder not found: $CikFolder" 2
}
if (-not $InfoArgs -or $InfoArgs.Count -eq 0) { $InfoArgs = @('-i') }

if (-not $OutFile) {
  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  $OutFile = Join-Path (Get-Location) ("hashtree-oracle-" + [IO.Path]::GetFileNameWithoutExtension($Package) + "-$stamp.txt")
}

# Operate on a copy when asked, so the real package is never written to.
$target = $Package
if ($WorkCopy) {
  Say "copying package -> $WorkCopy  (this takes a while for a large package)"
  Copy-Item -LiteralPath $Package -Destination $WorkCopy -Force
  $target = (Resolve-Path -LiteralPath $WorkCopy).Path
}

$len = (Get-Item -LiteralPath $target).Length

Section 'inputs'
Say ("package    : " + $Package)
Say ("size       : " + $len.ToString('N0') + " bytes (" + ($len / 1GB).ToString('F2') + " GB)")
if ($WorkCopy) { Say ("work copy  : " + $target) }
Say ("xvdtool    : " + $XvdTool)
Say ("cik folder : " + $(if ($CikFolder) { $CikFolder } else { '(none — info may not be able to decrypt)' }))
Say ("info args  : " + ($InfoArgs -join ' '))

# ---- helpers ---------------------------------------------------------------

function Invoke-Xvd([string[]]$toolArgs) {
  $out = ''
  try   { $out = (& $XvdTool @toolArgs 2>&1 | Out-String) }
  catch { $out = "(launch failed: $_)" }
  return $out
}

function Get-Info([string]$pkgPath) {
  $a = @()
  $a += $InfoArgs
  if ($CikFolder) { $a += @('--cikfolder', $CikFolder) }
  $a += $pkgPath
  $out = Invoke-Xvd $a
  # Some builds spell it --info rather than -i; retry once if we clearly got nothing useful.
  if (($out -notmatch '\S') -or ($out -match 'nknown (option|argument)')) {
    $b = @('--info')
    if ($CikFolder) { $b += @('--cikfolder', $CikFolder) }
    $b += $pkgPath
    $alt = Invoke-Xvd $b
    if ($alt -match '\S') { return $alt }
  }
  return $out
}

# Lines that carry a hash verdict — matched loosely, because the exact wording lives in the
# xvdtool fork and has changed before ("XVC Data Hash Valid: False", "Data hash tree valid", …).
function Get-HashVerdictLines([string]$text) {
  @($text -split "`r?`n" | Where-Object {
      $_ -match '(?i)hash' -and $_ -match '(?i)(valid|invalid|true|false|match|mismatch|ok|fail)'
    } | ForEach-Object { $_.Trim() })
}

function Invoke-ByteFlip([string]$path, [long]$offset) {
  $fs = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
  try {
    $fs.Position = $offset
    $orig = $fs.ReadByte()
    if ($orig -lt 0) { throw "could not read byte at offset $offset" }
    $fs.Position = $offset
    $fs.WriteByte([byte](($orig -bxor 0xFF) -band 0xFF))
    $fs.Flush($true)
    return $orig
  } finally { $fs.Dispose() }
}

function Set-Byte([string]$path, [long]$offset, [int]$value) {
  $fs = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
  try {
    $fs.Position = $offset
    $fs.WriteByte([byte]$value)
    $fs.Flush($true)
  } finally { $fs.Dispose() }
}

function Get-Byte([string]$path, [long]$offset) {
  $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'Read')
  try { $fs.Position = $offset; return $fs.ReadByte() } finally { $fs.Dispose() }
}

# ---- 1. what does this build even offer? -----------------------------------

Section 'xvdtool --help (captured for the record)'
Say (Invoke-Xvd @('--help'))

# ---- 2. pristine verdict ---------------------------------------------------

Section 'info: PRISTINE package'
$pristineOut = Get-Info $target
Say $pristineOut

$pristineHash = Get-HashVerdictLines $pristineOut
Section 'hash verdict lines (pristine)'
if ($pristineHash.Count -eq 0) { Say '(none found)' } else { $pristineHash | ForEach-Object { Say ('  ' + $_) } }

if ($InfoOnly) {
  Section 'result'
  Say 'INFO ONLY — no byte was written, so no discrimination test was run.'
  Set-Content -LiteralPath $OutFile -Value $report.ToString()
  Say ("report written: " + $OutFile)
  exit 4
}

# ---- 3. corrupt one byte, ask again ----------------------------------------

# Default deep into user data (75% in), well past the header + hash tree region, so a hash check
# that covers data has something to catch and one that only checks the header does not.
if ($CorruptOffset -lt 0) { $CorruptOffset = [long]($len * 0.75) }
if ($CorruptOffset -ge $len) { Fail "CorruptOffset $CorruptOffset is past EOF ($len)" 2 }

Section 'discrimination test'
$origByte = $null
$corruptHash = @()
$failure = $null
try {
  Say ("flipping ONE byte at offset {0:N0} in: {1}" -f $CorruptOffset, $target)
  $origByte = Invoke-ByteFlip $target $CorruptOffset
  Say ("original byte value: 0x{0:X2}  (restore this if the script dies before it does)" -f $origByte)

  $corruptOut = Get-Info $target
  Section 'info: CORRUPTED package'
  Say $corruptOut
  $corruptHash = Get-HashVerdictLines $corruptOut
  Section 'hash verdict lines (corrupted)'
  if ($corruptHash.Count -eq 0) { Say '(none found)' } else { $corruptHash | ForEach-Object { Say ('  ' + $_) } }
}
catch {
  # Most likely cause: the package is open by the running app / cache proxy, so it can't be written.
  $failure = $_
}
finally {
  if ($null -ne $origByte) {
    Set-Byte $target $CorruptOffset $origByte
    $check = Get-Byte $target $CorruptOffset
    if ($check -eq $origByte) {
      Say ("restored byte at {0:N0} (verified 0x{1:X2})" -f $CorruptOffset, $check)
    } else {
      Say ("!! RESTORE FAILED at {0:N0} — expected 0x{1:X2}, found 0x{2:X2}" -f $CorruptOffset, $origByte, $check)
      Say  '!! Repair it manually before using this package.'
    }
  }
}

# ---- 4. verdict ------------------------------------------------------------

Section 'RESULT'
$exit = 4

if ($failure) {
  Say ("ERROR — the discrimination test could not run: {0}" -f $failure)
  Say 'If the package is locked, close GamesLocalShare (and stop the cache proxy) and re-run, or'
  Say 'use -WorkCopy <path> to test a copy instead.'
  Set-Content -LiteralPath $OutFile -Value $report.ToString()
  Write-Host ''
  Write-Host ("report written: " + $OutFile)
  exit 1
}
$pj = ($pristineHash -join "`n")
$cj = ($corruptHash  -join "`n")

if ($pristineHash.Count -eq 0 -and $corruptHash.Count -eq 0) {
  Say 'INCONCLUSIVE — this XVDTool build reports no hash verdict at all.'
  Say 'The tree may still be fine; the tool simply does not expose a check. Next step: add a'
  Say 'verify entry point in the xvdtool fork (D:\xvdtool, LibXboxOne) and re-run. The --help'
  Say 'output above shows what this build does offer.'
  $exit = 4
}
elseif ($pj -ne $cj) {
  Say 'ORACLE WORKS — the hash verdict CHANGED when one byte was corrupted.'
  Say 'The tree notices wrong bytes, so it can verify ranges we produce locally without ever'
  Say 'holding the whole genuine package. This unblocks the reuse design (and, with it, any'
  Say 'merge of an old skeleton into a new capture).'
  Say ''
  Say 'Next: fetch-cdn-package.ps1 to pull an older version, then diff-packages.ps1 to size'
  Say 'how much of the package is reusable at the same offset.'
  $exit = 0
}
elseif ($pj -match '(?i)(false|invalid|mismatch|fail)') {
  Say 'BLOCKED — the hash reads INVALID even on the pristine package, and corruption changed nothing.'
  Say 'This reproduces the streaming-skeletonizer spike observation ("XVC Data Hash Valid: False").'
  Say 'As shipped there is no way to verify locally-produced bytes, so gap-filling from the install'
  Say 'or merging an old skeleton cannot be promoted safely. Fix the check in the xvdtool fork'
  Say '(is it getting the key material it needs? is it checking the right region?) before designing'
  Say 'anything on top of it.'
  $exit = 3
}
else {
  Say 'BLOCKED — the hash reads the same (and positive) whether or not the data is corrupt.'
  Say 'The check did not notice a deliberately flipped byte, so it is not looking at the data it'
  Say 'claims to and is useless as an oracle. Try -CorruptOffset inside a region you know is'
  Say 'user data; if it still does not react, the check needs fixing in the fork.'
  $exit = 3
}

Set-Content -LiteralPath $OutFile -Value $report.ToString()
Write-Host ''
Write-Host ("report written: " + $OutFile)
exit $exit
