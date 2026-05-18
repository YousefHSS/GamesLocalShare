$file = 'c:\laragon\www\GamesLocalShare\Services\InteropBridge.cs'
$lines = [System.IO.File]::ReadAllLines($file)

# Remove line 614 (the premature closing brace)
$lines = $lines[0..613] + $lines[614..($lines.Count-1)]

# Now add the closing brace after the sender commands (after line 641, now 640)
$insertIdx = 640
$lines = $lines[0..$insertIdx] + @('            }') + $lines[($insertIdx+1)..($lines.Count-1)]

[System.IO.File]::WriteAllLines($file, $lines)
Write-Host "Fixed switch closing brace"