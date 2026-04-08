using System.Diagnostics;
using System.Text.RegularExpressions;
using NSearcher.Core;

namespace NSearcher.Benchmarks;

internal static class CliComparisonRunner
{
    private static readonly Regex CountLineRegex = new(@":(?<count>\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GrepDigitClassRepetitionRegex = new(@"\\d\{(?<count>\d+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GrepWordClassRepetitionRegex = new(@"\\w\{(?<count>\d+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] RipgrepCandidatePaths =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "OpenAI.Codex_2p2nqsd0c76g0",
            "LocalCache",
            "Local",
            "OpenAI",
            "Codex",
            "bin",
            "rg.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "cursor",
            "resources",
            "app",
            "node_modules",
            "@vscode",
            "ripgrep",
            "bin",
            "rg.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Microsoft VS Code",
            "cfbea10c5f",
            "resources",
            "app",
            "node_modules",
            "@vscode",
            "ripgrep",
            "bin",
            "rg.exe")
    ];

    private static readonly string[] GrepCandidatePaths =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git",
            "usr",
            "bin",
            "grep.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git",
            "bin",
            "grep.exe"),
        @"C:\msys64\usr\bin\grep.exe"
    ];

    private static readonly string[] UgrepCandidatePaths =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ugrep",
            "bin",
            "ugrep.exe")
    ];

    private static readonly string[] NSearcherCandidatePaths =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "NSearcher",
            "NSearcher.exe"),
        Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "publish-aot", "win-x64", "NSearcher.exe"),
        Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "publish", "win-x64", "NSearcher.exe"),
        Path.Combine(Directory.GetCurrentDirectory(), "NSearcher.Cli", "bin", "Release", "net10.0", "NSearcher.exe")
    ];

    public static CliToolPaths? ResolveToolPaths(
        string? nSearcherOverridePath,
        string? ripgrepOverridePath,
        string? grepOverridePath,
        string? ugrepOverridePath,
        bool requireRipgrep,
        bool requireGrep,
        bool requireUgrep)
    {
        var nSearcherPath = ResolvePath(nSearcherOverridePath, NSearcherCandidatePaths);
        if (nSearcherPath is null)
        {
            return null;
        }

        string? ripgrepPath = null;
        if (requireRipgrep)
        {
            ripgrepPath = ResolvePath(ripgrepOverridePath, RipgrepCandidatePaths);
            if (ripgrepPath is null)
            {
                return null;
            }
        }

        string? grepPath = null;
        if (requireGrep)
        {
            grepPath = ResolvePath(grepOverridePath, GrepCandidatePaths);
            if (grepPath is null)
            {
                return null;
            }
        }

        string? ugrepPath = null;
        if (requireUgrep)
        {
            ugrepPath = ResolvePath(ugrepOverridePath, UgrepCandidatePaths);
            if (ugrepPath is null)
            {
                return null;
            }
        }

        return new CliToolPaths(nSearcherPath, ripgrepPath, grepPath, ugrepPath);
    }

    public static async Task<CliComparisonResult> CompareAsync(
        string scenarioName,
        string scenarioRoot,
        SearchOptions options,
        CliComparisonExpectation expectation,
        CliToolPaths toolPaths,
        int warmupRuns,
        int measuredRuns,
        CancellationToken cancellationToken = default)
    {
        var nSearcherSpec = BuildNSearcherSpec(toolPaths.NSearcherPath, scenarioRoot, options);
        await WarmUpAsync(nSearcherSpec, options, expectation, warmupRuns, cancellationToken);
        var nSearcherMeasurement = await MeasureAsync(nSearcherSpec, options, expectation, measuredRuns, cancellationToken);
        var nSearcherMedian = Median(nSearcherMeasurement.RunsMs);
        CliToolMeasurement? ripgrepMeasurement = null;
        double? ripgrepMedian = null;
        double? advantagePercent = null;
        double? speedupFactor = null;

        if (toolPaths.RipgrepPath is not null)
        {
            var ripgrepSpec = BuildRipgrepSpec(toolPaths.RipgrepPath, scenarioRoot, options);
            await WarmUpAsync(ripgrepSpec, options, expectation, warmupRuns, cancellationToken);
            ripgrepMeasurement = await MeasureAsync(ripgrepSpec, options, expectation, measuredRuns, cancellationToken);
            ripgrepMedian = Median(ripgrepMeasurement.RunsMs);
            advantagePercent = ((ripgrepMedian.Value - nSearcherMedian) / ripgrepMedian.Value) * 100.0;
            speedupFactor = ripgrepMedian.Value / nSearcherMedian;
        }

        CliToolMeasurement? grepMeasurement = null;
        double? grepMedian = null;
        double? grepAdvantagePercent = null;
        double? grepSpeedupFactor = null;

        if (toolPaths.GrepPath is not null)
        {
            var grepSpec = BuildGrepSpec(toolPaths.GrepPath, scenarioRoot, options);
            await WarmUpAsync(grepSpec, options, expectation, warmupRuns, cancellationToken);
            grepMeasurement = await MeasureAsync(grepSpec, options, expectation, measuredRuns, cancellationToken);
            grepMedian = Median(grepMeasurement.RunsMs);
            grepAdvantagePercent = ((grepMedian.Value - nSearcherMedian) / grepMedian.Value) * 100.0;
            grepSpeedupFactor = grepMedian.Value / nSearcherMedian;
        }

        CliToolMeasurement? ugrepMeasurement = null;
        double? ugrepMedian = null;
        double? ugrepAdvantagePercent = null;
        double? ugrepSpeedupFactor = null;

        if (toolPaths.UgrepPath is not null)
        {
            var ugrepSpec = BuildUgrepSpec(toolPaths.UgrepPath, scenarioRoot, options);
            await WarmUpAsync(ugrepSpec, options, expectation, warmupRuns, cancellationToken);
            ugrepMeasurement = await MeasureAsync(ugrepSpec, options, expectation, measuredRuns, cancellationToken);
            ugrepMedian = Median(ugrepMeasurement.RunsMs);
            ugrepAdvantagePercent = ((ugrepMedian.Value - nSearcherMedian) / ugrepMedian.Value) * 100.0;
            ugrepSpeedupFactor = ugrepMedian.Value / nSearcherMedian;
        }

        return new CliComparisonResult(
            scenarioName,
            toolPaths.NSearcherPath,
            toolPaths.RipgrepPath,
            toolPaths.GrepPath,
            toolPaths.UgrepPath,
            nSearcherMeasurement with { MedianMs = nSearcherMedian },
            ripgrepMeasurement is null ? null : ripgrepMeasurement with { MedianMs = ripgrepMedian!.Value },
            grepMeasurement is null ? null : grepMeasurement with { MedianMs = grepMedian!.Value },
            ugrepMeasurement is null ? null : ugrepMeasurement with { MedianMs = ugrepMedian!.Value },
            advantagePercent,
            speedupFactor,
            grepAdvantagePercent,
            grepSpeedupFactor,
            ugrepAdvantagePercent,
            ugrepSpeedupFactor);
    }

    private static async Task WarmUpAsync(
        ProcessSpec spec,
        SearchOptions options,
        CliComparisonExpectation expectation,
        int warmupRuns,
        CancellationToken cancellationToken)
    {
        for (var iteration = 0; iteration < warmupRuns; iteration++)
        {
            await ExecuteOnceAsync(spec, options, expectation, cancellationToken);
        }
    }

    private static async Task<CliToolMeasurement> MeasureAsync(
        ProcessSpec spec,
        SearchOptions options,
        CliComparisonExpectation expectation,
        int measuredRuns,
        CancellationToken cancellationToken)
    {
        var results = new double[measuredRuns];

        for (var iteration = 0; iteration < measuredRuns; iteration++)
        {
            ForceGc();
            var stopwatch = Stopwatch.StartNew();
            await ExecuteOnceAsync(spec, options, expectation, cancellationToken);
            stopwatch.Stop();
            results[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }

        return new CliToolMeasurement(spec.ToolName, results, 0);
    }

    private static async Task ExecuteOnceAsync(
        ProcessSpec spec,
        SearchOptions options,
        CliComparisonExpectation expectation,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec.ExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            }
        };

        foreach (var argument in spec.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        ValidateProcessOutput(spec.ToolName, process.ExitCode, stdout, stderr, options, expectation);
    }

    private static ProcessSpec BuildNSearcherSpec(string executablePath, string scenarioRoot, SearchOptions options)
    {
        var arguments = BuildSharedArguments(options, scenarioRoot, includeNoColor: true);
        return new ProcessSpec(executablePath, "NSearcher", arguments);
    }

    private static ProcessSpec BuildRipgrepSpec(string executablePath, string scenarioRoot, SearchOptions options)
    {
        var arguments = new List<string>
        {
            "--no-config",
            "--color",
            "never"
        };

        if (!options.UseDefaultExcludes)
        {
            arguments.Add("--no-ignore");
        }

        if (options.IncludeHidden)
        {
            arguments.Add("--hidden");
        }

        if (options.IncludeBinary)
        {
            arguments.Add("--text");
        }

        arguments.Add(options.CaseMode switch
        {
            CaseMode.Insensitive => "-i",
            CaseMode.Sensitive => "-s",
            _ => "-S"
        });

        if (!options.UseRegex)
        {
            arguments.Add("-F");
        }

        if (options.FilesWithMatches)
        {
            arguments.Add("-l");
        }
        else if (options.CountOnly)
        {
            arguments.Add("-c");
        }

        foreach (var glob in options.IncludeGlobs)
        {
            arguments.Add("-g");
            arguments.Add(glob);
        }

        foreach (var glob in options.ExcludeGlobs)
        {
            arguments.Add("-g");
            arguments.Add($"!{glob}");
        }

        arguments.Add(options.Pattern);
        arguments.Add(scenarioRoot);

        return new ProcessSpec(executablePath, "ripgrep", arguments.ToArray());
    }

    private static ProcessSpec BuildGrepSpec(string executablePath, string scenarioRoot, SearchOptions options)
    {
        var arguments = new List<string>
        {
            "--color=never",
            "-r",
            "-H"
        };

        if (!options.IncludeBinary)
        {
            arguments.Add("-I");
        }

        if (options.CaseMode == CaseMode.Insensitive)
        {
            arguments.Add("-i");
        }

        if (options.FilesWithMatches)
        {
            arguments.Add("-l");
        }
        else if (options.CountOnly)
        {
            arguments.Add("-c");
        }

        arguments.Add(options.UseRegex ? "-E" : "-F");
        arguments.Add(TranslatePatternForGrep(options.Pattern, options.UseRegex));
        arguments.Add(scenarioRoot);

        return new ProcessSpec(executablePath, "grep", arguments.ToArray());
    }

    private static ProcessSpec BuildUgrepSpec(string executablePath, string scenarioRoot, SearchOptions options)
    {
        var arguments = new List<string>
        {
            "--color=never",
            "-R",
            "-H"
        };

        if (!options.IncludeBinary)
        {
            arguments.Add("-I");
        }

        arguments.Add(options.CaseMode switch
        {
            CaseMode.Insensitive => "-i",
            CaseMode.Smart => "--smart-case",
            _ => "-s"
        });

        if (options.FilesWithMatches)
        {
            arguments.Add("-l");
        }
        else if (options.CountOnly)
        {
            arguments.Add("-c");
        }

        arguments.Add(options.UseRegex ? "-E" : "-F");
        arguments.Add(TranslatePatternForGrep(options.Pattern, options.UseRegex));
        arguments.Add(scenarioRoot);

        return new ProcessSpec(executablePath, "ugrep", arguments.ToArray());
    }

    private static string[] BuildSharedArguments(SearchOptions options, string scenarioRoot, bool includeNoColor)
    {
        var arguments = new List<string>
        {
            options.Pattern,
            scenarioRoot
        };

        if (options.UseRegex)
        {
            arguments.Add("--regex");
        }

        arguments.Add(options.CaseMode switch
        {
            CaseMode.Insensitive => "-i",
            CaseMode.Sensitive => "-s",
            _ => "-S"
        });

        if (options.FilesWithMatches)
        {
            arguments.Add("-l");
        }
        else if (options.CountOnly)
        {
            arguments.Add("-c");
        }

        if (options.IncludeHidden)
        {
            arguments.Add("--hidden");
        }

        if (options.IncludeBinary)
        {
            arguments.Add("--binary");
        }

        if (!options.UseDefaultExcludes)
        {
            arguments.Add("--no-default-excludes");
        }

        foreach (var glob in options.IncludeGlobs)
        {
            arguments.Add("-g");
            arguments.Add(glob);
        }

        foreach (var glob in options.ExcludeGlobs)
        {
            arguments.Add("--exclude");
            arguments.Add(glob);
        }

        if (includeNoColor)
        {
            arguments.Add("--no-color");
        }

        return arguments.ToArray();
    }

    private static void ValidateProcessOutput(
        string toolName,
        int exitCode,
        string stdout,
        string stderr,
        SearchOptions options,
        CliComparisonExpectation expectation)
    {
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{toolName} exited with code {exitCode}. stderr: {stderr}");
        }

        var lines = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (options.FilesWithMatches)
        {
            if (lines.Length != expectation.ExpectedFilesMatched)
            {
                throw new InvalidOperationException(
                    $"{toolName} returned {lines.Length} matching files, expected {expectation.ExpectedFilesMatched}.");
            }

            return;
        }

        if (options.CountOnly)
        {
            if (string.Equals(toolName, "grep", StringComparison.Ordinal) ||
                string.Equals(toolName, "ugrep", StringComparison.Ordinal))
            {
                lines = lines
                    .Where(static line => !line.EndsWith(":0", StringComparison.Ordinal))
                    .ToArray();
            }

            if (lines.Length != expectation.ExpectedFilesMatched)
            {
                throw new InvalidOperationException(
                    $"{toolName} returned {lines.Length} count lines, expected {expectation.ExpectedFilesMatched}.");
            }

            var totalMatchingLines = 0;
            foreach (var line in lines)
            {
                var match = CountLineRegex.Match(line);
                if (!match.Success)
                {
                    throw new InvalidOperationException($"{toolName} produced an unexpected count line: '{line}'.");
                }

                totalMatchingLines += int.Parse(match.Groups["count"].Value);
            }

            if (totalMatchingLines != expectation.ExpectedMatchingLines)
            {
                throw new InvalidOperationException(
                    $"{toolName} reported {totalMatchingLines} matching lines, expected {expectation.ExpectedMatchingLines}.");
            }
        }
    }

    private static string TranslatePatternForGrep(string pattern, bool useRegex)
    {
        if (!useRegex)
        {
            return pattern;
        }

        var translated = GrepDigitClassRepetitionRegex.Replace(
            pattern,
            static match => string.Concat(Enumerable.Repeat("[0-9]", int.Parse(match.Groups["count"].Value))));
        translated = GrepWordClassRepetitionRegex.Replace(
            translated,
            static match => string.Concat(Enumerable.Repeat("[[:alnum:]_]", int.Parse(match.Groups["count"].Value))));
        translated = translated.Replace(@"\d", "[0-9]", StringComparison.Ordinal);
        translated = translated.Replace(@"\w", "[[:alnum:]_]", StringComparison.Ordinal);
        translated = translated.Replace(@"\s", "[[:space:]]", StringComparison.Ordinal);
        return translated;
    }

    private static string? ResolvePath(string? overridePath, IEnumerable<string> candidates)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolvedOverride = Path.GetFullPath(overridePath);
            return File.Exists(resolvedOverride) ? resolvedOverride : null;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
}

internal readonly record struct CliToolPaths(string NSearcherPath, string? RipgrepPath, string? GrepPath, string? UgrepPath);

internal readonly record struct CliComparisonExpectation(int ExpectedFilesMatched, int ExpectedMatchingLines);

internal sealed record CliComparisonResult(
    string ScenarioName,
    string NSearcherPath,
    string? RipgrepPath,
    string? GrepPath,
    string? UgrepPath,
    CliToolMeasurement NSearcher,
    CliToolMeasurement? Ripgrep,
    CliToolMeasurement? Grep,
    CliToolMeasurement? Ugrep,
    double? NSearcherAdvantagePercent,
    double? SpeedupFactor,
    double? NSearcherVsGrepAdvantagePercent,
    double? GrepSpeedupFactor,
    double? NSearcherVsUgrepAdvantagePercent,
    double? UgrepSpeedupFactor);

internal sealed record CliToolMeasurement(string ToolName, double[] RunsMs, double MedianMs);

internal sealed record ProcessSpec(string ExecutablePath, string ToolName, string[] Arguments);
