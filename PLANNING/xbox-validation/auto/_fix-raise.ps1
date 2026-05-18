$file = 'c:\laragon\www\GamesLocalShare\ViewModels\MainViewModel.cs'
$lines = Get-Content $file
$lines = $lines | Where-Object { $_ -notmatch 'RaiseStateChanged\(\);' }
$lines | Set-Content $file
Write-Host "Removed RaiseStateChanged()"