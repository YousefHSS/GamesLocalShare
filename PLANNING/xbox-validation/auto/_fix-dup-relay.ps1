$file = 'c:\laragon\www\GamesLocalShare\ViewModels\MainViewModel.cs'
$lines = Get-Content $file

# Remove standalone [RelayCommand] lines (not followed by a method)
$newLines = @()
$i = 0
while ($i -lt $lines.Count) {
    $line = $lines[$i]
    if ($line -match '^\s*\[RelayCommand\]\s*$') {
        # Check if next line is a method declaration
        if ($i + 1 -lt $lines.Count -and $lines[$i+1] -match 'private (async Task|void)') {
            # Keep it, it's valid
            $newLines += $line
        } else {
            # Skip it, it's duplicate
            Write-Host "Removed duplicate [RelayCommand] at line $($i+1)"
        }
    } else {
        $newLines += $line
    }
    $i++
}

$newLines | Set-Content $file
Write-Host "DONE"