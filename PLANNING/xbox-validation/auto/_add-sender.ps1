$file = 'c:\laragon\www\GamesLocalShare\ViewModels\MainViewModel.cs'
$lines = [System.IO.File]::ReadAllLines($file)

# 1. Add XboxSenderService field after XboxTransferService
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'private readonly XboxTransferService _xboxTransferService;') {
        $insertIdx = $i + 1
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: XboxTransferService field not found"; exit 1 }

$fieldLine = '    private readonly XboxSenderService _xboxSenderService;'
$lines = $lines[0..($insertIdx-1)] + @($fieldLine) + $lines[$insertIdx..($lines.Count-1)]
Write-Host "Added XboxSenderService field"

# 2. Initialize XboxSenderService in constructor (after XboxTransferService init)
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '_xboxTransferService = new XboxTransferService\(\);') {
        $insertIdx = $i + 1
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: XboxTransferService ctor not found"; exit 1 }

$ctorLine = '        _xboxSenderService = new XboxSenderService();'
$lines = $lines[0..($insertIdx-1)] + @($ctorLine) + $lines[$insertIdx..($lines.Count-1)]
Write-Host "Added XboxSenderService initialization"

# 3. Add sender commands before the existing Xbox transfer commands
# Find "StartXboxTransferAsync" and insert before it
$insertIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match 'private async Task StartXboxTransferAsync\(string sourcePath\)') {
        $insertIdx = $i
        break
    }
}
if ($insertIdx -lt 0) { Write-Host "ERROR: StartXboxTransferAsync not found"; exit 1 }

$senderCommands = @(
    ''
    '    // ===== Xbox Sender Commands ====='
    ''
    '    [RelayCommand]'
    '    private async Task StartXboxStageAsync(string sourcePath)'
    '    {'
    '        if (string.IsNullOrWhiteSpace(sourcePath))'
    '        {'
    '            AddLog("Xbox staging: no source path provided", LogMessageType.Error);'
    '            return;'
    '        }'
    ''
    '        _xboxSenderService.Reset();'
    '        var error = _xboxSenderService.ValidateSource(sourcePath);'
    '        if (error != null)'
    '        {'
    '            AddLog($"Xbox staging validation failed: {error}", LogMessageType.Error);'
    '            LastError = error;'
    '            return;'
    '        }'
    ''
    '        IsXboxTransferActive = true;'
    '        XboxTransfer = _xboxSenderService.State;'
    '        _xboxSenderService.StateChanged += OnXboxTransferStateChanged;'
    '        _xboxSenderService.LogMessage += (_, msg) => AddLog(msg, LogMessageType.Info);'
    ''
    '        AddLog($"Xbox staging: {_xboxSenderService.State.GameName} ({_xboxSenderService.State.SourceBytes / 1024 / 1024} MB, {_xboxSenderService.State.SourceFileCount} files)", LogMessageType.Info);'
    ''
    '        // Prompt user for destination'
    '        StatusMessage = "Xbox staging: Select destination folder (USB/shared drive)...";'
    '        RaiseStateChanged();'
    '    }'
    ''
    '    [RelayCommand]'
    '    private async Task CompleteXboxStageAsync(string destinationPath)'
    '    {'
    '        if (string.IsNullOrWhiteSpace(destinationPath))'
    '        {'
    '            AddLog("Xbox staging: no destination provided", LogMessageType.Error);'
    '            return;'
    '        }'
    ''
    '        try'
    '        {'
    '            await _xboxSenderService.StageAsync(destinationPath);'
    '            if (_xboxSenderService.State.CurrentStep == XboxTransferStep.Complete)'
    '            {'
    '                AddLog($"Xbox staging complete: {_xboxSenderService.State.DestinationPath}", LogMessageType.Info);'
    '                StatusMessage = $"Xbox staging complete: {_xboxSenderService.State.GameName}";'
    '                await ScanLocalGamesAsync();'
    '            }'
    '            else'
    '            {'
    '                LastError = _xboxSenderService.State.ErrorMessage ?? "Staging failed";'
    '            }'
    '        }'
    '        catch (Exception ex)'
    '        {'
    '            AddLog($"Xbox staging error: {ex.Message}", LogMessageType.Error);'
    '            LastError = ex.Message;'
    '        }'
    '        finally'
    '        {'
    '            IsXboxTransferActive = false;'
    '            _xboxSenderService.StateChanged -= OnXboxTransferStateChanged;'
    '        }'
    '    }'
    ''
    '    [RelayCommand]'
    '    private void CancelXboxStage()'
    '    {'
    '        _xboxSenderService.Cancel();'
    '        IsXboxTransferActive = false;'
    '        StatusMessage = "Xbox staging cancelled";'
    '        AddLog("Xbox staging cancelled by user", LogMessageType.Warning);'
    '    }'
    ''
)

$lines = $lines[0..($insertIdx-1)] + $senderCommands + $lines[$insertIdx..($lines.Count-1)]
Write-Host "Added sender commands"

[System.IO.File]::WriteAllLines($file, $lines)
Write-Host "DONE - $($lines.Count) lines"