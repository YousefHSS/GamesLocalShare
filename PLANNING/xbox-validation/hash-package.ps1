# Hashes every file under -Root and writes a manifest:
#   relative_path<TAB>sha256<TAB>size<TAB>mtime_utc
# Usage:
#   .\hash-package.ps1 -Root "F:\Backup\Forza Horizon 6" -Out "manifest-A.tsv"

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Out
)

if (-not (Test-Path -LiteralPath $Root)) { throw "Root not found: $Root" }
$Root = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')

Write-Host "Enumerating files under $Root ..."
$files = Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue
$total = $files.Count
$totalBytes = ($files | Measure-Object Length -Sum).Sum
Write-Host "Files: $total | Total: $([math]::Round($totalBytes/1GB,2)) GB"

$sha = [System.Security.Cryptography.SHA256]::Create()
$buf = New-Object byte[] (4MB)
$writer = [System.IO.StreamWriter]::new($Out, $false, [System.Text.Encoding]::UTF8)
$writer.WriteLine("# root=$Root")
$writer.WriteLine("# generated=$([DateTime]::UtcNow.ToString('o'))")
$writer.WriteLine("# columns=relative_path<TAB>sha256<TAB>size<TAB>mtime_utc")

$i = 0
$bytesDone = 0L
$start = Get-Date
try {
    foreach ($f in $files) {
        $i++
        $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
        try {
            $fs = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
            $sha.Initialize()
            while (($read = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                [void]$sha.TransformBlock($buf, 0, $read, $null, 0)
            }
            [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
            $hash = [BitConverter]::ToString($sha.Hash).Replace('-','').ToLowerInvariant()
            $fs.Dispose()
            $mtime = $f.LastWriteTimeUtc.ToString('o')
            $writer.WriteLine("$rel`t$hash`t$($f.Length)`t$mtime")
        } catch {
            $writer.WriteLine("$rel`tERROR:$($_.Exception.Message -replace '[\t\r\n]',' ')`t$($f.Length)`t")
        }
        $bytesDone += $f.Length
        if ($i % 50 -eq 0 -or $i -eq $total) {
            $elapsed = (Get-Date) - $start
            $mbps = if ($elapsed.TotalSeconds -gt 0) { [math]::Round(($bytesDone/1MB)/$elapsed.TotalSeconds,1) } else { 0 }
            $pct  = if ($totalBytes -gt 0) { [math]::Round(100*$bytesDone/$totalBytes,1) } else { 0 }
            Write-Progress -Activity "Hashing $Root" -Status "$i / $total  |  $pct%  |  $mbps MB/s" -PercentComplete $pct
        }
    }
} finally {
    $writer.Dispose()
    $sha.Dispose()
}

$elapsed = (Get-Date) - $start
Write-Host "Done. Wrote $Out in $([math]::Round($elapsed.TotalMinutes,1)) min."
