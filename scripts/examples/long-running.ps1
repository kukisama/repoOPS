Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Long Running Task (Demo)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This task demonstrates real-time output streaming." -ForegroundColor Yellow
Write-Host "It will run for approximately 30 seconds." -ForegroundColor Yellow
Write-Host ""

$totalSteps = 30

for ($i = 1; $i -le $totalSteps; $i++) {
    $percent = [math]::Round(($i / $totalSteps) * 100)
    $barLength = 40
    $filled = [math]::Round($barLength * $i / $totalSteps)
    $empty = $barLength - $filled
    $bar = "█" * $filled + "░" * $empty

    Write-Host "`r[$bar] $percent% ($i/$totalSteps)" -NoNewline -ForegroundColor $(
        if ($percent -lt 33) { "Red" }
        elseif ($percent -lt 66) { "Yellow" }
        else { "Green" }
    )

    # Also write some verbose output every 5 steps
    if ($i % 5 -eq 0) {
        Write-Host ""
        Write-Host "  [$(Get-Date -Format 'HH:mm:ss')] Step $i completed - Processing batch $(Get-Random -Minimum 1000 -Maximum 9999)..." -ForegroundColor DarkGray
    }

    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " Long running task completed!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Total duration: ~30 seconds" -ForegroundColor Cyan
