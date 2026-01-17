# Settings Test Script
# Run this after building to test if settings persist

Write-Host "?? Testing Settings Persistence..." -ForegroundColor Cyan

$settingsPath = "$env:APPDATA\GamesLocalShare\settings.json"

Write-Host "`n?? Settings file location:" -ForegroundColor Yellow
Write-Host "   $settingsPath"

if (Test-Path $settingsPath) {
    Write-Host "`n? Settings file exists" -ForegroundColor Green
    Write-Host "`n?? Current settings content:" -ForegroundColor Yellow
    Get-Content $settingsPath | Write-Host
    
    Write-Host "`n?? File info:" -ForegroundColor Yellow
    $file = Get-Item $settingsPath
    Write-Host "   Size: $($file.Length) bytes"
    Write-Host "   Last Modified: $($file.LastWriteTime)"
    Write-Host "   Attributes: $($file.Attributes)"
    
    # Test write permissions
    Write-Host "`n?? Testing write permissions..." -ForegroundColor Yellow
    try {
        $testFile = "$env:APPDATA\GamesLocalShare\test_write.tmp"
        "test" | Out-File $testFile
        Remove-Item $testFile
        Write-Host "   ? Write permissions OK" -ForegroundColor Green
    }
    catch {
        Write-Host "   ? Write permissions FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
} else {
    Write-Host "`n? Settings file does NOT exist" -ForegroundColor Yellow
    Write-Host "   This is normal if the app hasn't been run yet" -ForegroundColor Gray
}

Write-Host "`n?? To test:" -ForegroundColor Cyan
Write-Host "   1. Run the app: .\publish-output\GamesLocalShare.exe" -ForegroundColor Gray
Write-Host "   2. Open Settings and make changes" -ForegroundColor Gray
Write-Host "   3. Close the app completely" -ForegroundColor Gray
Write-Host "   4. Run this script again to see if settings persisted" -ForegroundColor Gray
Write-Host "   5. Run the app again to see if settings are loaded" -ForegroundColor Gray
