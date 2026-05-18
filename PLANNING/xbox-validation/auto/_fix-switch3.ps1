$file = 'c:\laragon\www\GamesLocalShare\Services\InteropBridge.cs'
$lines = [System.IO.File]::ReadAllLines($file)

# Remove line 614 (premature switch closing brace)
$lines = $lines[0..613] + $lines[614..($lines.Count-1)]

# Add the switch closing brace after BrowseXboxDestination break (now line 640)
$insertIdx = 640
$lines = $lines[0..$insertIdx] + @('                }') + $lines[($insertIdx+1)..($lines.Count-1)]

[System.IO.File]::WriteAllLines($file, $lines)
Write-Host "Fixed: moved switch closing brace to after sender commands"