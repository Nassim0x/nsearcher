param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version,
    [string]$OutputRoot,
    [switch]$DisableAot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publish-runtime.ps1')

function Format-InvariantNumber {
    param(
        [double]$Value,
        [string]$Format = '0.##'
    )

    return $Value.ToString($Format, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-ReleaseVersion {
    param(
        [string]$ProjectRoot,
        [string]$RequestedVersion
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion)) {
        return $RequestedVersion.Trim()
    }

    $describedVersion = (& git -C $ProjectRoot describe --tags --always 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($describedVersion)) {
        return $describedVersion.Trim()
    }

    return 'dev'
}

function Get-SafeFileToken {
    param([string]$Value)

    $safeValue = ($Value -replace '[^\w\.-]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safeValue)) {
        return 'dev'
    }

    return $safeValue
}

function Initialize-Directory {
    param([string]$Path)

    if (Test-Path $Path) {
        Get-ChildItem -Path $Path -Force | Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Get-BenchmarkSnapshot {
    param([string]$ProjectRoot)

    $benchmarkPath = Join-Path $ProjectRoot 'artifacts\benchmarks\latest.json'
    if (-not (Test-Path $benchmarkPath)) {
        return $null
    }

    return Get-Content -Raw $benchmarkPath | ConvertFrom-Json
}

function New-BenchmarkSummaryObject {
    param([psobject]$BenchmarkSnapshot)

    if ($null -eq $BenchmarkSnapshot) {
        return $null
    }

    return [ordered]@{
        GeneratedAtUtc = $BenchmarkSnapshot.GeneratedAtUtc
        EngineGeometricMeanSpeedup = [Math]::Round([double]$BenchmarkSnapshot.Aggregate.GeometricMeanSpeedup, 2)
        RipgrepGeometricMeanSpeedup = if ($BenchmarkSnapshot.RipgrepComparison) { [Math]::Round([double]$BenchmarkSnapshot.RipgrepComparison.GeometricMeanSpeedup, 2) } else { $null }
        GrepGeometricMeanSpeedup = if ($BenchmarkSnapshot.GrepComparison) { [Math]::Round([double]$BenchmarkSnapshot.GrepComparison.GeometricMeanSpeedup, 2) } else { $null }
        UgrepGeometricMeanSpeedup = if ($BenchmarkSnapshot.UgrepComparison) { [Math]::Round([double]$BenchmarkSnapshot.UgrepComparison.GeometricMeanSpeedup, 2) } else { $null }
        BestScenario = $BenchmarkSnapshot.Aggregate.BestScenario
        BestImprovementPercent = [Math]::Round([double]$BenchmarkSnapshot.Aggregate.BestImprovementPercent, 1)
    }
}

function Write-ReleaseNotes {
    param(
        [string]$Path,
        [string]$Version,
        [string]$Runtime,
        [string]$BundleArchiveName,
        [psobject]$PublishResult,
        [psobject]$BenchmarkSnapshot
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# NSearcher $Version")
    $lines.Add('')
    $lines.Add(('Windows release bundle for `{0}`.' -f $Runtime))
    $lines.Add('')
    $lines.Add('## Bundle Contents')
    $lines.Add('')
    $lines.Add('- `install.ps1` and `install.cmd` to install the bundle into `%LOCALAPPDATA%\Programs\NSearcher`')
    $lines.Add('- `uninstall.ps1` and `uninstall.cmd` to remove the installation cleanly')
    $lines.Add('- `payload\` containing the published launcher and server binaries')
    $lines.Add('- `package-manifest.json` with build metadata and per-file hashes')
    $lines.Add('')
    $lines.Add('## Release Artifacts')
    $lines.Add('')
    $lines.Add(('- `{0}`' -f $BundleArchiveName))
    $lines.Add('- `SHA256SUMS.txt` for release asset verification')
    $lines.Add('- `package-manifest.json`')
    $lines.Add('- `RELEASE-NOTES.md`')
    $lines.Add('')
    $lines.Add('## Build Metadata')
    $lines.Add('')
    $lines.Add(('- publish mode: `{0}`' -f $PublishResult.PublishMode))
    $lines.Add(('- launcher mode: `{0}`' -f $PublishResult.LauncherMode))
    $lines.Add(('- runtime: `{0}`' -f $Runtime))
    $lines.Add(('- generated at: `{0}`' -f [DateTimeOffset]::UtcNow.ToString('u')))
    $lines.Add('')
    $lines.Add('## Installation')
    $lines.Add('')
    $lines.Add('PowerShell:')
    $lines.Add('')
    $lines.Add('```powershell')
    $lines.Add('.\install.ps1')
    $lines.Add('```')
    $lines.Add('')
    $lines.Add('Command Prompt:')
    $lines.Add('')
    $lines.Add('```bat')
    $lines.Add('install.cmd')
    $lines.Add('```')
    $lines.Add('')

    if ($null -ne $BenchmarkSnapshot) {
        $lines.Add('## Benchmark Snapshot')
        $lines.Add('')
        $lines.Add('- source: `artifacts/benchmarks/latest.json`')
        $lines.Add(('- generated at: `{0}`' -f $BenchmarkSnapshot.GeneratedAtUtc))
        $lines.Add(('- engine geometric mean speedup vs internal baseline: `{0}x`' -f (Format-InvariantNumber -Value ([double]$BenchmarkSnapshot.Aggregate.GeometricMeanSpeedup))))
        $lines.Add(('- CLI geometric mean speedup vs ripgrep: `{0}x`' -f (Format-InvariantNumber -Value ([double]$BenchmarkSnapshot.RipgrepComparison.GeometricMeanSpeedup))))
        $lines.Add(('- CLI geometric mean speedup vs grep: `{0}x`' -f (Format-InvariantNumber -Value ([double]$BenchmarkSnapshot.GrepComparison.GeometricMeanSpeedup))))
        $lines.Add(('- CLI geometric mean speedup vs ugrep: `{0}x`' -f (Format-InvariantNumber -Value ([double]$BenchmarkSnapshot.UgrepComparison.GeometricMeanSpeedup))))
        $lines.Add('')
    }

    $lines.Add('## Uninstall')
    $lines.Add('')
    $lines.Add('```powershell')
    $lines.Add('.\uninstall.ps1')
    $lines.Add('```')

    Set-Content -Path $Path -Value $lines -Encoding UTF8
}

function Write-PackageManifest {
    param(
        [string]$BundleRoot,
        [string]$Path,
        [string]$Version,
        [string]$Runtime,
        [psobject]$PublishResult,
        [psobject]$BenchmarkSnapshot
    )

    $files = Get-ChildItem -Path $BundleRoot -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $fullName = $_.FullName
            $relativePath = $fullName.Substring($BundleRoot.Length).TrimStart('\')

            [ordered]@{
                Path = $relativePath
                Size = $_.Length
                Sha256 = (Get-FileHash -Algorithm SHA256 -Path $fullName).Hash
            }
        }

    $manifest = [ordered]@{
        PackageId = 'NSearcher'
        Version = $Version
        Runtime = $Runtime
        GeneratedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        DefaultInstallDir = '%LOCALAPPDATA%\Programs\NSearcher'
        PublishMode = $PublishResult.PublishMode
        LauncherMode = $PublishResult.LauncherMode
        BenchmarkSnapshot = New-BenchmarkSummaryObject -BenchmarkSnapshot $BenchmarkSnapshot
        Files = $files
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

function Write-Sha256Sums {
    param(
        [string]$ReleaseDirectory,
        [string[]]$Paths
    )

    $lines = foreach ($path in $Paths) {
        $hash = Get-FileHash -Algorithm SHA256 -Path $path
        '{0} *{1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $path)
    }

    Set-Content -Path (Join-Path $ReleaseDirectory 'SHA256SUMS.txt') -Value $lines -Encoding Ascii
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$resolvedVersion = Get-ReleaseVersion -ProjectRoot $projectRoot -RequestedVersion $Version
$safeVersion = Get-SafeFileToken -Value $resolvedVersion

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot 'artifacts\releases'
}

$releaseDirectory = Join-Path (Join-Path $OutputRoot $safeVersion) $Runtime
$bundleName = "NSearcher-$safeVersion-$Runtime"
$bundleRoot = Join-Path $releaseDirectory $bundleName
$payloadRoot = Join-Path $bundleRoot 'payload'
$releaseNotesPath = Join-Path $releaseDirectory 'RELEASE-NOTES.md'
$manifestPath = Join-Path $releaseDirectory 'package-manifest.json'
$zipPath = Join-Path $releaseDirectory "$bundleName.zip"
$packagingRoot = Join-Path $projectRoot 'packaging\windows'

Write-Step "Packaging NSearcher $resolvedVersion for $Runtime"
New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
Initialize-Directory -Path $releaseDirectory
New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null

$publishResult = Publish-NSearcher `
    -ProjectRoot $projectRoot `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -DisableAot:$DisableAot `
    -Version $resolvedVersion

Copy-NSearcherInstallLayout -PublishResult $publishResult -Destination $payloadRoot -ExcludeSymbols
Copy-Item -Path (Join-Path $packagingRoot '*') -Destination $bundleRoot -Recurse -Force
Copy-Item -Path (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $bundleRoot 'LICENSE') -Force
Copy-Item -Path (Join-Path $projectRoot 'README.md') -Destination (Join-Path $bundleRoot 'README.md') -Force

$benchmarkSnapshot = Get-BenchmarkSnapshot -ProjectRoot $projectRoot
Write-ReleaseNotes `
    -Path (Join-Path $bundleRoot 'RELEASE-NOTES.md') `
    -Version $resolvedVersion `
    -Runtime $Runtime `
    -BundleArchiveName (Split-Path -Leaf $zipPath) `
    -PublishResult $publishResult `
    -BenchmarkSnapshot $benchmarkSnapshot

Write-PackageManifest `
    -BundleRoot $bundleRoot `
    -Path $manifestPath `
    -Version $resolvedVersion `
    -Runtime $Runtime `
    -PublishResult $publishResult `
    -BenchmarkSnapshot $benchmarkSnapshot

Copy-Item -Path (Join-Path $bundleRoot 'RELEASE-NOTES.md') -Destination $releaseNotesPath -Force
Copy-Item -Path $manifestPath -Destination (Join-Path $bundleRoot 'package-manifest.json') -Force

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path $bundleRoot -DestinationPath $zipPath -CompressionLevel Optimal
Write-Sha256Sums -ReleaseDirectory $releaseDirectory -Paths @($zipPath, $manifestPath, $releaseNotesPath)

Write-Step 'Package complete'
Write-Host "Bundle directory: $bundleRoot" -ForegroundColor Green
Write-Host "ZIP package: $zipPath" -ForegroundColor Green
Write-Host "Release notes: $releaseNotesPath" -ForegroundColor Green
Write-Host "Manifest: $manifestPath" -ForegroundColor Green
Write-Host "Checksums: $(Join-Path $releaseDirectory 'SHA256SUMS.txt')" -ForegroundColor Green
