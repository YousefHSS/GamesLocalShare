# Settings Test Script
# Run this after building to test if settings persist

Write-Host "?? Testing Settings Persistence..." -ForegroundColor Cyan

$settingsFolder = "$env:APPDATA\GamesLocalShare"
$settingsPath = "$settingsFolder\settings.json"
$settingsBackupPath = "$settingsFolder\settings.txt"

Write-Host "`n?? Settings folder location:" -ForegroundColor Yellow
Write-Host "   $settingsFolder"

# Check folder
if (Test-Path $settingsFolder) {
    Write-Host "`n? Settings folder exists" -ForegroundColor Green
} else {
    Write-Host "`n? Settings folder does NOT exist (will be created on first run)" -ForegroundColor Yellow
}

# Check JSON file
Write-Host "`n?? JSON Settings file:" -ForegroundColor Yellow
if (Test-Path $settingsPath) {
    Write-Host "   ? Exists" -ForegroundColor Green
    Write-Host "`n   Content:" -ForegroundColor Gray
    Get-Content $settingsPath | ForEach-Object { Write-Host "   $_" -ForegroundColor White }
    
    $file = Get-Item $settingsPath
    Write-Host "`n   Last Modified: $($file.LastWriteTime)" -ForegroundColor Gray
} else {
    Write-Host "   ? Does NOT exist" -ForegroundColor Yellow
}

# Check text backup file
Write-Host "`n?? Text Backup file (settings.txt):" -ForegroundColor Yellow
if (Test-Path $settingsBackupPath) {
    Write-Host "   ? Exists" -ForegroundColor Green
    Write-Host "`n   Content:" -ForegroundColor Gray
    Get-Content $settingsBackupPath | ForEach-Object { Write-Host "   $_" -ForegroundColor White }
    
    $file = Get-Item $settingsBackupPath
    Write-Host "`n   Last Modified: $($file.LastWriteTime)" -ForegroundColor Gray
} else {
    Write-Host "   ? Does NOT exist (will be created on first save)" -ForegroundColor Yellow
}

# Test write permissions
Write-Host "`n?? Testing write permissions..." -ForegroundColor Yellow
try {
    if (-not (Test-Path $settingsFolder)) {
        New-Item -ItemType Directory -Path $settingsFolder -Force | Out-Null
        Write-Host "   ? Created settings folder" -ForegroundColor Green
    }
    
    $testFile = "$settingsFolder\test_write.tmp"
    "test" | Out-File $testFile
    Remove-Item $testFile
    Write-Host "   ? Write permissions OK" -ForegroundColor Green
}
catch {
    Write-Host "   ? Write permissions FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n?? How to test:" -ForegroundColor Cyan
Write-Host "   1. Run the app (from installer or publish-output)" -ForegroundColor Gray
Write-Host "   2. Open Settings and toggle some options" -ForegroundColor Gray
Write-Host "   3. Click 'Save' and close settings" -ForegroundColor Gray
Write-Host "   4. Close the app COMPLETELY (check system tray too!)" -ForegroundColor Gray
Write-Host "   5. Run this script to see if files were saved" -ForegroundColor Gray
Write-Host "   6. Run the app again - settings should be loaded" -ForegroundColor Gray

Write-Host "`n??  Common issues:" -ForegroundColor Yellow
Write-Host "   - App running in system tray (close from tray icon Å® Exit)" -ForegroundColor Gray
Write-Host "   - Settings saved but not loaded (check both .json and .txt)" -ForegroundColor Gray
Write-Host "   - Multiple app instances running" -ForegroundColor Gray
