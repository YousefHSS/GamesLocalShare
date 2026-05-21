param([Parameter(Mandatory)][string]$ArgsFile)
$ErrorActionPreference = 'SilentlyContinue'
$a = Get-Content -LiteralPath $ArgsFile -Raw | ConvertFrom-Json
$res = @()
foreach ($rel in @($a.Files)) {
    $src = Join-Path $a.GameFolder $rel
    $dst = Join-Path $a.DestGame   $rel
    $e = [ordered]@{ Path = $rel; Copied = $false; Header = ''; Size = 0; Error = '' }
    try {
        $fs  = [System.IO.File]::Open($src, 'Open', 'Read', 'ReadWrite')
        $len = $fs.Length
        $buf = New-Object byte[] $len
        $off = 0
        while ($off -lt $len) {
            $n = $fs.Read($buf, $off, $len - $off)
            if ($n -le 0) { break }
            $off += $n
        }
        $fs.Close()
        $dir = Split-Path -Parent $dst
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        [System.IO.File]::WriteAllBytes($dst, $buf)
        if ($len -ge 2) { $e.Header = ('{0:x2}{1:x2}' -f $buf[0], $buf[1]) }
        $e.Size   = $len
        $e.Copied = $true
    } catch {
        $e.Error = "$_"
    }
    $res += [pscustomobject]$e
}
[pscustomobject]@{
    Identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    Files    = $res
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $a.ResultPath -Encoding UTF8
