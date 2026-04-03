param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NSearcher"
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Remove-DirectoryFromUserPath {
    param([string]$Directory)

    $currentUserPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ([string]::IsNullOrWhiteSpace($currentUserPath)) {
        return
    }

    $updatedEntries = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { $_.TrimEnd('\') -ine $Directory.TrimEnd('\') }

    [Environment]::SetEnvironmentVariable("Path", (($updatedEntries -join ';').Trim(';')), "User")

    $sessionEntries = $env:Path.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { $_.TrimEnd('\') -ine $Directory.TrimEnd('\') }

    $env:Path = ($sessionEntries -join ';').Trim(';')
}

Write-Step "Removing PATH entry"
Remove-DirectoryFromUserPath -Directory $InstallDir

if (Test-Path $InstallDir) {
    Write-Step "Removing installed files"
    Remove-Item -Path $InstallDir -Recurse -Force
}

Write-Step "Uninstall complete"
