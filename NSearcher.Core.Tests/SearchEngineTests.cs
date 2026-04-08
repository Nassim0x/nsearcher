using System.Text;
using NSearcher.Core;

namespace NSearcher.Core.Tests;

public sealed class SearchEngineTests : IDisposable
{
    private readonly string _rootDirectory;

    public SearchEngineTests()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "nsearcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public async Task LiteralSearch_FindsMatchesAcrossMultipleFiles()
    {
        WriteFile("docs/a.txt", "hello world\nneedle here\n");
        WriteFile("docs/b.txt", "Needle\nanother needle\n");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "docs")],
                CaseMode = CaseMode.Insensitive,
                MaxDegreeOfParallelism = 2
            },
            (result, _) =>
            {
                lock (results)
                {
                    results.Add(result);
                }

                return ValueTask.CompletedTask;
            });

        Assert.Equal(2, summary.FilesMatched);
        Assert.Equal(3, summary.MatchLines);
        Assert.Equal(3, summary.MatchCount);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SmartCase_IgnoresCaseOnlyForLowercasePatterns()
    {
        WriteFile("smart.txt", "Alpha\nalpha\nALPHA\n");
        var engine = new SearchEngine();

        var lowercaseResults = new List<SearchFileResult>();
        var lowercaseSummary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "alpha",
                Paths = [Path.Combine(_rootDirectory, "smart.txt")],
                CaseMode = CaseMode.Smart
            },
            (result, _) =>
            {
                lowercaseResults.Add(result);
                return ValueTask.CompletedTask;
            });

        var uppercaseResults = new List<SearchFileResult>();
        var uppercaseSummary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "Alpha",
                Paths = [Path.Combine(_rootDirectory, "smart.txt")],
                CaseMode = CaseMode.Smart
            },
            (result, _) =>
            {
                uppercaseResults.Add(result);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(3, lowercaseSummary.MatchLines);
        Assert.Single(lowercaseResults);
        Assert.Equal(1, uppercaseSummary.MatchLines);
        Assert.Single(uppercaseResults);
    }

    [Fact]
    public async Task ContextLines_AreMergedWithoutDuplicates()
    {
        WriteFile("context.txt", "one\ntwo needle\nthree needle\nfour\nfive\n");
        var engine = new SearchEngine();
        var results = new List<SearchFileResult>();

        await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "context.txt")],
                BeforeContext = 1,
                AfterContext = 1
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var outputLines = Assert.Single(results).OutputLines;
        Assert.Equal([1, 2, 3, 4], outputLines.Select(line => line.LineNumber).ToArray());
        Assert.Equal(
            [SearchOutputLineKind.Context, SearchOutputLineKind.Match, SearchOutputLineKind.Match, SearchOutputLineKind.Context],
            outputLines.Select(line => line.Kind).ToArray());
    }

    [Fact]
    public async Task IncludeExcludeAndBinaryDetection_FilterCandidates()
    {
        WriteFile("src/keep.cs", "needle");
        WriteFile("src/skip.log", "needle");
        WriteBinaryFile("src/data.bin", [0, 1, 2, 3, 0, 5]);

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "src")],
                IncludeGlobs = ["*.cs", "*.log", "*.bin"],
                ExcludeGlobs = ["*.log"]
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var onlyResult = Assert.Single(results);
        Assert.EndsWith("src/keep.cs", onlyResult.FilePath, StringComparison.Ordinal);
        Assert.Equal(1, summary.FilesMatched);
        Assert.Equal(1, summary.FilesSkippedExcluded);
        Assert.Equal(1, summary.FilesSkippedBinary);
    }

    [Fact]
    public async Task RootDirectorySearch_TraversesNestedSubdirectories()
    {
        WriteFile("workspace/root.txt", "nothing here");
        WriteFile("workspace/level1/level2/deep.txt", "needle in deep folder");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "workspace")]
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.EndsWith("workspace/level1/level2/deep.txt", match.FilePath, StringComparison.Ordinal);
        Assert.Equal(1, summary.FilesMatched);
        Assert.Equal(2, summary.FilesEnumerated);
    }

    [Fact]
    public async Task Utf16TextFile_IsNotSkippedAsBinary()
    {
        var filePath = Path.Combine(_rootDirectory, "utf16.txt");
        File.WriteAllText(filePath, "alpha\nneedle\nomega\n", Encoding.Unicode);

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [filePath],
                CaseMode = CaseMode.Sensitive
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        Assert.Single(results);
        Assert.Equal(1, summary.FilesMatched);
        Assert.Equal(0, summary.FilesSkippedBinary);
    }

    [Fact]
    public async Task FilesWithMatches_FindsLargeSingleLineFile()
    {
        WriteFile("large-single-line.txt", new string('a', 200_000) + "needle" + new string('b', 200_000));

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "large-single-line.txt")],
                CaseMode = CaseMode.Sensitive,
                FilesWithMatches = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.EndsWith("large-single-line.txt", match.FilePath, StringComparison.Ordinal);
        Assert.Equal(1, summary.FilesMatched);
        Assert.Equal(1, summary.MatchLines);
    }

    [Fact]
    public async Task CountOnly_LiteralFastPathCountsMatchingLinesAndHits()
    {
        WriteFile("counts.txt", "needle needle\nnone here\nneedle\n");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "counts.txt")],
                CaseMode = CaseMode.Sensitive,
                CountOnly = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.Equal(2, match.MatchLineCount);
        Assert.Equal(3, match.MatchCount);
        Assert.Equal(2, summary.MatchLines);
        Assert.Equal(3, summary.MatchCount);
    }

    [Fact]
    public async Task FilesWithMatches_SummaryOnlyFastPathFindsSmallFiles()
    {
        WriteFile("small/a.txt", "alpha\nneedle\nomega\n");
        WriteFile("small/b.txt", "alpha\nomega\n");

        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "needle",
                Paths = [Path.Combine(_rootDirectory, "small")],
                CaseMode = CaseMode.Sensitive,
                FilesWithMatches = true
            });

        Assert.Equal(1, summary.FilesMatched);
        Assert.Equal(1, summary.MatchLines);
        Assert.Equal(1, summary.MatchCount);
    }

    [Fact]
    public async Task RegexCountOnly_SimpleStructuredPatternCountsMatchesWithoutFalsePositives()
    {
        WriteFile(
            "logs/app.log",
            "2026-04-02T12:34:00Z INFO svc=01 ERR-1042 timeout\n" +
            "2026-04-02T12:34:01Z INFO svc=01 ERR-20A8 invalid\n" +
            "2026-04-02T12:34:02Z INFO svc=01 ERR-2048 cache failed ERR-3001 retry exhausted\n");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = @"ERR-\d{4}",
                Paths = [Path.Combine(_rootDirectory, "logs")],
                UseRegex = true,
                CaseMode = CaseMode.Sensitive,
                CountOnly = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.Equal(2, match.MatchLineCount);
        Assert.Equal(3, match.MatchCount);
        Assert.Equal(2, summary.MatchLines);
        Assert.Equal(3, summary.MatchCount);
    }

    [Fact]
    public async Task RegexCountOnly_LongSingleLineAcrossBufferedChunksCountsMatches()
    {
        WriteFile(
            "logs/long-line.log",
            new string('a', 300_000) + " ERR-1042 middle ERR-3001 " + new string('b', 300_000));

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = @"ERR-\d{4}",
                Paths = [Path.Combine(_rootDirectory, "logs")],
                UseRegex = true,
                CaseMode = CaseMode.Sensitive,
                CountOnly = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.Equal(1, match.MatchLineCount);
        Assert.Equal(2, match.MatchCount);
        Assert.Equal(1, summary.MatchLines);
        Assert.Equal(2, summary.MatchCount);
    }

    [Fact]
    public async Task RegexPrefilter_DoesNotSkipAlternationMatches()
    {
        WriteFile("regex/alternation.txt", "bar\n");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "foo|bar",
                Paths = [Path.Combine(_rootDirectory, "regex")],
                UseRegex = true,
                CaseMode = CaseMode.Sensitive,
                CountOnly = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.Equal(1, match.MatchLineCount);
        Assert.Equal(1, match.MatchCount);
        Assert.Equal(1, summary.MatchLines);
        Assert.Equal(1, summary.MatchCount);
    }

    [Fact]
    public async Task RegexPrefilter_DoesNotSkipOptionalMatches()
    {
        WriteFile("regex/optional.txt", "color\n");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = "colou?r",
                Paths = [Path.Combine(_rootDirectory, "regex")],
                UseRegex = true,
                CaseMode = CaseMode.Sensitive,
                CountOnly = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.Equal(1, match.MatchLineCount);
        Assert.Equal(1, match.MatchCount);
        Assert.Equal(1, summary.MatchLines);
        Assert.Equal(1, summary.MatchCount);
    }

    [Fact]
    public async Task SimpleRegexMatcher_UsesUnicodeDigitSemantics()
    {
        WriteFile("regex/unicode-digits.txt", "ERR-١٢٣٤\n");

        var results = new List<SearchFileResult>();
        var engine = new SearchEngine();

        var summary = await engine.ExecuteAsync(
            new SearchOptions
            {
                Pattern = @"ERR-\d{4}",
                Paths = [Path.Combine(_rootDirectory, "regex")],
                UseRegex = true,
                CaseMode = CaseMode.Sensitive,
                CountOnly = true
            },
            (result, _) =>
            {
                results.Add(result);
                return ValueTask.CompletedTask;
            });

        var match = Assert.Single(results);
        Assert.Equal(1, match.MatchLineCount);
        Assert.Equal(1, match.MatchCount);
        Assert.Equal(1, summary.MatchLines);
        Assert.Equal(1, summary.MatchCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void WriteBinaryFile(string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(_rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }
}
