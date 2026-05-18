$file = 'c:\laragon\www\GamesLocalShare\Services\InteropBridge.cs'
$lines = [System.IO.File]::ReadAllLines($file)

# Find the StartLocalCopy break and insert Xbox commands after it
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'case "StartLocalCopy":') {
        for ($j = $i; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim() -eq 'break;' -and $j -gt $i + 5) {
                $insertIdx = $j
                break
            }
        }
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: StartLocalCopy break not found"; exit 1 }

$xboxCommands = @(
    ''
    '                // Xbox transfer commands (receiver + sender)'
    '                case "StartXboxTransfer":'
    '                    if (payload?.TryGetProperty("sourcePath", out var xboxSourceEl) == true)'
    '                    {'
    '                        var sourcePath = xboxSourceEl.GetString() ?? "";'
    '                        if (_viewModel.StartXboxTransferCommand.CanExecute(sourcePath))'
    '                            await _viewModel.StartXboxTransferCommand.ExecuteAsync(sourcePath);'
    '                    }'
    '                    break;'
    ''
    '                case "CancelXboxTransfer":'
    '                    if (_viewModel.CancelXboxTransferCommand.CanExecute(null))'
    '                        _viewModel.CancelXboxTransferCommand.Execute(null);'
    '                    break;'
    ''
    '                case "BrowseXboxSource":'
    '                    await HandleBrowseXboxSourceAsync();'
    '                    break;'
    ''
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

$lines = $lines[0..$insertIdx] + $xboxCommands + $lines[($insertIdx+1)..($lines.Count-1)]
Write-Host "Added Xbox commands"

# Add the two browse methods before PushSettingsAsync
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'private async Task PushSettingsAsync\(\)') {
        $insertIdx = $i
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: PushSettingsAsync not found"; exit 1 }

$browseMethods = @(
    '    private async Task HandleBrowseXboxSourceAsync()'
    '    {'
    '        if (_webView == null) return;'
    '        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;'
    '        if (topLevel == null) return;'
    ''
    '        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions'
    '        {'
    '            Title = "Select Xbox staged game folder (must contain transfer-summary.json)",'
    '            AllowMultiple = false'
    '        });'
    ''
    '        if (result.Count > 0)'
    '        {'
    '            var path = result[0].Path.LocalPath;'
    '            var json = JsonSerializer.Serialize(new { xboxSourcePath = path }, JsonOptions);'
    '            await ExecuteJavaScriptAsync($"window.__updateState({json});");'
    '        }'
    '    }'
    ''
    '    private async Task HandleBrowseXboxDestinationAsync()'
    '    {'
    '        if (_webView == null) return;'
    '        var topLevel = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;'
    '        if (topLevel == null) return;'
    ''
    '        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions'
    '        {'
    '            Title = "Select destination folder for Xbox staged game (USB/shared drive)",'
    '            AllowMultiple = false'
    '        });'
    ''
    '        if (result.Count > 0)'
    '        {'
    '            var path = result[0].Path.LocalPath;'
    '            var json = JsonSerializer.Serialize(new { xboxDestinationPath = path }, JsonOptions);'
    '            await ExecuteJavaScriptAsync($"window.__updateState({json});");'
    '        }'
    '    }'
    ''
)

$lines = $lines[0..($insertIdx-1)] + $browseMethods + $lines[$insertIdx..($lines.Count-1)]
Write-Host "Added browse methods"

# Add xboxTransfer and xboxDestinationPath to GetFullState
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'crossLocationGames = _viewModel\.CrossLocationGames') {
        $insertIdx = $i
        break
    }
}
if ($insertIdx -gt 0) {
    $stateLines = @(
        ''
        '            // Xbox transfer'
    '            xboxTransfer = _viewModel.XboxTransfer,'
    '            isXboxTransferActive = _viewModel.IsXboxTransferActive,'
    )
    $lines = $lines[0..$insertIdx] + $stateLines + $lines[($insertIdx+1)..($lines.Count-1)]
    Write-Host "Added Xbox state to GetFullState"
}

[System.IO.File]::WriteAllLines($file, $lines)
Write-Host "DONE - $($lines.Count) lines"