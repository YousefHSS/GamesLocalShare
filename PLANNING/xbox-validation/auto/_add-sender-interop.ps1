$file = 'c:\laragon\www\GamesLocalShare\Services\InteropBridge.cs'
$lines = [System.IO.File]::ReadAllLines($file)

# Insert sender messages after line 613 (BrowseXboxSource break), which is index 612
$insertIdx = 613
Write-Host "Inserting at line $($insertIdx + 1)"

$senderInterop = @(
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

$lines = $lines[0..$insertIdx] + $senderInterop + $lines[($insertIdx+1)..($lines.Count-1)]
Write-Host "Added sender interop messages"

# Add HandleBrowseXboxDestinationAsync method before HandleBrowseXboxSourceAsync
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'private async Task HandleBrowseXboxSourceAsync\(\)') {
        $insertIdx = $i
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: HandleBrowseXboxSourceAsync not found"; exit 1 }

$browseDestMethod = @(
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

$lines = $lines[0..($insertIdx-1)] + $browseDestMethod + $lines[$insertIdx..($lines.Count-1)]
Write-Host "Added HandleBrowseXboxDestinationAsync method"

[System.IO.File]::WriteAllLines($file, $lines)
Write-Host "DONE - $($lines.Count) lines"