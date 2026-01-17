# Build and Package Script for GamesLocalShare
# Run this to create release builds and installer

param(
    [string]$Version = "1.0.0",
    [switch]$BuildInstaller = $false,
    [switch]$SelfContained = $false
)

Write-Host "?? Building GamesLocalShare v$Version..." -ForegroundColor Cyan

# Clean old builds
Write-Host "`n?? Cleaning old builds..." -ForegroundColor Yellow
Remove-Item -Path ".\bin\Release\net8.0\win-x64\publish" -Recurse -ErrorAction SilentlyContinue
Remove-Item -Path ".\publish-output" -Recurse -ErrorAction SilentlyContinue
Remove-Item -Path ".\installer_output" -Recurse -ErrorAction SilentlyContinue

# Build
Write-Host "`n?? Building application..." -ForegroundColor Yellow
if ($SelfContained) {
    Write-Host "   Mode: Self-Contained (no .NET required)" -ForegroundColor Gray
    dotnet publish -c Release -r win-x64 --self-contained true
} else {
    Write-Host "   Mode: Framework-Dependent (requires .NET 8)" -ForegroundColor Gray
    dotnet publish -c Release -r win-x64 --self-contained false
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n? Build failed!" -ForegroundColor Red
    exit 1
}

# Copy to output folder
Write-Host "`n?? Copying files to publish-output..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path ".\publish-output" -Force | Out-Null
Copy-Item -Path ".\bin\Release\net8.0\win-x64\publish\*" -Destination ".\publish-output" -Recurse

# Create ZIP
$zipName = if ($SelfContained) { "GamesLocalShare-v$Version-SelfContained.zip" } else { "GamesLocalShare-v$Version-Portable.zip" }
Write-Host "`n?? Creating $zipName..." -ForegroundColor Yellow
Compress-Archive -Path ".\publish-output\*" -DestinationPath $zipName -Force

$zipSize = (Get-Item $zipName).Length / 1MB
Write-Host "   Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Gray

# Build installer if requested
if ($BuildInstaller) {
    Write-Host "`n?? Building installer..." -ForegroundColor Yellow
    
    # Check if Inno Setup is installed
    $innoPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    if (Test-Path $innoPath) {
        & $innoPath "installer.iss"
        
        if ($LASTEXITCODE -eq 0) {
            $installerFiles = Get-ChildItem ".\installer_output\*.exe"
            if ($installerFiles) {
                Write-Host "   ? Installer created: $($installerFiles[0].Name)" -ForegroundColor Green
                $installerSize = $installerFiles[0].Length / 1MB
                Write-Host "   Size: $([math]::Round($installerSize, 2)) MB" -ForegroundColor Gray
            }
        } else {
            Write-Host "   ? Installer build failed!" -ForegroundColor Red
        }
    } else {
        Write-Host "   ??  Inno Setup not found at: $innoPath" -ForegroundColor Yellow
        Write-Host "   Download from: https://jrsoftware.org/isinfo.php" -ForegroundColor Gray
    }
}

# Summary
Write-Host "`n? Build Complete!" -ForegroundColor Green
Write-Host "`nFiles created:" -ForegroundColor Cyan
Write-Host "  ?? publish-output\ - Application files" -ForegroundColor White
Write-Host "  ?? $zipName - Portable package" -ForegroundColor White
if ($BuildInstaller -and (Test-Path ".\installer_output")) {
    $installerFiles = Get-ChildItem ".\installer_output\*.exe"
    if ($installerFiles) {
        Write-Host "  ?? installer_output\$($installerFiles[0].Name) - Windows installer" -ForegroundColor White
    }
}

Write-Host "`n?? Next steps:" -ForegroundColor Cyan
Write-Host "  1. Test the build: .\publish-output\GamesLocalShare.exe" -ForegroundColor Gray
if ($BuildInstaller) {
    Write-Host "  2. Test the installer: .\installer_output\*.exe" -ForegroundColor Gray
}
Write-Host "  3. Create GitHub release:" -ForegroundColor Gray
Write-Host "       git tag v$Version" -ForegroundColor DarkGray
Write-Host "       git push origin v$Version" -ForegroundColor DarkGray
Write-Host "  4. Upload files to GitHub release" -ForegroundColor Gray

# Show GitHub release command
Write-Host "`n?? GitHub Release Command:" -ForegroundColor Cyan
Write-Host "gh release create v$Version $zipName --title 'GamesLocalShare v$Version' --notes 'Release notes here'" -ForegroundColor DarkGray
