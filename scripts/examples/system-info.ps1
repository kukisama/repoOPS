Write-Host "========================================" -ForegroundColor Cyan
Write-Host " System Information" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "--- OS Information ---" -ForegroundColor Yellow
Write-Host "OS:          $([System.Environment]::OSVersion.VersionString)" -ForegroundColor White
Write-Host "Machine:     $([System.Environment]::MachineName)" -ForegroundColor White
Write-Host "User:        $([System.Environment]::UserName)" -ForegroundColor White
Write-Host "Processors:  $([System.Environment]::ProcessorCount)" -ForegroundColor White
Write-Host ""

Write-Host "--- PowerShell ---" -ForegroundColor Yellow
Write-Host "Version:     $($PSVersionTable.PSVersion)" -ForegroundColor White
Write-Host "Edition:     $($PSVersionTable.PSEdition)" -ForegroundColor White
Write-Host ""

Write-Host "--- .NET Runtime ---" -ForegroundColor Yellow
Write-Host "Version:     $([System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription)" -ForegroundColor White
Write-Host ""

Write-Host "--- Environment ---" -ForegroundColor Yellow
Write-Host "Current Dir: $(Get-Location)" -ForegroundColor White
Write-Host "Temp Dir:    $([System.IO.Path]::GetTempPath())" -ForegroundColor White
Write-Host ""

# Check for common tools
Write-Host "--- Available Tools ---" -ForegroundColor Yellow
$tools = @("git", "dotnet", "node", "npm", "python", "pwsh")
foreach ($tool in $tools) {
    $found = Get-Command $tool -ErrorAction SilentlyContinue
    if ($found) {
        Write-Host "  ✅ $tool" -NoNewline -ForegroundColor Green
        Write-Host " ($($found.Source))" -ForegroundColor DarkGray
    } else {
        Write-Host "  ❌ $tool (not found)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "✅ System info collected." -ForegroundColor Green
