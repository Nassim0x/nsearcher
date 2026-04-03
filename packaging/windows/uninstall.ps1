param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NSearcher"
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-support.ps1')

Write-Step 'Removing PATH entry'
Remove-DirectoryFromUserPath -Directory $InstallDir

if (Test-Path $InstallDir) {
    Write-Step 'Removing installed files'
    Stop-InstalledProcesses -Directory $InstallDir
    Remove-Item -Path $InstallDir -Recurse -Force
}

Write-Step 'Uninstall complete'
