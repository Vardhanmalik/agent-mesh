# PowerShell wrapper to run azure-setup.sh on Windows
param(
    [switch]$UseWSL = $false,
    [switch]$UseGitBash = $false
)

$scriptPath = Join-Path $PSScriptRoot "azure-setup.sh"
$errorLog = Join-Path $PSScriptRoot "azure-setup-errors.log"

# Clean up old error log
if (Test-Path $errorLog) {
    Remove-Item $errorLog -Force -ErrorAction SilentlyContinue
}

Write-Host "Orchestrator Engine - Azure Setup Wrapper" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Script: $scriptPath" -ForegroundColor Yellow
Write-Host ""

# Try to detect and use bash
$bashPath = $null

# First try: Check if git bash is installed
if ($UseGitBash -or -not $UseWSL) {
    $gitBashPaths = @(
        "C:\Program Files\Git\bin\bash.exe",
        "C:\Program Files (x86)\Git\bin\bash.exe"
    )
    
    foreach ($path in $gitBashPaths) {
        if (Test-Path $path) {
            $bashPath = $path
            Write-Host "Found Git Bash at: $bashPath" -ForegroundColor Green
            break
        }
    }
}

# Second try: Check if WSL is available
if (-not $bashPath -and ($UseWSL -or -not $UseGitBash)) {
    try {
        $wslCheck = wsl --list --verbose 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "WSL is available" -ForegroundColor Green
            $bashPath = "wsl"
        }
    }
    catch {
        # WSL not available
    }
}

# If still no bash found, provide instructions
if (-not $bashPath) {
    Write-Host "ERROR: Bash is not available!" -ForegroundColor Red
    Write-Host ""
    Write-Host "To run this script, you need one of the following:" -ForegroundColor Yellow
    Write-Host "1. Git Bash (https://git-scm.com/download/win)"
    Write-Host "2. Windows Subsystem for Linux (WSL)"
    Write-Host ""
    Write-Host "Install one and try again." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "After installing Git Bash, run: .\run-setup.ps1 -UseGitBash" -ForegroundColor Cyan
    Write-Host "After installing WSL, run: .\run-setup.ps1 -UseWSL" -ForegroundColor Cyan
    exit 1
}

# Run the script
Write-Host "Running setup script..." -ForegroundColor Cyan
Write-Host ""

if ($bashPath -eq "wsl") {
    # Convert Windows path to WSL path
    $wslPath = $scriptPath -replace '\\', '/' -replace '^([A-Z]):', '/mnt/$1' -replace '/mnt/([a-z])/', '/mnt/${1}/'
    $wslPath = $wslPath.ToLower()
    wsl bash $wslPath
} else {
    # Use Git Bash
    & $bashPath $scriptPath
}

$exitCode = $LASTEXITCODE

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "Setup completed with exit code: $exitCode" -ForegroundColor Cyan
Write-Host ""

# Display error log if it has content
if (Test-Path $errorLog) {
    $logSize = (Get-Item $errorLog).Length
    if ($logSize -gt 0) {
        Write-Host "Error log contents:" -ForegroundColor Yellow
        Get-Content $errorLog
    }
}

exit $exitCode
