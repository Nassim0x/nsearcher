function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Add-DirectoryToUserPath {
    param([string]$Directory)

    $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @()

    if (-not [string]::IsNullOrWhiteSpace($currentUserPath)) {
        $entries = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
    }

    $alreadyPresent = $entries | Where-Object {
        $_.TrimEnd('\') -ieq $Directory.TrimEnd('\')
    }

    if (-not $alreadyPresent) {
        $updatedEntries = @($entries + $Directory)
        $updatedUserPath = ($updatedEntries -join ';').Trim(';')
        [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, 'User')
        Write-Host "Added '$Directory' to the user PATH." -ForegroundColor Green
    }
    else {
        Write-Host "'$Directory' is already present in the user PATH." -ForegroundColor Yellow
    }

    $sessionEntries = $env:Path.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
    $sessionHasDirectory = $sessionEntries | Where-Object {
        $_.TrimEnd('\') -ieq $Directory.TrimEnd('\')
    }

    if (-not $sessionHasDirectory) {
        $env:Path = (($env:Path.TrimEnd(';')) + ';' + $Directory).Trim(';')
    }
}

function Remove-DirectoryFromUserPath {
    param([string]$Directory)

    $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ([string]::IsNullOrWhiteSpace($currentUserPath)) {
        return
    }

    $updatedEntries = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { $_.TrimEnd('\') -ine $Directory.TrimEnd('\') }

    [Environment]::SetEnvironmentVariable('Path', (($updatedEntries -join ';').Trim(';')), 'User')

    $sessionEntries = $env:Path.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { $_.TrimEnd('\') -ine $Directory.TrimEnd('\') }

    $env:Path = ($sessionEntries -join ';').Trim(';')
}

function Stop-InstalledProcesses {
    param([string]$Directory)

    Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -and $_.Path.StartsWith($Directory, [System.StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}
