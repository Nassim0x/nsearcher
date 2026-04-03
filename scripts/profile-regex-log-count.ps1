param(
    [int]$Iterations = 120,
    [int]$Concurrency = 6,
    [string]$ScenarioRoot = "",
    [string]$TraceOutput = "",
    [switch]$SkipCorpusGeneration
)

$ErrorActionPreference = "Stop"

function Get-ProjectRoot {
    Split-Path -Parent $PSScriptRoot
}

function Get-ArtifactPath {
    param([string]$RelativePath)

    $projectRoot = Get-ProjectRoot
    return Join-Path $projectRoot ("artifacts\" + $RelativePath)
}

function Get-DefaultPipeName {
    $sanitized = -join ([Environment]::UserName.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) } | ForEach-Object { [char]::ToLowerInvariant($_) })
    if ([string]::IsNullOrWhiteSpace($sanitized)) {
        $sanitized = "user"
    }

    return "nsearcher-$sanitized"
}

function New-RegexLogCorpus {
    param([string]$Root)

    if (Test-Path $Root) {
        Remove-Item $Root -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    for ($directoryIndex = 0; $directoryIndex -lt 4; $directoryIndex++) {
        $directory = Join-Path $Root ("logs-{0:D2}\archive" -f $directoryIndex)
        New-Item -ItemType Directory -Force -Path $directory | Out-Null

        for ($fileIndex = 0; $fileIndex -lt 6; $fileIndex++) {
            $hasMatch = $fileIndex -eq ($directoryIndex % 6)
            $path = Join-Path $directory ("app-{0:D3}.log" -f $fileIndex)
            $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)), 65536)

            try {
                for ($lineIndex = 0; $lineIndex -lt 18000; $lineIndex++) {
                    $writer.Write("2026-04-02T12:34:")
                    $writer.Write(($lineIndex % 60).ToString("D2"))
                    $writer.Write("Z INFO svc=")
                    $writer.Write($directoryIndex.ToString("D2"))
                    $writer.Write(" file=")
                    $writer.Write($fileIndex.ToString("D2"))
                    $writer.Write(" request completed trace=abcdef0123456789")

                    if ($hasMatch -and $lineIndex -eq 4000) {
                        $writer.Write(" ERR-1042 timeout during upstream call")
                    }
                    elseif ($hasMatch -and $lineIndex -eq 15000) {
                        $writer.Write(" ERR-2048 cache refresh failed")
                    }

                    $writer.Write("`n")
                }
            }
            finally {
                $writer.Dispose()
                $stream.Dispose()
            }
        }
    }
}

function Invoke-RegexLoad {
    param(
        [string]$ClientPath,
        [string]$Root,
        [int]$Count
    )

    for ($iteration = 0; $iteration -lt $Count; $iteration++) {
        $null = & $ClientPath "ERR-\d{4}" $Root --regex -s -c --no-color 2>$null
    }
}

$projectRoot = Get-ProjectRoot
$traceTool = Join-Path $projectRoot ".tools\dotnet-trace.exe"
$serverPath = Join-Path $projectRoot "NSearcher.Cli\bin\Release\net10.0\NSearcher.Server.exe"
$clientPath = Join-Path $env:LOCALAPPDATA "Programs\NSearcher\NSearcher.exe"

if (-not (Test-Path $traceTool)) {
    throw "dotnet-trace was not found at '$traceTool'."
}

if (-not (Test-Path $serverPath)) {
    throw "Managed server build not found at '$serverPath'. Build the solution in Release first."
}

if (-not (Test-Path $clientPath)) {
    throw "Installed NSearcher client not found at '$clientPath'."
}

if ([string]::IsNullOrWhiteSpace($ScenarioRoot)) {
    $ScenarioRoot = Get-ArtifactPath "profiling\regex-log-count"
}

if ([string]::IsNullOrWhiteSpace($TraceOutput)) {
    $TraceOutput = Get-ArtifactPath "profiling\regex-log-count.nettrace"
}

$reportOutput = [System.IO.Path]::ChangeExtension($TraceOutput, ".topN.txt")
$traceDirectory = Split-Path -Parent $TraceOutput
if (-not [string]::IsNullOrWhiteSpace($traceDirectory)) {
    New-Item -ItemType Directory -Force -Path $traceDirectory | Out-Null
}

if (-not $SkipCorpusGeneration) {
    New-RegexLogCorpus -Root $ScenarioRoot
}

$existingServers = Get-Process NSearcher.Server -ErrorAction SilentlyContinue
if ($existingServers) {
    $existingServers | Stop-Process -Force -ErrorAction SilentlyContinue
}

$pipeName = Get-DefaultPipeName
    $daemon = Start-Process -FilePath $serverPath -ArgumentList @("--internal-daemon", $pipeName) -PassThru -WindowStyle Hidden

try {
    Start-Sleep -Milliseconds 200

    $jobCount = [Math]::Max(1, $Concurrency)
    $baseIterationsPerJob = [Math]::Floor($Iterations / $jobCount)
    $remainder = $Iterations % $jobCount
    $loadJobs = New-Object System.Collections.Generic.List[object]

    for ($jobIndex = 0; $jobIndex -lt $jobCount; $jobIndex++) {
        $jobIterations = $baseIterationsPerJob
        if ($jobIndex -lt $remainder) {
            $jobIterations++
        }

        if ($jobIterations -le 0) {
            continue
        }

        $job = Start-Job -ScriptBlock {
            param($ClientPath, $Root, $Count)
            for ($iteration = 0; $iteration -lt $Count; $iteration++) {
                & $ClientPath "ERR-\d{4}" $Root --regex -s -c --no-color 2>$null | Out-Null
            }
        } -ArgumentList $clientPath, $ScenarioRoot, $jobIterations

        $loadJobs.Add($job)
    }

    try {
        & $traceTool collect --process-id $daemon.Id --profile dotnet-sampled-thread-time --duration 00:00:08 --output $TraceOutput | Out-Host
    }
    finally {
        if ($loadJobs.Count -gt 0) {
            Wait-Job -Job $loadJobs | Out-Null
            Receive-Job -Job $loadJobs | Out-Null
            Remove-Job -Job $loadJobs -Force
        }
    }

    & $traceTool report $TraceOutput topN --number 40 | Tee-Object -FilePath $reportOutput

    Write-Host ""
    Write-Host "Trace:  $TraceOutput"
    Write-Host "Report: $reportOutput"
    Write-Host "Corpus: $ScenarioRoot"
}
finally {
    if (-not $daemon.HasExited) {
        $daemon.Kill()
        $daemon.WaitForExit()
    }
}
