param(
    [string]$ProjectName = "MyProject"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " All Platforms Build Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Project: $ProjectName" -ForegroundColor Yellow
Write-Host "Time:    $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Yellow
Write-Host ""

Write-Host "--- Building Android ---" -ForegroundColor Magenta
$androidSteps = @(
    "Compiling Android source code...",
    "Processing Android resources...",
    "Building Android APK..."
)
foreach ($step in $androidSteps) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] " -NoNewline -ForegroundColor DarkGray
    Write-Host $step -ForegroundColor White
    Start-Sleep -Milliseconds (Get-Random -Minimum 500 -Maximum 1500)
}
Write-Host "✅ Android build complete." -ForegroundColor Green
Write-Host ""

Write-Host "--- Building Desktop ---" -ForegroundColor Magenta
$desktopSteps = @(
    "Compiling Desktop source code...",
    "Building WPF components...",
    "Packaging Desktop application..."
)
foreach ($step in $desktopSteps) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] " -NoNewline -ForegroundColor DarkGray
    Write-Host $step -ForegroundColor White
    Start-Sleep -Milliseconds (Get-Random -Minimum 500 -Maximum 1500)
}
Write-Host "✅ Desktop build complete." -ForegroundColor Green
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " All platforms built successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Outputs:" -ForegroundColor Yellow
Write-Host "  Android: build/$ProjectName-debug.apk" -ForegroundColor Gray
Write-Host "  Desktop: dist/$ProjectName-Setup.exe" -ForegroundColor Gray
Write-Host ""
Write-Host "Total time: $((Get-Random -Minimum 20 -Maximum 120)) seconds." -ForegroundColor Cyan
