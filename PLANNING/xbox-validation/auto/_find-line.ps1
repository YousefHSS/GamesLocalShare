$lines = Get-Content 'c:\laragon\www\GamesLocalShare\Services\InteropBridge.cs'
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'BrowseXboxSource') {
        Write-Host "Line $($i+1): $($lines[$i])"
        Write-Host "Line $($i+2): $($lines[$i+1])"
        Write-Host "Line $($i+3): $($lines[$i+2])"
        break
    }
}