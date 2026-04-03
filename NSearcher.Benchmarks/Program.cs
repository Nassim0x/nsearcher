using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NSearcher.Core;

namespace NSearcher.Benchmarks;

internal static class Program
{
    private const string LiteralNeedle = "NEEDLE-2026";
    private const int DefaultWarmupRuns = 1;
    private const int DefaultMeasuredRuns = 6;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly BenchmarkScenario[] ScenarioCatalog =
    [
        new(
            "literal-huge-presence",
            "Rare literal in huge multilingual UTF-8 documents with files-with-matches mode",
            CreateHugeUtf8PresenceCorpus,
            static root => CreateLiteralOptions(root, filesWithMatches: true, countOnly: false),
            new BenchmarkExpectation(1, 1, 1)),
        new(
            "literal-count-lines",
            "Rare literal in large code-like line corpus with count-only mode",
            CreateLiteralCountCorpus,
            static root => CreateLiteralOptions(root, filesWithMatches: false, countOnly: true),
            new BenchmarkExpectation(6, 12, 18)),
        new(
            "literal-many-small-files",
            "Recursive literal search across many small nested files",
            CreateManySmallFilesCorpus,
            static root => CreateLiteralOptions(root, filesWithMatches: true, countOnly: false),
            new BenchmarkExpectation(120, 120, 120)),
        new(
            "regex-log-count",
            "Regex search over structured log files with count-only mode",
            CreateRegexLogCorpus,
            static root => CreateRegexOptions(root),
            new BenchmarkExpectation(4, 8, 8))
    ];

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var settings, out var errorMessage))
        {
            Console.Error.WriteLine(errorMessage);
            Console.Error.WriteLine();
            WriteHelp();
            return 1;
        }

        if (settings.ShowHelp)
        {
            WriteHelp();
            return 0;
        }

        if (settings.ListScenarios)
        {
            WriteScenarioList();
            return 0;
        }

        var selectedScenarios = ResolveScenarios(settings.ScenarioNames);
        if (selectedScenarios is null)
        {
            return 1;
        }

        CliToolPaths? cliToolPaths = null;
        if (settings.CompareWithRipgrep || settings.CompareWithGrep || settings.CompareWithUgrep)
        {
            cliToolPaths = CliComparisonRunner.ResolveToolPaths(
                settings.NSearcherCliPath,
                settings.RipgrepPath,
                settings.GrepPath,
                settings.UgrepPath,
                settings.CompareWithRipgrep,
                settings.CompareWithGrep,
                settings.CompareWithUgrep);
            if (cliToolPaths is null)
            {
                Console.Error.WriteLine("Unable to resolve the requested CLI executables for comparison.");
                return 1;
            }
        }

        var suiteRoot = Path.Combine(Path.GetTempPath(), "nsearcher-benchmark-suite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suiteRoot);

        try
        {
            Console.WriteLine($"Benchmark suite root: {suiteRoot}");
            Console.WriteLine($"Warmup runs: {settings.WarmupRuns}, measured runs: {settings.MeasuredRuns}");
            if (cliToolPaths is not null)
            {
                Console.WriteLine($"CLI compare: NSearcher={cliToolPaths.Value.NSearcherPath}");
                if (cliToolPaths.Value.RipgrepPath is not null)
                {
                    Console.WriteLine($"CLI compare: ripgrep={cliToolPaths.Value.RipgrepPath}");
                }

                if (cliToolPaths.Value.GrepPath is not null)
                {
                    Console.WriteLine($"CLI compare: grep={cliToolPaths.Value.GrepPath}");
                }

                if (cliToolPaths.Value.UgrepPath is not null)
                {
                    Console.WriteLine($"CLI compare: ugrep={cliToolPaths.Value.UgrepPath}");
                }
            }

            var baselineEngine = new BaselineSearchEngine();
            var optimizedEngine = new SearchEngine();
            var scenarioResults = new List<ScenarioBenchmarkResult>(selectedScenarios.Length);

            foreach (var scenario in selectedScenarios)
            {
                var result = await RunScenarioAsync(
                    scenario,
                    suiteRoot,
                    settings,
                    baselineEngine,
                    optimizedEngine,
                    cliToolPaths);

                scenarioResults.Add(result);
                WriteScenarioResult(result);
            }

            var report = BuildReport(settings, scenarioResults);
            var artifactInfo = WriteArtifacts(report, settings.JsonOutputPath);
            WriteSummary(report, artifactInfo);
            return 0;
        }
        finally
        {
            if (Directory.Exists(suiteRoot))
            {
                Directory.Delete(suiteRoot, recursive: true);
            }
        }
    }

    private static async Task<ScenarioBenchmarkResult> RunScenarioAsync(
        BenchmarkScenario scenario,
        string suiteRoot,
        BenchmarkSettings settings,
        BaselineSearchEngine baselineEngine,
        SearchEngine optimizedEngine,
        CliToolPaths? cliToolPaths)
    {
        var scenarioRoot = Path.Combine(suiteRoot, scenario.Name);
        Directory.CreateDirectory(scenarioRoot);

        var corpus = scenario.CreateCorpus(scenarioRoot);
        var options = scenario.CreateOptions(scenarioRoot);

        Console.WriteLine();
        Console.WriteLine($"Scenario [{scenario.Name}]");
        Console.WriteLine($"  {scenario.Description}");
        Console.WriteLine(
            $"  Corpus: {corpus.FileCount} files, {corpus.TotalBytes / (1024.0 * 1024.0):F1} MiB, {corpus.MatchingFiles} matching files");

        await WarmUpAsync(baselineEngine, optimizedEngine, options, scenario.Expectation.Validate, settings.WarmupRuns);

        var baselineMeasurement = await MeasureAsync(
            () => baselineEngine.ExecuteAsync(options),
            scenario.Expectation.Validate,
            settings.MeasuredRuns);

        var optimizedMeasurement = await MeasureAsync(
            () => optimizedEngine.ExecuteAsync(options),
            scenario.Expectation.Validate,
            settings.MeasuredRuns);

        var baselineMedian = Median(baselineMeasurement.RunsMs);
        var optimizedMedian = Median(optimizedMeasurement.RunsMs);
        var improvementPercent = ((baselineMedian - optimizedMedian) / baselineMedian) * 100.0;
        var speedupFactor = baselineMedian / optimizedMedian;
        CliComparisonResult? cliComparison = null;

        if (cliToolPaths is not null)
        {
            cliComparison = await CliComparisonRunner.CompareAsync(
                scenario.Name,
                scenarioRoot,
                options,
                new CliComparisonExpectation(
                    (int)scenario.Expectation.ExpectedFilesMatched,
                    (int)scenario.Expectation.ExpectedMatchLines),
                cliToolPaths.Value,
                settings.WarmupRuns,
                settings.MeasuredRuns);
        }

        return new ScenarioBenchmarkResult(
            scenario.Name,
            scenario.Description,
            DescribeOptions(options),
            corpus,
            baselineMeasurement,
            optimizedMeasurement,
            baselineMedian,
            optimizedMedian,
            improvementPercent,
            speedupFactor,
            cliComparison);
    }

    private static async Task WarmUpAsync(
        BaselineSearchEngine baselineEngine,
        SearchEngine optimizedEngine,
        SearchOptions options,
        Action<SearchSummary> validateSummary,
        int warmupRuns)
    {
        for (var iteration = 0; iteration < warmupRuns; iteration++)
        {
            validateSummary(await baselineEngine.ExecuteAsync(options));
            validateSummary(await optimizedEngine.ExecuteAsync(options));
        }
    }

    private static async Task<EngineMeasurement> MeasureAsync(
        Func<Task<SearchSummary>> runner,
        Action<SearchSummary> validateSummary,
        int measuredRuns)
    {
        var results = new double[measuredRuns];
        SearchSummary? lastSummary = null;

        for (var iteration = 0; iteration < measuredRuns; iteration++)
        {
            ForceGc();
            var stopwatch = Stopwatch.StartNew();
            var summary = await runner();
            stopwatch.Stop();

            validateSummary(summary);
            lastSummary = summary;
            results[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        return new EngineMeasurement(results, lastSummary ?? throw new InvalidOperationException("No benchmark runs were executed."));
    }

    private static SearchOptions CreateLiteralOptions(string root, bool filesWithMatches, bool countOnly) =>
        new()
        {
            Pattern = LiteralNeedle,
            Paths = [root],
            CaseMode = CaseMode.Sensitive,
            FilesWithMatches = filesWithMatches,
            CountOnly = countOnly,
            UseColor = false,
            ReportErrors = false,
            UseDefaultExcludes = false,
            MaxDegreeOfParallelism = 0
        };

    private static SearchOptions CreateRegexOptions(string root) =>
        new()
        {
            Pattern = @"ERR-\d{4}",
            Paths = [root],
            UseRegex = true,
            CaseMode = CaseMode.Sensitive,
            CountOnly = true,
            UseColor = false,
            ReportErrors = false,
            UseDefaultExcludes = false,
            MaxDegreeOfParallelism = 0
        };

    private static CorpusInfo CreateHugeUtf8PresenceCorpus(string root)
    {
        const int directoryCount = 2;
        const int filesPerDirectory = 2;
        const int matchingFiles = 1;
        long totalBytes = 0;
        var remainingMatches = matchingFiles;

        for (var directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
        {
            var directory = Path.Combine(root, $"group-{directoryIndex:D2}", "nested", "leaf");
            Directory.CreateDirectory(directory);

            for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
            {
                var hasMatch = remainingMatches > 0 && fileIndex == directoryIndex % filesPerDirectory;
                var filePath = Path.Combine(directory, $"file-{fileIndex:D3}.txt");
                totalBytes += WriteHugeUtf8Document(filePath, hasMatch);

                if (hasMatch)
                {
                    remainingMatches--;
                }
            }
        }

        return new CorpusInfo(directoryCount * filesPerDirectory, matchingFiles, totalBytes);
    }

    private static long WriteHugeUtf8Document(string filePath, bool includeMatch)
    {
        const int segmentCount = 1_200_000;
        const int matchSegment = 600_000;
        const string basePrefix = "\u00E9ntr\u00E9e_\u65E5\u672C\u8A9E_\u0434\u0430\u043D\u043D\u044B\u0435_\u0628\u062D\u062B_\u03B4\u03BF\u03BA\u03B9\u03BC\u03AE:";
        const string basePayload = "\u03B1\u03B2\u03B3\u03B4\u03B5\u03B6\u03B7\u03B8_\u65E5\u672C\u8A9E_\u0434\u0430\u043D\u043D\u044B\u0435_\u0628\u062D\u062B_\u00E9l\u00E9ment_payload_payload_payload_0123456789|";

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024);
        using var writer = new StreamWriter(stream, Utf8NoBom, 128 * 1024, leaveOpen: true);

        for (var index = 0; index < segmentCount; index++)
        {
            writer.Write(basePrefix);
            writer.Write(index.ToString("D6"));
            writer.Write(':');

            if (includeMatch && index == matchSegment)
            {
                writer.Write("exception_trace_");
                writer.Write(LiteralNeedle);
                writer.Write("_payload_payload_payload|");
            }
            else
            {
                writer.Write(basePayload);
            }
        }

        writer.Write('\n');
        writer.Flush();
        return stream.Length;
    }

    private static CorpusInfo CreateLiteralCountCorpus(string root)
    {
        const int directoryCount = 6;
        const int filesPerDirectory = 8;
        const int matchingFiles = 6;
        long totalBytes = 0;

        for (var directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
        {
            var directory = Path.Combine(root, $"svc-{directoryIndex:D2}", "src", "handlers");
            Directory.CreateDirectory(directory);

            for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
            {
                var hasMatch = fileIndex == directoryIndex % filesPerDirectory;
                var filePath = Path.Combine(directory, $"handler-{fileIndex:D3}.log");
                totalBytes += WriteLiteralCountFile(filePath, hasMatch, directoryIndex, fileIndex);
            }
        }

        return new CorpusInfo(directoryCount * filesPerDirectory, matchingFiles, totalBytes);
    }

    private static long WriteLiteralCountFile(string filePath, bool includeMatch, int directoryIndex, int fileIndex)
    {
        const int lineCount = 16_000;
        const int firstMatchLine = 2_300;
        const int secondMatchLine = 12_900;

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var writer = new StreamWriter(stream, Utf8NoBom, 64 * 1024, leaveOpen: true);

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            writer.Write("module/service/handler/");
            writer.Write(directoryIndex.ToString("D2"));
            writer.Write('/');
            writer.Write(fileIndex.ToString("D2"));
            writer.Write(':');
            writer.Write(lineIndex.ToString("D5"));
            writer.Write(" parsing_token_sequence_payload_payload_0123456789_abcdefghijklmnopqrstuvwxyz");

            if (includeMatch && lineIndex == firstMatchLine)
            {
                writer.Write(" context=");
                writer.Write(LiteralNeedle);
                writer.Write(" and_again=");
                writer.Write(LiteralNeedle);
            }
            else if (includeMatch && lineIndex == secondMatchLine)
            {
                writer.Write(" match=");
                writer.Write(LiteralNeedle);
            }

            writer.Write('\n');
        }

        writer.Flush();
        return stream.Length;
    }

    private static CorpusInfo CreateManySmallFilesCorpus(string root)
    {
        const int groupCount = 15;
        const int branchCount = 5;
        const int filesPerBranch = 20;
        const int matchingFiles = 120;
        const int matchStride = 10;
        long totalBytes = 0;
        var fileOrdinal = 0;
        var remainingMatches = matchingFiles;

        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            for (var branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                var directory = Path.Combine(root, $"pkg-{groupIndex:D2}", $"feature-{branchIndex:D2}", "tests");
                Directory.CreateDirectory(directory);

                for (var fileIndex = 0; fileIndex < filesPerBranch; fileIndex++)
                {
                    var hasMatch = remainingMatches > 0 && fileOrdinal % matchStride == 0;
                    var filePath = Path.Combine(directory, $"spec-{fileIndex:D3}.txt");
                    totalBytes += WriteSmallTextFile(filePath, hasMatch, fileOrdinal);

                    if (hasMatch)
                    {
                        remainingMatches--;
                    }

                    fileOrdinal++;
                }
            }
        }

        return new CorpusInfo(groupCount * branchCount * filesPerBranch, matchingFiles, totalBytes);
    }

    private static long WriteSmallTextFile(string filePath, bool includeMatch, int fileOrdinal)
    {
        const int lineCount = 120;
        const int matchLine = 73;

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024);
        using var writer = new StreamWriter(stream, Utf8NoBom, 16 * 1024, leaveOpen: true);

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            writer.Write("spec_file=");
            writer.Write(fileOrdinal.ToString("D5"));
            writer.Write(" line=");
            writer.Write(lineIndex.ToString("D3"));
            writer.Write(" payload payload payload 0123456789 abcdefghijklmnopqrstuvwxyz");

            if (includeMatch && lineIndex == matchLine)
            {
                writer.Write(" literal=");
                writer.Write(LiteralNeedle);
            }

            writer.Write('\n');
        }

        writer.Flush();
        return stream.Length;
    }

    private static CorpusInfo CreateRegexLogCorpus(string root)
    {
        const int directoryCount = 4;
        const int filesPerDirectory = 6;
        const int matchingFiles = 4;
        long totalBytes = 0;

        for (var directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
        {
            var directory = Path.Combine(root, $"logs-{directoryIndex:D2}", "archive");
            Directory.CreateDirectory(directory);

            for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
            {
                var hasMatch = fileIndex == directoryIndex % filesPerDirectory;
                var filePath = Path.Combine(directory, $"app-{fileIndex:D3}.log");
                totalBytes += WriteRegexLogFile(filePath, hasMatch, directoryIndex, fileIndex);
            }
        }

        return new CorpusInfo(directoryCount * filesPerDirectory, matchingFiles, totalBytes);
    }

    private static long WriteRegexLogFile(string filePath, bool includeMatch, int directoryIndex, int fileIndex)
    {
        const int lineCount = 18_000;
        const int firstMatchLine = 4_000;
        const int secondMatchLine = 15_000;

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var writer = new StreamWriter(stream, Utf8NoBom, 64 * 1024, leaveOpen: true);

        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            writer.Write("2026-04-02T12:34:");
            writer.Write((lineIndex % 60).ToString("D2"));
            writer.Write("Z INFO svc=");
            writer.Write(directoryIndex.ToString("D2"));
            writer.Write(" file=");
            writer.Write(fileIndex.ToString("D2"));
            writer.Write(" request completed trace=abcdef0123456789");

            if (includeMatch && lineIndex == firstMatchLine)
            {
                writer.Write(" ERR-1042 timeout during upstream call");
            }
            else if (includeMatch && lineIndex == secondMatchLine)
            {
                writer.Write(" ERR-2048 cache refresh failed");
            }

            writer.Write('\n');
        }

        writer.Flush();
        return stream.Length;
    }

    private static void ValidateSummary(
        SearchSummary summary,
        long expectedFilesMatched,
        long expectedMatchLines,
        long expectedMatchCount)
    {
        if (summary.FilesMatched != expectedFilesMatched)
        {
            throw new InvalidOperationException(
                $"Unexpected file match count. Expected {expectedFilesMatched}, got {summary.FilesMatched}.");
        }

        if (summary.MatchLines != expectedMatchLines)
        {
            throw new InvalidOperationException(
                $"Unexpected matching line count. Expected {expectedMatchLines}, got {summary.MatchLines}.");
        }

        if (summary.MatchCount != expectedMatchCount)
        {
            throw new InvalidOperationException(
                $"Unexpected total hit count. Expected {expectedMatchCount}, got {summary.MatchCount}.");
        }
    }

    private static SearchModeDescriptor DescribeOptions(SearchOptions options) =>
        new(
            PatternKind: options.UseRegex ? "regex" : "literal",
            Mode: options.FilesWithMatches ? "files-with-matches" : options.CountOnly ? "count-only" : "full-output",
            CaseMode: options.CaseMode.ToString(),
            BeforeContext: options.BeforeContext,
            AfterContext: options.AfterContext);

    private static BenchmarkReport BuildReport(BenchmarkSettings settings, IReadOnlyList<ScenarioBenchmarkResult> scenarios)
    {
        var orderedImprovements = scenarios
            .Select(result => result.ImprovementPercent)
            .OrderBy(value => value)
            .ToArray();

        var medianImprovement = Median(orderedImprovements);
        var bestScenario = scenarios.MaxBy(result => result.ImprovementPercent)!;
        var worstScenario = scenarios.MinBy(result => result.ImprovementPercent)!;
        var geometricMeanSpeedup = Math.Exp(scenarios.Average(result => Math.Log(result.SpeedupFactor)));
        var ripgrepComparisons = scenarios
            .Where(result => result.CliComparison?.Ripgrep is not null)
            .Select(result => result.CliComparison!)
            .ToArray();
        RipgrepAggregate? ripgrepAggregate = null;

        if (ripgrepComparisons.Length > 0)
        {
            var orderedCliAdvantages = ripgrepComparisons
                .Select(result => result.NSearcherAdvantagePercent!.Value)
                .OrderBy(value => value)
                .ToArray();

            ripgrepAggregate = new RipgrepAggregate(
                ScenarioCount: ripgrepComparisons.Length,
                NSearcherWins: ripgrepComparisons.Count(result => result.NSearcherAdvantagePercent!.Value > 0.25),
                RipgrepWins: ripgrepComparisons.Count(result => result.NSearcherAdvantagePercent!.Value < -0.25),
                NearTies: ripgrepComparisons.Count(result => Math.Abs(result.NSearcherAdvantagePercent!.Value) <= 0.25),
                MedianAdvantagePercent: Median(orderedCliAdvantages),
                GeometricMeanSpeedup: Math.Exp(ripgrepComparisons.Average(result => Math.Log(result.SpeedupFactor!.Value))));
        }

        var grepComparisons = scenarios
            .Where(result => result.CliComparison?.Grep is not null)
            .Select(result => result.CliComparison!)
            .ToArray();
        GrepAggregate? grepAggregate = null;

        if (grepComparisons.Length > 0)
        {
            var orderedCliAdvantages = grepComparisons
                .Select(result => result.NSearcherVsGrepAdvantagePercent!.Value)
                .OrderBy(value => value)
                .ToArray();

            grepAggregate = new GrepAggregate(
                ScenarioCount: grepComparisons.Length,
                NSearcherWins: grepComparisons.Count(result => result.NSearcherVsGrepAdvantagePercent!.Value > 0.25),
                GrepWins: grepComparisons.Count(result => result.NSearcherVsGrepAdvantagePercent!.Value < -0.25),
                NearTies: grepComparisons.Count(result => Math.Abs(result.NSearcherVsGrepAdvantagePercent!.Value) <= 0.25),
                MedianAdvantagePercent: Median(orderedCliAdvantages),
                GeometricMeanSpeedup: Math.Exp(grepComparisons.Average(result => Math.Log(result.GrepSpeedupFactor!.Value))));
        }

        var ugrepComparisons = scenarios
            .Where(result => result.CliComparison?.Ugrep is not null)
            .Select(result => result.CliComparison!)
            .ToArray();
        UgrepAggregate? ugrepAggregate = null;

        if (ugrepComparisons.Length > 0)
        {
            var orderedCliAdvantages = ugrepComparisons
                .Select(result => result.NSearcherVsUgrepAdvantagePercent!.Value)
                .OrderBy(value => value)
                .ToArray();

            ugrepAggregate = new UgrepAggregate(
                ScenarioCount: ugrepComparisons.Length,
                NSearcherWins: ugrepComparisons.Count(result => result.NSearcherVsUgrepAdvantagePercent!.Value > 0.25),
                UgrepWins: ugrepComparisons.Count(result => result.NSearcherVsUgrepAdvantagePercent!.Value < -0.25),
                NearTies: ugrepComparisons.Count(result => Math.Abs(result.NSearcherVsUgrepAdvantagePercent!.Value) <= 0.25),
                MedianAdvantagePercent: Median(orderedCliAdvantages),
                GeometricMeanSpeedup: Math.Exp(ugrepComparisons.Average(result => Math.Log(result.UgrepSpeedupFactor!.Value))));
        }

        return new BenchmarkReport(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Host: new HostInfo(
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount),
            WarmupRuns: settings.WarmupRuns,
            MeasuredRuns: settings.MeasuredRuns,
            Aggregate: new BenchmarkAggregate(
                ScenarioCount: scenarios.Count,
                BestScenario: bestScenario.Name,
                BestImprovementPercent: bestScenario.ImprovementPercent,
                WorstScenario: worstScenario.Name,
                WorstImprovementPercent: worstScenario.ImprovementPercent,
                MedianImprovementPercent: medianImprovement,
                GeometricMeanSpeedup: geometricMeanSpeedup),
            RipgrepComparison: ripgrepAggregate,
            GrepComparison: grepAggregate,
            UgrepComparison: ugrepAggregate,
            Scenarios: scenarios);
    }

    private static ArtifactInfo WriteArtifacts(BenchmarkReport report, string? requestedJsonOutputPath)
    {
        var artifactsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "benchmarks");
        Directory.CreateDirectory(artifactsDirectory);

        var timestamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss");
        var latestPath = Path.Combine(artifactsDirectory, "latest.json");
        var timestampedPath = Path.Combine(artifactsDirectory, $"benchmark-{timestamp}.json");
        var json = JsonSerializer.Serialize(report, ReportJsonOptions);
        string? ripgrepChartPath = null;
        string? toolComparisonChartPath = null;

        File.WriteAllText(latestPath, json);
        File.WriteAllText(timestampedPath, json);

        var cliComparisons = report.Scenarios
            .Where(result => result.CliComparison?.Ripgrep is not null)
            .Select(result => result.CliComparison!)
            .ToArray();

        if (cliComparisons.Length > 0)
        {
            ripgrepChartPath = Path.Combine(artifactsDirectory, "nsearcher-vs-ripgrep.svg");
            RipgrepComparisonChartWriter.WriteSvg(ripgrepChartPath, cliComparisons);
        }

        var threeToolComparisons = report.Scenarios
            .Where(result => result.CliComparison is not null)
            .Select(result => result.CliComparison!)
            .Where(result => result.Ripgrep is not null || result.Grep is not null || result.Ugrep is not null)
            .ToArray();

        if (threeToolComparisons.Length > 0)
        {
            toolComparisonChartPath = Path.Combine(artifactsDirectory, "nsearcher-vs-ripgrep-vs-grep-vs-ugrep.svg");
            CliToolComparisonChartWriter.WriteSvg(toolComparisonChartPath, threeToolComparisons);
        }

        string? customPath = null;
        if (!string.IsNullOrWhiteSpace(requestedJsonOutputPath))
        {
            customPath = Path.GetFullPath(requestedJsonOutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(customPath)!);
            File.WriteAllText(customPath, json);
        }

        return new ArtifactInfo(latestPath, timestampedPath, customPath, ripgrepChartPath, toolComparisonChartPath);
    }

    private static void WriteScenarioResult(ScenarioBenchmarkResult result)
    {
        Console.WriteLine($"  Baseline runs (ms):  {string.Join(", ", result.Baseline.RunsMs.Select(FormatMs))}");
        Console.WriteLine($"  Optimized runs (ms): {string.Join(", ", result.Optimized.RunsMs.Select(FormatMs))}");
        Console.WriteLine($"  Baseline median:  {result.BaselineMedianMs:F1} ms");
        Console.WriteLine($"  Optimized median: {result.OptimizedMedianMs:F1} ms");
        Console.WriteLine($"  Improvement: {result.ImprovementPercent:F1}%");
        Console.WriteLine($"  Speedup: {result.SpeedupFactor:F2}x");
        Console.WriteLine(
            $"  Optimized runtime profile: {result.Optimized.LastSummary.RuntimeProfile.WorkerCount} workers, " +
            $"{result.Optimized.LastSummary.RuntimeProfile.StreamBufferSize / 1024} KiB buffers, " +
            $"auto-tuned={(result.Optimized.LastSummary.RuntimeProfile.AutoTuned ? "yes" : "no")}");

        if (result.CliComparison?.Ripgrep is not null)
        {
            Console.WriteLine($"  NSearcher CLI runs (ms): {string.Join(", ", result.CliComparison.NSearcher.RunsMs.Select(FormatMs))}");
            Console.WriteLine($"  ripgrep runs (ms):      {string.Join(", ", result.CliComparison.Ripgrep!.RunsMs.Select(FormatMs))}");
            Console.WriteLine($"  NSearcher vs ripgrep: {result.CliComparison.NSearcherAdvantagePercent:F1}%");
            Console.WriteLine($"  CLI speedup vs ripgrep: {result.CliComparison.SpeedupFactor:F2}x");
        }

        if (result.CliComparison?.Grep is not null)
        {
            Console.WriteLine($"  grep runs (ms):         {string.Join(", ", result.CliComparison.Grep.RunsMs.Select(FormatMs))}");
            Console.WriteLine($"  NSearcher vs grep: {result.CliComparison.NSearcherVsGrepAdvantagePercent:F1}%");
            Console.WriteLine($"  CLI speedup vs grep: {result.CliComparison.GrepSpeedupFactor:F2}x");
        }

        if (result.CliComparison?.Ugrep is not null)
        {
            Console.WriteLine($"  ugrep runs (ms):        {string.Join(", ", result.CliComparison.Ugrep.RunsMs.Select(FormatMs))}");
            Console.WriteLine($"  NSearcher vs ugrep: {result.CliComparison.NSearcherVsUgrepAdvantagePercent:F1}%");
            Console.WriteLine($"  CLI speedup vs ugrep: {result.CliComparison.UgrepSpeedupFactor:F2}x");
        }
    }

    private static void WriteSummary(BenchmarkReport report, ArtifactInfo artifactInfo)
    {
        Console.WriteLine();
        Console.WriteLine("Summary");
        Console.WriteLine(
            $"{PadRight("Scenario", 24)} {PadLeft("Baseline", 10)} {PadLeft("Optimized", 10)} {PadLeft("Improve", 9)} {PadLeft("Speedup", 8)}");

        foreach (var scenario in report.Scenarios)
        {
            Console.WriteLine(
                $"{PadRight(scenario.Name, 24)} {PadLeft($"{scenario.BaselineMedianMs:F1} ms", 10)} " +
                $"{PadLeft($"{scenario.OptimizedMedianMs:F1} ms", 10)} {PadLeft($"{scenario.ImprovementPercent:F1}%", 9)} " +
                $"{PadLeft($"{scenario.SpeedupFactor:F2}x", 8)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Best improvement: {report.Aggregate.BestScenario} at {report.Aggregate.BestImprovementPercent:F1}%");
        Console.WriteLine($"Worst improvement: {report.Aggregate.WorstScenario} at {report.Aggregate.WorstImprovementPercent:F1}%");
        Console.WriteLine($"Median improvement: {report.Aggregate.MedianImprovementPercent:F1}%");
        Console.WriteLine($"Geometric mean speedup: {report.Aggregate.GeometricMeanSpeedup:F2}x");

        if (report.RipgrepComparison is not null)
        {
            Console.WriteLine($"NSearcher wins vs ripgrep: {report.RipgrepComparison.NSearcherWins}/{report.RipgrepComparison.ScenarioCount}");
            Console.WriteLine($"ripgrep wins vs NSearcher: {report.RipgrepComparison.RipgrepWins}/{report.RipgrepComparison.ScenarioCount}");
            Console.WriteLine($"Near ties vs ripgrep: {report.RipgrepComparison.NearTies}/{report.RipgrepComparison.ScenarioCount}");
            Console.WriteLine($"Median CLI advantage vs ripgrep: {report.RipgrepComparison.MedianAdvantagePercent:F1}%");
            Console.WriteLine($"Geometric mean CLI speedup vs ripgrep: {report.RipgrepComparison.GeometricMeanSpeedup:F2}x");
        }

        if (report.GrepComparison is not null)
        {
            Console.WriteLine($"NSearcher wins vs grep: {report.GrepComparison.NSearcherWins}/{report.GrepComparison.ScenarioCount}");
            Console.WriteLine($"grep wins vs NSearcher: {report.GrepComparison.GrepWins}/{report.GrepComparison.ScenarioCount}");
            Console.WriteLine($"Near ties vs grep: {report.GrepComparison.NearTies}/{report.GrepComparison.ScenarioCount}");
            Console.WriteLine($"Median CLI advantage vs grep: {report.GrepComparison.MedianAdvantagePercent:F1}%");
            Console.WriteLine($"Geometric mean CLI speedup vs grep: {report.GrepComparison.GeometricMeanSpeedup:F2}x");
        }

        if (report.UgrepComparison is not null)
        {
            Console.WriteLine($"NSearcher wins vs ugrep: {report.UgrepComparison.NSearcherWins}/{report.UgrepComparison.ScenarioCount}");
            Console.WriteLine($"ugrep wins vs NSearcher: {report.UgrepComparison.UgrepWins}/{report.UgrepComparison.ScenarioCount}");
            Console.WriteLine($"Near ties vs ugrep: {report.UgrepComparison.NearTies}/{report.UgrepComparison.ScenarioCount}");
            Console.WriteLine($"Median CLI advantage vs ugrep: {report.UgrepComparison.MedianAdvantagePercent:F1}%");
            Console.WriteLine($"Geometric mean CLI speedup vs ugrep: {report.UgrepComparison.GeometricMeanSpeedup:F2}x");
        }

        Console.WriteLine($"Latest JSON report: {artifactInfo.LatestJsonPath}");
        Console.WriteLine($"Timestamped JSON report: {artifactInfo.TimestampedJsonPath}");

        if (artifactInfo.CustomJsonPath is not null)
        {
            Console.WriteLine($"Custom JSON report: {artifactInfo.CustomJsonPath}");
        }

        if (artifactInfo.RipgrepChartPath is not null)
        {
            Console.WriteLine($"ripgrep comparison chart: {artifactInfo.RipgrepChartPath}");
        }

        if (artifactInfo.ToolComparisonChartPath is not null)
        {
            Console.WriteLine($"multi-tool comparison chart: {artifactInfo.ToolComparisonChartPath}");
        }
    }

    private static BenchmarkScenario[]? ResolveScenarios(IReadOnlyList<string> scenarioNames)
    {
        if (scenarioNames.Count == 0)
        {
            return ScenarioCatalog;
        }

        var catalog = ScenarioCatalog.ToDictionary(scenario => scenario.Name, StringComparer.OrdinalIgnoreCase);
        var selected = new List<BenchmarkScenario>(scenarioNames.Count);

        foreach (var scenarioName in scenarioNames)
        {
            if (!catalog.TryGetValue(scenarioName, out var scenario))
            {
                Console.Error.WriteLine($"Unknown scenario '{scenarioName}'.");
                Console.Error.WriteLine();
                WriteScenarioList();
                return null;
            }

            selected.Add(scenario);
        }

        return selected.ToArray();
    }

    private static bool TryParseArguments(
        string[] args,
        out BenchmarkSettings settings,
        out string? errorMessage)
    {
        var scenarioNames = new List<string>();
        var warmupRuns = DefaultWarmupRuns;
        var measuredRuns = DefaultMeasuredRuns;
        string? jsonOutputPath = null;
        string? ripgrepPath = null;
        string? grepPath = null;
        string? ugrepPath = null;
        string? nSearcherCliPath = null;
        var showHelp = false;
        var listScenarios = false;
        var compareWithRipgrep = false;
        var compareWithGrep = false;
        var compareWithUgrep = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "--list":
                    listScenarios = true;
                    break;

                case "--scenario":
                    if (!TryReadValue(args, ref index, out var scenarioName))
                    {
                        settings = default;
                        errorMessage = "Missing value for --scenario.";
                        return false;
                    }

                    scenarioNames.Add(scenarioName);
                    break;

                case "--json":
                    if (!TryReadValue(args, ref index, out jsonOutputPath))
                    {
                        settings = default;
                        errorMessage = "Missing value for --json.";
                        return false;
                    }

                    break;

                case "--compare-ripgrep":
                    compareWithRipgrep = true;
                    break;

                case "--compare-grep":
                    compareWithGrep = true;
                    break;

                case "--compare-ugrep":
                    compareWithUgrep = true;
                    break;

                case "--ripgrep-path":
                    if (!TryReadValue(args, ref index, out ripgrepPath))
                    {
                        settings = default;
                        errorMessage = "Missing value for --ripgrep-path.";
                        return false;
                    }

                    compareWithRipgrep = true;
                    break;

                case "--nsearcher-cli-path":
                    if (!TryReadValue(args, ref index, out nSearcherCliPath))
                    {
                        settings = default;
                        errorMessage = "Missing value for --nsearcher-cli-path.";
                        return false;
                    }

                    compareWithRipgrep = true;
                    break;

                case "--grep-path":
                    if (!TryReadValue(args, ref index, out grepPath))
                    {
                        settings = default;
                        errorMessage = "Missing value for --grep-path.";
                        return false;
                    }

                    compareWithGrep = true;
                    break;

                case "--ugrep-path":
                    if (!TryReadValue(args, ref index, out ugrepPath))
                    {
                        settings = default;
                        errorMessage = "Missing value for --ugrep-path.";
                        return false;
                    }

                    compareWithUgrep = true;
                    break;

                case "--warmup":
                    if (!TryReadPositiveInteger(args, ref index, out warmupRuns))
                    {
                        settings = default;
                        errorMessage = "Missing or invalid value for --warmup.";
                        return false;
                    }

                    break;

                case "--runs":
                    if (!TryReadPositiveInteger(args, ref index, out measuredRuns))
                    {
                        settings = default;
                        errorMessage = "Missing or invalid value for --runs.";
                        return false;
                    }

                    break;

                default:
                    settings = default;
                    errorMessage = $"Unknown option '{args[index]}'.";
                    return false;
            }
        }

        settings = new BenchmarkSettings(
            showHelp,
            listScenarios,
            scenarioNames,
            warmupRuns,
            measuredRuns,
            jsonOutputPath,
            compareWithRipgrep,
            compareWithGrep,
            compareWithUgrep,
            ripgrepPath,
            grepPath,
            ugrepPath,
            nSearcherCliPath);
        errorMessage = null;
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadPositiveInteger(string[] args, ref int index, out int value)
    {
        value = 0;
        return index + 1 < args.Length &&
               int.TryParse(args[++index], out value) &&
               value > 0;
    }

    private static void WriteHelp()
    {
        Console.WriteLine("NSearcher benchmark suite");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project .\\NSearcher.Benchmarks -c Release [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --list               List available scenarios");
        Console.WriteLine("  --scenario <name>    Run only one named scenario (repeatable)");
        Console.WriteLine("  --runs <n>           Number of measured runs per engine (default: 6)");
        Console.WriteLine("  --warmup <n>         Number of warmup runs per engine (default: 1)");
        Console.WriteLine("  --json <path>        Also write the JSON report to a custom path");
        Console.WriteLine("  --compare-ripgrep    Compare the NSearcher CLI against ripgrep");
        Console.WriteLine("  --compare-grep       Compare the NSearcher CLI against GNU grep");
        Console.WriteLine("  --compare-ugrep      Compare the NSearcher CLI against ugrep");
        Console.WriteLine("  --ripgrep-path <p>   Override the ripgrep executable path");
        Console.WriteLine("  --grep-path <p>      Override the grep executable path");
        Console.WriteLine("  --ugrep-path <p>     Override the ugrep executable path");
        Console.WriteLine("  --nsearcher-cli-path Override the NSearcher CLI executable path");
        Console.WriteLine("  -h, --help           Show this help");
    }

    private static void WriteScenarioList()
    {
        Console.WriteLine("Available benchmark scenarios:");

        foreach (var scenario in ScenarioCatalog)
        {
            Console.WriteLine($"  {scenario.Name} - {scenario.Description}");
        }
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;

        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static string FormatMs(double value) => value.ToString("F1");

    private static string PadLeft(string value, int width) => value.PadLeft(width, ' ');

    private static string PadRight(string value, int width) => value.PadRight(width, ' ');

    private sealed record BenchmarkScenario(
        string Name,
        string Description,
        Func<string, CorpusInfo> CreateCorpus,
        Func<string, SearchOptions> CreateOptions,
        BenchmarkExpectation Expectation);

    private readonly record struct BenchmarkSettings(
        bool ShowHelp,
        bool ListScenarios,
        IReadOnlyList<string> ScenarioNames,
        int WarmupRuns,
        int MeasuredRuns,
        string? JsonOutputPath,
        bool CompareWithRipgrep,
        bool CompareWithGrep,
        bool CompareWithUgrep,
        string? RipgrepPath,
        string? GrepPath,
        string? UgrepPath,
        string? NSearcherCliPath);

    private readonly record struct CorpusInfo(int FileCount, int MatchingFiles, long TotalBytes);

    private readonly record struct BenchmarkExpectation(
        long ExpectedFilesMatched,
        long ExpectedMatchLines,
        long ExpectedMatchCount)
    {
        public void Validate(SearchSummary summary) =>
            ValidateSummary(summary, ExpectedFilesMatched, ExpectedMatchLines, ExpectedMatchCount);
    }

    private sealed record EngineMeasurement(double[] RunsMs, SearchSummary LastSummary);

    private sealed record SearchModeDescriptor(
        string PatternKind,
        string Mode,
        string CaseMode,
        int BeforeContext,
        int AfterContext);

    private sealed record ScenarioBenchmarkResult(
        string Name,
        string Description,
        SearchModeDescriptor SearchMode,
        CorpusInfo Corpus,
        EngineMeasurement Baseline,
        EngineMeasurement Optimized,
        double BaselineMedianMs,
        double OptimizedMedianMs,
        double ImprovementPercent,
        double SpeedupFactor,
        CliComparisonResult? CliComparison);

    private sealed record HostInfo(
        string OsDescription,
        string ProcessArchitecture,
        string FrameworkDescription,
        int LogicalProcessors);

    private sealed record BenchmarkAggregate(
        int ScenarioCount,
        string BestScenario,
        double BestImprovementPercent,
        string WorstScenario,
        double WorstImprovementPercent,
        double MedianImprovementPercent,
        double GeometricMeanSpeedup);

    private sealed record RipgrepAggregate(
        int ScenarioCount,
        int NSearcherWins,
        int RipgrepWins,
        int NearTies,
        double MedianAdvantagePercent,
        double GeometricMeanSpeedup);

    private sealed record GrepAggregate(
        int ScenarioCount,
        int NSearcherWins,
        int GrepWins,
        int NearTies,
        double MedianAdvantagePercent,
        double GeometricMeanSpeedup);

    private sealed record UgrepAggregate(
        int ScenarioCount,
        int NSearcherWins,
        int UgrepWins,
        int NearTies,
        double MedianAdvantagePercent,
        double GeometricMeanSpeedup);

    private sealed record BenchmarkReport(
        DateTimeOffset GeneratedAtUtc,
        HostInfo Host,
        int WarmupRuns,
        int MeasuredRuns,
        BenchmarkAggregate Aggregate,
        RipgrepAggregate? RipgrepComparison,
        GrepAggregate? GrepComparison,
        UgrepAggregate? UgrepComparison,
        IReadOnlyList<ScenarioBenchmarkResult> Scenarios);

    private sealed record ArtifactInfo(
        string LatestJsonPath,
        string TimestampedJsonPath,
        string? CustomJsonPath,
        string? RipgrepChartPath,
        string? ToolComparisonChartPath);
}
