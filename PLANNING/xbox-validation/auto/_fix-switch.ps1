$file = 'c:\laragon\www\GamesLocalShare\Services\InteropBridge.cs'
$lines = [System.IO.File]::ReadAllLines($file)

# Remove the incorrectly placed sender commands (lines 614-642)
# Keep lines 0-613, then skip to 643+
$lines = $lines[0..613] + $lines[643..($lines.Count-1)]

# Now find BrowseXboxSource break and insert sender commands before the switch closing brace
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'case "BrowseXboxSource":') {
        for ($j = $i; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim() -eq 'break;' -and $j -gt $i + 2) {
                $insertIdx = $j
                break
            }
        }
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: BrowseXboxSource break not found"; exit 1 }

$senderCommands = @(
    ''
    '                // Xbox sender commands'
    '                case "StartXboxStage":'
    '                    if (payload?.TryGetProperty("sourcePath", out var stageSourceEl) == true)'
    '                    {'
    '                        var sourcePath = stageSourceEl.GetString() ?? "";'
    '                        if (_viewModel.StartXboxStageCommand.CanExecute(sourcePath))'
    '                            await _viewModel.StartXboxStageCommand.ExecuteAsync(sourcePath);'
    '                    }'
    '                    break;'
    ''
    '                case "CompleteXboxStage":'
    '                    if (payload?.TryGetProperty("destinationPath", out var stageDestEl) == true)'
    '                    {'
    '                        var destPath = stageDestEl.GetString() ?? "";'
    '                        if (_viewModel.CompleteXboxStageCommand.CanExecute(destPath))'
    '                            await _viewModel.CompleteXboxStageCommand.ExecuteAsync(destPath);'
    '                    }'
    '                    break;'
    ''
    '                case "CancelXboxStage":'
    '                    if (_viewModel.CancelXboxStageCommand.CanExecute(null))'
    '                        _viewModel.CancelXboxStageCommand.Execute(null);'
    '                    break;'
    ''
    '                case "BrowseXboxDestination":'
    '                    await HandleBrowseXboxDestinationAsync();'
    '                    break;'
)

$lines = $lines[0..$insertIdx] + $senderCommands + $lines[($insertIdx+1)..($lines.Count-1)]

[System.IO.File]::WriteAllLines($file, $lines)
Write-Host "DONE - fixed switch structure"