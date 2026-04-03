param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NSearcher",
    [string]$PayloadDir = (Join-Path $PSScriptRoot 'payload')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-support.ps1')

if (-not (Test-Path $PayloadDir)) {
    throw "Bundle payload directory not found: $PayloadDir"
}

$requiredFiles = @(
    'NSearcher.exe',
    'NSearcher.Server.exe'
)

foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $PayloadDir $requiredFile
    if (-not (Test-Path $requiredPath)) {
        throw "Bundle payload is missing required file: $requiredFile"
    }
}

Write-Step "Installing bundle into $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Stop-InstalledProcesses -Directory $InstallDir

Get-ChildItem -Path $InstallDir -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Copy-Item -Path (Join-Path $PayloadDir '*') -Destination $InstallDir -Recurse -Force
Add-DirectoryToUserPath -Directory $InstallDir

Write-Step 'Installation complete'
Write-Host 'Installed from release bundle.' -ForegroundColor Green
Write-Host 'Open a new terminal, then run:' -ForegroundColor Green
Write-Host '  NSearcher --help' -ForegroundColor White
