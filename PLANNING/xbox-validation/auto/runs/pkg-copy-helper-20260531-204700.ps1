param([Parameter(Mandatory)][string]$ArgsFile)
$ErrorActionPreference = 'SilentlyContinue'
$a = Get-Content -LiteralPath $ArgsFile -Raw | ConvertFrom-Json
$res = @()
foreach ($rel in @($a.Files)) {
    $src = Join-Path $a.GameFolder $rel
    $dst = Join-Path $a.DestGame   $rel
    $e = [ordered]@{ Path = $rel; Copied = $false; Header = ''; Size = 0; Error = '' }
    try {
        $fsIn  = [System.IO.File]::Open($src, 'Open', 'Read', 'ReadWrite')
        $len   = $fsIn.Length

        # Read the first 2 bytes (file header) for the diagnostic ID.
        $headerBuf = New-Object byte[] 2
        $headRead = 0
        if ($len -ge 2) {
            while ($headRead -lt 2) {
                $n = $fsIn.Read($headerBuf, $headRead, 2 - $headRead)
                if ($n -le 0) { break }
                $headRead += $n
            }
            $e.Header = ('{0:x2}{1:x2}' -f $headerBuf[0], $headerBuf[1])
        }
        $fsIn.Position = 0

        $dir = Split-Path -Parent $dst
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }

        $fsOut = [System.IO.File]::Open($dst, 'Create', 'Write', 'None')
        try {
            # 1 MB chunks â€” fast on NVMe, safe for >4 GB files.
            $fsIn.CopyTo($fsOut, 1048576)
        } finally {
            $fsOut.Flush()
            $fsOut.Close()
        }
        $fsIn.Close()

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
