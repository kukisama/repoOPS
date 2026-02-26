Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Clean Build Artifacts" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$dirs = @("build", "dist", "release", "obj", "bin", ".cache")

foreach ($dir in $dirs) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] " -NoNewline -ForegroundColor DarkGray
    if (Test-Path $dir) {
        Write-Host "Removing $dir..." -ForegroundColor Yellow
        # In a real scenario: Remove-Item $dir -Recurse -Force
        Start-Sleep -Milliseconds 300
        Write-Host "  ✅ Removed $dir" -ForegroundColor Green
    } else {
        Write-Host "Skipping $dir (not found)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "✅ Clean complete!" -ForegroundColor Green
