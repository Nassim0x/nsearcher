param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NSearcher",
    [switch]$DisableAot
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Add-DirectoryToUserPath {
    param([string]$Directory)

    $currentUserPath = [Environment]::GetEnvironmentVariable("Path", "User")
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
        [Environment]::SetEnvironmentVariable("Path", $updatedUserPath, "User")
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

function Get-VcVarsAllPath {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $installationPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
            $vcvarsall = Join-Path $installationPath 'VC\Auxiliary\Build\vcvarsall.bat'
            if (Test-Path $vcvarsall) {
                return $vcvarsall
            }
        }
    }

    $fallback = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat'
    if (Test-Path $fallback) {
        return $fallback
    }

    return $null
}

function Build-NativeLauncher {
    param(
        [string]$ProjectRoot,
        [string]$OutputDir
    )

    $vcvarsall = Get-VcVarsAllPath
    if ([string]::IsNullOrWhiteSpace($vcvarsall)) {
        return $false
    }

    $sourceFile = Join-Path $ProjectRoot 'native\launcher\nsearcher_launcher.c'
    if (-not (Test-Path $sourceFile)) {
        return $false
    }

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $objFile = Join-Path $OutputDir 'nsearcher_launcher.obj'
    $exeFile = Join-Path $OutputDir 'NSearcher.exe'
    $tempCmd = Join-Path $env:TEMP ("nsearcher-build-" + [Guid]::NewGuid().ToString("N") + ".cmd")

    try {
        @"
@echo off
call "$vcvarsall" x64 >nul
if errorlevel 1 exit /b 1
cl /nologo /O2 /MT /GL /DNDEBUG /DUNICODE /D_UNICODE /Fo"$objFile" /Fe"$exeFile" "$sourceFile" /link /LTCG shell32.lib
"@ | Set-Content -Path $tempCmd -Encoding Ascii

        & cmd.exe /c $tempCmd | Out-Host
        return $LASTEXITCODE -eq 0 -and (Test-Path $exeFile)
    }
    finally {
        Remove-Item $tempCmd -Force -ErrorAction SilentlyContinue
    }
}

function Format-CmdArgument {
    param([string]$Value)

    if ($Value -match '[\s"]') {
        return '"' + $Value.Replace('"', '\"') + '"'
    }

    return $Value
}

function Invoke-DotNetPublish {
    param(
        [string[]]$Arguments,
        [switch]$UseVcEnvironment
    )

    if ($UseVcEnvironment) {
        $vcvarsall = Get-VcVarsAllPath
        if ([string]::IsNullOrWhiteSpace($vcvarsall)) {
            return $false
        }

        $tempCmd = Join-Path $env:TEMP ("nsearcher-publish-" + [Guid]::NewGuid().ToString("N") + ".cmd")

        try {
            $argumentLine = ($Arguments | ForEach-Object { Format-CmdArgument $_ }) -join ' '
            @"
@echo off
call "$vcvarsall" x64 >nul
if errorlevel 1 exit /b 1
dotnet $argumentLine
"@ | Set-Content -Path $tempCmd -Encoding Ascii

            & cmd.exe /c $tempCmd | Out-Host
            return $LASTEXITCODE -eq 0
        }
        finally {
            Remove-Item $tempCmd -Force -ErrorAction SilentlyContinue
        }
    }

    & dotnet @Arguments | Out-Host
    return $LASTEXITCODE -eq 0
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$clientProjectFile = Join-Path $projectRoot "NSearcher.Client\NSearcher.Client.csproj"
$serverProjectFile = Join-Path $projectRoot "NSearcher.Cli\NSearcher.Cli.csproj"
$publishDir = Join-Path $projectRoot "artifacts\publish\$Runtime"
$serverPublishDir = Join-Path $publishDir "server"
$aotPublishDir = Join-Path $projectRoot "artifacts\publish-aot\$Runtime"
$aotServerPublishDir = Join-Path $aotPublishDir "server"
$nativeLauncherPublishDir = Join-Path $projectRoot "artifacts\native-launcher\$Runtime"
$publishMode = "managed"
$nativeLauncherBuilt = $false

if (-not $DisableAot) {
    Write-Step "Publishing NSearcher (Native AOT if supported)"
    $clientAotPublished = Invoke-DotNetPublish -Arguments @(
        "publish",
        $clientProjectFile,
        "-c", $Configuration,
        "-r", $Runtime,
        "-p:PublishAot=true",
        "-p:PublishTrimmed=true",
        "-p:InvariantGlobalization=true",
        "-p:IlcUseEnvironmentalTools=true",
        "-o", $aotPublishDir
    ) -UseVcEnvironment

    $serverAotPublished = $false
    if ($clientAotPublished) {
        $serverAotPublished = Invoke-DotNetPublish -Arguments @(
            "publish",
            $serverProjectFile,
            "-c", $Configuration,
            "-r", $Runtime,
            "-p:PublishAot=true",
            "-p:PublishTrimmed=true",
            "-p:InvariantGlobalization=true",
            "-p:IlcUseEnvironmentalTools=true",
            "-o", $aotServerPublishDir
        ) -UseVcEnvironment
    }

    if ($clientAotPublished -and $serverAotPublished) {
        $publishDir = $aotPublishDir
        $serverPublishDir = $aotServerPublishDir
        $publishMode = "native-aot"
    }
    else {
        Write-Host "Native AOT publish failed on this machine. Falling back to managed publish." -ForegroundColor Yellow
    }
}

if ($publishMode -ne "native-aot") {
    Write-Step "Publishing NSearcher (managed fallback)"
    $clientManagedPublished = Invoke-DotNetPublish -Arguments @(
        "publish",
        $clientProjectFile,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "false",
        "-o", $publishDir
    )

    $serverManagedPublished = $false
    if ($clientManagedPublished) {
        $serverManagedPublished = Invoke-DotNetPublish -Arguments @(
            "publish",
            $serverProjectFile,
            "-c", $Configuration,
            "-r", $Runtime,
            "--self-contained", "false",
            "-o", $serverPublishDir
        )
    }

    if (-not ($clientManagedPublished -and $serverManagedPublished)) {
        throw "dotnet publish failed."
    }
}

$nativeLauncherBuilt = Build-NativeLauncher -ProjectRoot $projectRoot -OutputDir $nativeLauncherPublishDir

Write-Step "Installing into $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Stop-InstalledProcesses -Directory $InstallDir
Get-ChildItem -Path $InstallDir -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $publishDir "*") -Destination $InstallDir -Recurse -Force
Copy-Item -Path (Join-Path $serverPublishDir "*") -Destination $InstallDir -Recurse -Force
if ($nativeLauncherBuilt) {
    Copy-Item -Path (Join-Path $nativeLauncherPublishDir "NSearcher.exe") -Destination (Join-Path $InstallDir "NSearcher.exe") -Force
}

Add-DirectoryToUserPath -Directory $InstallDir

Write-Step "Installation complete"
Write-Host "Publish mode: $publishMode" -ForegroundColor Green
$launcherMode = if ($nativeLauncherBuilt) { "native-c" } else { "managed-aot" }
Write-Host "Launcher mode: $launcherMode" -ForegroundColor Green
Write-Host "Open a new terminal, then run:" -ForegroundColor Green
Write-Host "  NSearcher --help" -ForegroundColor White
