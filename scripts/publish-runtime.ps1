function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
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

function Get-PublishVersionProperties {
    param([string]$Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        return @()
    }

    $trimmed = $Version.Trim()
    $normalized = $trimmed.TrimStart('v', 'V')
    $properties = @("-p:InformationalVersion=$trimmed")

    if ($normalized -match '^\d+\.\d+\.\d+([-.].+)?$') {
        $properties += "-p:Version=$normalized"
    }

    return $properties
}

function Copy-NSearcherInstallLayout {
    param(
        [psobject]$PublishResult,
        [string]$Destination,
        [switch]$ExcludeSymbols
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    Get-ChildItem -Path $PublishResult.PublishDir -Force |
        Where-Object {
            $_.Name -ne 'server' -and
            (-not $ExcludeSymbols -or $_.PSIsContainer -or $_.Extension -ne '.pdb')
        } |
        ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $Destination -Recurse -Force
        }

    Get-ChildItem -Path $PublishResult.ServerPublishDir -Force |
        Where-Object {
            -not $ExcludeSymbols -or $_.PSIsContainer -or $_.Extension -ne '.pdb'
        } |
        ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $Destination -Recurse -Force
        }

    if ($PublishResult.NativeLauncherBuilt) {
        Copy-Item -Path (Join-Path $PublishResult.NativeLauncherPublishDir 'NSearcher.exe') -Destination (Join-Path $Destination 'NSearcher.exe') -Force
    }
}

function Publish-NSearcher {
    param(
        [string]$ProjectRoot,
        [string]$Configuration = "Release",
        [string]$Runtime = "win-x64",
        [switch]$DisableAot,
        [string]$Version,
        [string]$ArtifactsRoot
    )

    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
        $ArtifactsRoot = Join-Path $ProjectRoot 'artifacts'
    }

    $clientProjectFile = Join-Path $ProjectRoot 'NSearcher.Client\NSearcher.Client.csproj'
    $serverProjectFile = Join-Path $ProjectRoot 'NSearcher.Cli\NSearcher.Cli.csproj'
    $managedPublishDir = Join-Path (Join-Path $ArtifactsRoot 'publish') $Runtime
    $managedServerPublishDir = Join-Path $managedPublishDir 'server'
    $aotPublishDir = Join-Path (Join-Path $ArtifactsRoot 'publish-aot') $Runtime
    $aotServerPublishDir = Join-Path $aotPublishDir 'server'
    $nativeLauncherPublishDir = Join-Path (Join-Path $ArtifactsRoot 'native-launcher') $Runtime
    $publishMode = 'managed'
    $nativeLauncherBuilt = $false
    $publishProperties = Get-PublishVersionProperties -Version $Version
    $publishDir = $managedPublishDir
    $serverPublishDir = $managedServerPublishDir

    if (-not $DisableAot) {
        Write-Step "Publishing NSearcher (Native AOT if supported)"
        $clientAotPublished = Invoke-DotNetPublish -Arguments (@(
            'publish',
            $clientProjectFile,
            '-c', $Configuration,
            '-r', $Runtime,
            '-p:PublishAot=true',
            '-p:PublishTrimmed=true',
            '-p:InvariantGlobalization=true',
            '-p:IlcUseEnvironmentalTools=true',
            '-o', $aotPublishDir
        ) + $publishProperties) -UseVcEnvironment

        $serverAotPublished = $false
        if ($clientAotPublished) {
            $serverAotPublished = Invoke-DotNetPublish -Arguments (@(
                'publish',
                $serverProjectFile,
                '-c', $Configuration,
                '-r', $Runtime,
                '-p:PublishAot=true',
                '-p:PublishTrimmed=true',
                '-p:InvariantGlobalization=true',
                '-p:IlcUseEnvironmentalTools=true',
                '-o', $aotServerPublishDir
            ) + $publishProperties) -UseVcEnvironment
        }

        if ($clientAotPublished -and $serverAotPublished) {
            $publishDir = $aotPublishDir
            $serverPublishDir = $aotServerPublishDir
            $publishMode = 'native-aot'
        }
        else {
            Write-Host 'Native AOT publish failed on this machine. Falling back to managed publish.' -ForegroundColor Yellow
        }
    }

    if ($publishMode -ne 'native-aot') {
        Write-Step 'Publishing NSearcher (managed fallback)'
        $clientManagedPublished = Invoke-DotNetPublish -Arguments (@(
            'publish',
            $clientProjectFile,
            '-c', $Configuration,
            '-r', $Runtime,
            '--self-contained', 'false',
            '-o', $managedPublishDir
        ) + $publishProperties)

        $serverManagedPublished = $false
        if ($clientManagedPublished) {
            $serverManagedPublished = Invoke-DotNetPublish -Arguments (@(
                'publish',
                $serverProjectFile,
                '-c', $Configuration,
                '-r', $Runtime,
                '--self-contained', 'false',
                '-o', $managedServerPublishDir
            ) + $publishProperties)
        }

        if (-not ($clientManagedPublished -and $serverManagedPublished)) {
            throw 'dotnet publish failed.'
        }
    }

    $nativeLauncherBuilt = Build-NativeLauncher -ProjectRoot $ProjectRoot -OutputDir $nativeLauncherPublishDir
    $launcherMode = if ($nativeLauncherBuilt) { 'native-c' } else { 'managed-aot' }
    $launcherPath = if ($nativeLauncherBuilt) { Join-Path $nativeLauncherPublishDir 'NSearcher.exe' } else { Join-Path $publishDir 'NSearcher.exe' }

    return [pscustomobject]@{
        ProjectRoot = $ProjectRoot
        Runtime = $Runtime
        Configuration = $Configuration
        Version = $Version
        PublishMode = $publishMode
        PublishDir = $publishDir
        ServerPublishDir = $serverPublishDir
        NativeLauncherBuilt = $nativeLauncherBuilt
        NativeLauncherPublishDir = $nativeLauncherPublishDir
        LauncherMode = $launcherMode
        LauncherPath = $launcherPath
    }
}

function Publish-NSearcherInstaller {
    param(
        [string]$ProjectRoot,
        [string]$Configuration = "Release",
        [string]$Runtime = "win-x64",
        [string]$Version,
        [string]$PayloadZipPath,
        [string]$OutputDir,
        [switch]$DisableAot
    )

    if ([string]::IsNullOrWhiteSpace($PayloadZipPath) -or -not (Test-Path $PayloadZipPath)) {
        throw "Installer payload zip not found: $PayloadZipPath"
    }

    if ([string]::IsNullOrWhiteSpace($OutputDir)) {
        $OutputDir = Join-Path (Join-Path $ProjectRoot 'artifacts') 'installer'
    }

    $installerProjectFile = Join-Path $ProjectRoot 'NSearcher.Installer\NSearcher.Installer.csproj'
    $publishProperties = Get-PublishVersionProperties -Version $Version
    $publishProperties += "-p:PayloadZipPath=$PayloadZipPath"
    $installerPublished = $false
    $publishMode = 'managed-single-file'

    if (-not $DisableAot) {
        Write-Step 'Publishing NSearcher installer (Native AOT if supported)'
        $installerPublished = Invoke-DotNetPublish -Arguments (@(
            'publish',
            $installerProjectFile,
            '-c', $Configuration,
            '-r', $Runtime,
            '-p:PublishAot=true',
            '-p:PublishTrimmed=true',
            '-p:InvariantGlobalization=true',
            '-p:IlcUseEnvironmentalTools=true',
            '-p:StripSymbols=true',
            '-o', $OutputDir
        ) + $publishProperties) -UseVcEnvironment

        if ($installerPublished) {
            $publishMode = 'native-aot'
        }
        else {
            Write-Host 'Native AOT installer publish failed on this machine. Falling back to managed single-file publish.' -ForegroundColor Yellow
        }
    }

    if (-not $installerPublished) {
        Write-Step 'Publishing NSearcher installer (managed single-file fallback)'
        $installerPublished = Invoke-DotNetPublish -Arguments (@(
            'publish',
            $installerProjectFile,
            '-c', $Configuration,
            '-r', $Runtime,
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:EnableCompressionInSingleFile=true',
            '-p:PublishTrimmed=true',
            '-p:InvariantGlobalization=true',
            '-o', $OutputDir
        ) + $publishProperties)
    }

    if (-not $installerPublished) {
        throw 'Installer publish failed.'
    }

    $exePath = Join-Path $OutputDir 'NSearcher.Setup.exe'
    if (-not (Test-Path $exePath)) {
        throw "Installer executable was not produced at $exePath"
    }

    return [pscustomobject]@{
        ProjectRoot = $ProjectRoot
        Runtime = $Runtime
        Configuration = $Configuration
        Version = $Version
        PublishMode = $publishMode
        OutputDir = $OutputDir
        ExePath = $exePath
    }
}
