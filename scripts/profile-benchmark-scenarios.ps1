param(
    [string[]]$Scenario = @("all"),
    [string]$TraceRoot = "",
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

function Resolve-ScenarioNames {
    param([string[]]$RequestedScenarios)

    $catalog = @(
        "literal-huge-presence",
        "literal-count-lines",
        "literal-many-small-files",
        "regex-log-count"
    )

    if ($RequestedScenarios.Count -eq 1 -and $RequestedScenarios[0] -eq "all") {
        return $catalog
    }

    foreach ($name in $RequestedScenarios) {
        if ($catalog -notcontains $name) {
            throw "Unknown scenario '$name'."
        }
    }

    return $RequestedScenarios
}

function Get-ScenarioConfiguration {
    param([string]$Name)

    switch ($Name) {
        "literal-huge-presence" {
            return [pscustomobject]@{
                Name = $Name
                Pattern = "NEEDLE-2026"
                Arguments = @("-s", "-l", "--no-color")
                Iterations = 96
                Concurrency = 4
                Duration = "00:00:08"
            }
        }
        "literal-count-lines" {
            return [pscustomobject]@{
                Name = $Name
                Pattern = "NEEDLE-2026"
                Arguments = @("-s", "-c", "--no-color")
                Iterations = 480
                Concurrency = 8
                Duration = "00:00:08"
            }
        }
        "literal-many-small-files" {
            return [pscustomobject]@{
                Name = $Name
                Pattern = "NEEDLE-2026"
                Arguments = @("-s", "-l", "--no-color")
                Iterations = 320
                Concurrency = 8
                Duration = "00:00:08"
            }
        }
        "regex-log-count" {
            return [pscustomobject]@{
                Name = $Name
                Pattern = "ERR-\d{4}"
                Arguments = @("--regex", "-s", "-c", "--no-color")
                Iterations = 480
                Concurrency = 8
                Duration = "00:00:08"
            }
        }
        default {
            throw "Unsupported scenario '$Name'."
        }
    }
}

function Get-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function Write-HugeUtf8Document {
    param(
        [string]$FilePath,
        [bool]$IncludeMatch
    )

    $segmentCount = 1200000
    $matchSegment = 600000
    $basePrefix = "entrée_日本語_данные_بحث_δοκιμή:"
    $basePayload = "αβγδεζηθ_日本語_данные_بحث_élément_payload_payload_payload_0123456789|"

    $stream = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = New-Object System.IO.StreamWriter($stream, (Get-Utf8NoBomEncoding), 131072)

    try {
        for ($index = 0; $index -lt $segmentCount; $index++) {
            $writer.Write($basePrefix)
            $writer.Write($index.ToString("D6"))
            $writer.Write(':')

            if ($IncludeMatch -and $index -eq $matchSegment) {
                $writer.Write("exception_trace_")
                $writer.Write("NEEDLE-2026")
                $writer.Write("_payload_payload_payload|")
            }
            else {
                $writer.Write($basePayload)
            }
        }

        $writer.Write("`n")
        $writer.Flush()
        return $stream.Length
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-LiteralCountFile {
    param(
        [string]$FilePath,
        [bool]$IncludeMatch,
        [int]$DirectoryIndex,
        [int]$FileIndex
    )

    $lineCount = 16000
    $firstMatchLine = 2300
    $secondMatchLine = 12900

    $stream = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = New-Object System.IO.StreamWriter($stream, (Get-Utf8NoBomEncoding), 65536)

    try {
        for ($lineIndex = 0; $lineIndex -lt $lineCount; $lineIndex++) {
            $writer.Write("module/service/handler/")
            $writer.Write($DirectoryIndex.ToString("D2"))
            $writer.Write('/')
            $writer.Write($FileIndex.ToString("D2"))
            $writer.Write(':')
            $writer.Write($lineIndex.ToString("D5"))
            $writer.Write(" parsing_token_sequence_payload_payload_0123456789_abcdefghijklmnopqrstuvwxyz")

            if ($IncludeMatch -and $lineIndex -eq $firstMatchLine) {
                $writer.Write(" context=NEEDLE-2026 and_again=NEEDLE-2026")
            }
            elseif ($IncludeMatch -and $lineIndex -eq $secondMatchLine) {
                $writer.Write(" match=NEEDLE-2026")
            }

            $writer.Write("`n")
        }

        $writer.Flush()
        return $stream.Length
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-SmallTextFile {
    param(
        [string]$FilePath,
        [bool]$IncludeMatch,
        [int]$FileOrdinal
    )

    $lineCount = 120
    $matchLine = 73

    $stream = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = New-Object System.IO.StreamWriter($stream, (Get-Utf8NoBomEncoding), 16384)

    try {
        for ($lineIndex = 0; $lineIndex -lt $lineCount; $lineIndex++) {
            $writer.Write("spec_file=")
            $writer.Write($FileOrdinal.ToString("D5"))
            $writer.Write(" line=")
            $writer.Write($lineIndex.ToString("D3"))
            $writer.Write(" payload payload payload 0123456789 abcdefghijklmnopqrstuvwxyz")

            if ($IncludeMatch -and $lineIndex -eq $matchLine) {
                $writer.Write(" literal=NEEDLE-2026")
            }

            $writer.Write("`n")
        }

        $writer.Flush()
        return $stream.Length
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-RegexLogFile {
    param(
        [string]$FilePath,
        [bool]$IncludeMatch,
        [int]$DirectoryIndex,
        [int]$FileIndex
    )

    $lineCount = 18000
    $firstMatchLine = 4000
    $secondMatchLine = 15000

    $stream = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $writer = New-Object System.IO.StreamWriter($stream, (Get-Utf8NoBomEncoding), 65536)

    try {
        for ($lineIndex = 0; $lineIndex -lt $lineCount; $lineIndex++) {
            $writer.Write("2026-04-02T12:34:")
            $writer.Write(($lineIndex % 60).ToString("D2"))
            $writer.Write("Z INFO svc=")
            $writer.Write($DirectoryIndex.ToString("D2"))
            $writer.Write(" file=")
            $writer.Write($FileIndex.ToString("D2"))
            $writer.Write(" request completed trace=abcdef0123456789")

            if ($IncludeMatch -and $lineIndex -eq $firstMatchLine) {
                $writer.Write(" ERR-1042 timeout during upstream call")
            }
            elseif ($IncludeMatch -and $lineIndex -eq $secondMatchLine) {
                $writer.Write(" ERR-2048 cache refresh failed")
            }

            $writer.Write("`n")
        }

        $writer.Flush()
        return $stream.Length
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function New-ScenarioCorpus {
    param(
        [string]$ScenarioName,
        [string]$Root
    )

    if (Test-Path $Root) {
        Remove-Item $Root -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $totalBytes = 0L

    switch ($ScenarioName) {
        "literal-huge-presence" {
            $remainingMatches = 1
            for ($directoryIndex = 0; $directoryIndex -lt 2; $directoryIndex++) {
                $directory = Join-Path $Root ("group-{0:D2}\nested\leaf" -f $directoryIndex)
                New-Item -ItemType Directory -Force -Path $directory | Out-Null

                for ($fileIndex = 0; $fileIndex -lt 2; $fileIndex++) {
                    $hasMatch = $remainingMatches -gt 0 -and $fileIndex -eq ($directoryIndex % 2)
                    $path = Join-Path $directory ("file-{0:D3}.txt" -f $fileIndex)
                    $totalBytes += Write-HugeUtf8Document -FilePath $path -IncludeMatch:$hasMatch
                    if ($hasMatch) {
                        $remainingMatches--
                    }
                }
            }
        }
        "literal-count-lines" {
            for ($directoryIndex = 0; $directoryIndex -lt 6; $directoryIndex++) {
                $directory = Join-Path $Root ("svc-{0:D2}\src\handlers" -f $directoryIndex)
                New-Item -ItemType Directory -Force -Path $directory | Out-Null

                for ($fileIndex = 0; $fileIndex -lt 8; $fileIndex++) {
                    $hasMatch = $fileIndex -eq ($directoryIndex % 8)
                    $path = Join-Path $directory ("handler-{0:D3}.log" -f $fileIndex)
                    $totalBytes += Write-LiteralCountFile -FilePath $path -IncludeMatch:$hasMatch -DirectoryIndex $directoryIndex -FileIndex $fileIndex
                }
            }
        }
        "literal-many-small-files" {
            $fileOrdinal = 0
            $remainingMatches = 120

            for ($groupIndex = 0; $groupIndex -lt 15; $groupIndex++) {
                for ($branchIndex = 0; $branchIndex -lt 5; $branchIndex++) {
                    $directory = Join-Path $Root ("pkg-{0:D2}\feature-{1:D2}\tests" -f $groupIndex, $branchIndex)
                    New-Item -ItemType Directory -Force -Path $directory | Out-Null

                    for ($fileIndex = 0; $fileIndex -lt 20; $fileIndex++) {
                        $hasMatch = $remainingMatches -gt 0 -and ($fileOrdinal % 10) -eq 0
                        $path = Join-Path $directory ("spec-{0:D3}.txt" -f $fileIndex)
                        $totalBytes += Write-SmallTextFile -FilePath $path -IncludeMatch:$hasMatch -FileOrdinal $fileOrdinal

                        if ($hasMatch) {
                            $remainingMatches--
                        }

                        $fileOrdinal++
                    }
                }
            }
        }
        "regex-log-count" {
            for ($directoryIndex = 0; $directoryIndex -lt 4; $directoryIndex++) {
                $directory = Join-Path $Root ("logs-{0:D2}\archive" -f $directoryIndex)
                New-Item -ItemType Directory -Force -Path $directory | Out-Null

                for ($fileIndex = 0; $fileIndex -lt 6; $fileIndex++) {
                    $hasMatch = $fileIndex -eq ($directoryIndex % 6)
                    $path = Join-Path $directory ("app-{0:D3}.log" -f $fileIndex)
                    $totalBytes += Write-RegexLogFile -FilePath $path -IncludeMatch:$hasMatch -DirectoryIndex $directoryIndex -FileIndex $fileIndex
                }
            }
        }
        default {
            throw "Unsupported scenario '$ScenarioName'."
        }
    }

    return $totalBytes
}

function Invoke-ScenarioLoad {
    param(
        [string]$ClientPath,
        [string]$Root,
        [pscustomobject]$Configuration
    )

    & $ClientPath $Configuration.Pattern $Root @($Configuration.Arguments) 2>$null | Out-Null
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

if ([string]::IsNullOrWhiteSpace($TraceRoot)) {
    $TraceRoot = Get-ArtifactPath "profiling\scenarios"
}

New-Item -ItemType Directory -Force -Path $TraceRoot | Out-Null

$scenarioNames = Resolve-ScenarioNames -RequestedScenarios $Scenario
$pipeName = Get-DefaultPipeName

foreach ($scenarioName in $scenarioNames) {
    $config = Get-ScenarioConfiguration -Name $scenarioName
    $scenarioRoot = Join-Path $TraceRoot $scenarioName
    $tracePath = Join-Path $TraceRoot ($scenarioName + ".nettrace")
    $exclusiveReportPath = Join-Path $TraceRoot ($scenarioName + ".topN.txt")
    $inclusiveReportPath = Join-Path $TraceRoot ($scenarioName + ".inclusive-topN.txt")

    Write-Host ""
    Write-Host "==> Profiling $scenarioName" -ForegroundColor Cyan

    if (-not $SkipCorpusGeneration) {
        $totalBytes = New-ScenarioCorpus -ScenarioName $scenarioName -Root $scenarioRoot
        Write-Host ("    Corpus: {0:N1} MiB" -f ($totalBytes / 1MB)) -ForegroundColor DarkGray
    }

    $existingServers = Get-Process NSearcher.Server -ErrorAction SilentlyContinue
    if ($existingServers) {
        $existingServers | Stop-Process -Force -ErrorAction SilentlyContinue
    }

    $daemon = Start-Process -FilePath $serverPath -ArgumentList @("--internal-daemon", $pipeName) -PassThru -WindowStyle Hidden

    try {
        Start-Sleep -Milliseconds 200

        $jobCount = [Math]::Max(1, $config.Concurrency)
        $baseIterationsPerJob = [Math]::Floor($config.Iterations / $jobCount)
        $remainder = $config.Iterations % $jobCount
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
                param($ClientPath, $Root, $Pattern, $Arguments, $Count)
                for ($iteration = 0; $iteration -lt $Count; $iteration++) {
                    & $ClientPath $Pattern $Root @($Arguments) 2>$null | Out-Null
                }
            } -ArgumentList $clientPath, $scenarioRoot, $config.Pattern, $config.Arguments, $jobIterations

            $loadJobs.Add($job)
        }

        try {
            & $traceTool collect --process-id $daemon.Id --profile dotnet-sampled-thread-time --duration $config.Duration --output $tracePath | Out-Host
        }
        finally {
            if ($loadJobs.Count -gt 0) {
                Wait-Job -Job $loadJobs | Out-Null
                Receive-Job -Job $loadJobs | Out-Null
                Remove-Job -Job $loadJobs -Force
            }
        }

        & $traceTool report $tracePath topN --number 40 | Tee-Object -FilePath $exclusiveReportPath | Out-Host
        & $traceTool report $tracePath topN --number 40 --inclusive | Tee-Object -FilePath $inclusiveReportPath | Out-Host
    }
    finally {
        if (-not $daemon.HasExited) {
            $daemon.Kill()
            $daemon.WaitForExit()
        }
    }
}
