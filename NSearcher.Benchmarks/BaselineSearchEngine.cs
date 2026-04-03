using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using NSearcher.Core;

namespace NSearcher.Benchmarks;

internal sealed class BaselineSearchEngine
{
    private const int StreamBufferSize = 128 * 1024;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly EnumerationOptions EntryEnumerationOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private static readonly FrozenSet<string> DefaultExcludedDirectories =
        new[]
        {
            ".git",
            ".hg",
            ".svn",
            ".vs",
            "bin",
            "obj",
            "node_modules"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public async Task<SearchSummary> ExecuteAsync(
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();

        var pattern = BaselineSearchPattern.Create(options);
        var metrics = new SearchMetrics();
        var stopwatch = Stopwatch.StartNew();
        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var normalizedIncludeGlobs = NormalizePatterns(options.IncludeGlobs);
        var normalizedExcludeGlobs = NormalizePatterns(options.ExcludeGlobs);
        var workerCount = options.MaxDegreeOfParallelism == 0
            ? Math.Max(1, Environment.ProcessorCount)
            : options.MaxDegreeOfParallelism;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                EnumerateCandidates(options, metrics, currentDirectory, normalizedIncludeGlobs, normalizedExcludeGlobs, cts.Token),
                new ParallelOptions
                {
                    CancellationToken = cts.Token,
                    MaxDegreeOfParallelism = workerCount
                },
                (candidate, _) =>
                {
                    SearchFile(candidate, pattern, options, metrics, cts);
                    return ValueTask.CompletedTask;
                });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }

        stopwatch.Stop();
        return metrics.ToSummary(stopwatch.Elapsed);
    }

    private static void SearchFile(
        SearchCandidate candidate,
        BaselineSearchPattern pattern,
        SearchOptions options,
        SearchMetrics metrics,
        CancellationTokenSource cts)
    {
        try
        {
            using var stream = new FileStream(
                candidate.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                StreamBufferSize,
                FileOptions.SequentialScan);

            if (!options.IncludeBinary && LooksBinary(stream))
            {
                metrics.IncrementSkippedBinary();
                return;
            }

            var fileLength = candidate.Length >= 0 ? candidate.Length : stream.Length;
            metrics.IncrementFilesSearched();
            metrics.AddBytesScanned(fileLength);

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: StreamBufferSize,
                leaveOpen: false);

            var matchLineCount = 0;
            var matchCount = 0;
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                cts.Token.ThrowIfCancellationRequested();

                var lineMatch = pattern.ScanLine(line, stopAfterFirstMatch: options.FilesWithMatches);
                if (!lineMatch.HasMatches)
                {
                    continue;
                }

                matchLineCount++;
                matchCount += lineMatch.MatchCount;

                if (options.FilesWithMatches)
                {
                    break;
                }
            }

            if (matchLineCount == 0)
            {
                return;
            }

            metrics.IncrementFilesMatched();
            metrics.AddMatchLines(matchLineCount);
            metrics.AddMatchCount(matchCount);

            if (options.MaxResults is int maxResults && metrics.ReadMatchLines() >= maxResults)
            {
                metrics.MarkLimitReached();
                cts.Cancel();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            metrics.IncrementErrors();
        }
    }

    private static IEnumerable<SearchCandidate> EnumerateCandidates(
        SearchOptions options,
        SearchMetrics metrics,
        string currentDirectory,
        IReadOnlyList<string> normalizedIncludeGlobs,
        IReadOnlyList<string> normalizedExcludeGlobs,
        CancellationToken cancellationToken)
    {
        foreach (var rawPath in options.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.GetFullPath(rawPath);
            if (!TryGetAttributes(fullPath, out var attributes))
            {
                if (Directory.Exists(fullPath) || File.Exists(fullPath))
                {
                    metrics.IncrementErrors();
                }
                else
                {
                    metrics.IncrementMissingPaths();
                }

                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                var rootDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
                if (TryCreateCandidate(rootDirectory, fullPath, attributes, TryGetFileLength(fullPath), currentDirectory, options, metrics, normalizedIncludeGlobs, normalizedExcludeGlobs, out var candidate))
                {
                    metrics.IncrementFilesEnumerated();
                    yield return candidate;
                }

                continue;
            }

            foreach (var candidate in EnumerateDirectory(fullPath, options, metrics, currentDirectory, normalizedIncludeGlobs, normalizedExcludeGlobs, cancellationToken))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<SearchCandidate> EnumerateDirectory(
        string rootPath,
        SearchOptions options,
        SearchMetrics metrics,
        string displayBasePath,
        IReadOnlyList<string> normalizedIncludeGlobs,
        IReadOnlyList<string> normalizedExcludeGlobs,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSearchDirectory = pendingDirectories.Pop();

            using var enumerator = EnumerateEntries(currentSearchDirectory).GetEnumerator();

            while (true)
            {
                FileSystemEntrySnapshot entry;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    entry = enumerator.Current;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    metrics.IncrementErrors();
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    if (!ShouldSkipDirectory(rootPath, entry.FullPath, entry.Attributes, options, metrics, normalizedExcludeGlobs))
                    {
                        pendingDirectories.Push(entry.FullPath);
                    }

                    continue;
                }

                if (TryCreateCandidate(rootPath, entry.FullPath, entry.Attributes, entry.Length, displayBasePath, options, metrics, normalizedIncludeGlobs, normalizedExcludeGlobs, out var candidate))
                {
                    metrics.IncrementFilesEnumerated();
                    yield return candidate;
                }
            }
        }
    }

    private static bool ShouldSkipDirectory(
        string rootPath,
        string directoryPath,
        FileAttributes attributes,
        SearchOptions options,
        SearchMetrics metrics,
        IReadOnlyList<string> normalizedExcludeGlobs)
    {
        var directoryName = Path.GetFileName(directoryPath);
        if (!options.IncludeHidden && IsHidden(directoryPath, attributes))
        {
            metrics.IncrementSkippedHidden();
            return true;
        }

        if (options.UseDefaultExcludes && DefaultExcludedDirectories.Contains(directoryName))
        {
            metrics.IncrementSkippedExcluded();
            return true;
        }

        if (normalizedExcludeGlobs.Count > 0)
        {
            var relativePath = GetNormalizedRelativePath(rootPath, directoryPath) + "/";
            if (MatchesAny(relativePath, directoryName, normalizedExcludeGlobs))
            {
                metrics.IncrementSkippedExcluded();
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateCandidate(
        string rootPath,
        string filePath,
        FileAttributes attributes,
        long length,
        string currentDirectory,
        SearchOptions options,
        SearchMetrics metrics,
        IReadOnlyList<string> normalizedIncludeGlobs,
        IReadOnlyList<string> normalizedExcludeGlobs,
        out SearchCandidate candidate)
    {
        candidate = default;

        if (!options.IncludeHidden && IsHidden(filePath, attributes))
        {
            metrics.IncrementSkippedHidden();
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        var requiresRelativePath = normalizedExcludeGlobs.Count > 0 || normalizedIncludeGlobs.Count > 0;
        var relativePath = requiresRelativePath
            ? GetNormalizedRelativePath(rootPath, filePath)
            : null;

        if (normalizedExcludeGlobs.Count > 0 && MatchesAny(relativePath!, fileName, normalizedExcludeGlobs))
        {
            metrics.IncrementSkippedExcluded();
            return false;
        }

        if (normalizedIncludeGlobs.Count > 0 && !MatchesAny(relativePath!, fileName, normalizedIncludeGlobs))
        {
            metrics.IncrementSkippedExcluded();
            return false;
        }

        candidate = new SearchCandidate(filePath, currentDirectory, length);
        return true;
    }

    private static bool MatchesAny(string relativePath, string name, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, relativePath, ignoreCase: true) ||
                FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsHidden(string path, FileAttributes attributes)
    {
        var fileName = Path.GetFileName(path);
        return attributes.HasFlag(FileAttributes.Hidden) || fileName.StartsWith(".", StringComparison.Ordinal);
    }

    private static string GetNormalizedRelativePath(string basePath, string fullPath)
    {
        var trimmedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.StartsWith(trimmedBasePath, PathComparison))
        {
            var startIndex = trimmedBasePath.Length;

            if (fullPath.Length == startIndex)
            {
                return ".";
            }

            if (startIndex < fullPath.Length &&
                (fullPath[startIndex] == Path.DirectorySeparatorChar || fullPath[startIndex] == Path.AltDirectorySeparatorChar))
            {
                startIndex++;
                return NormalizePath(fullPath[startIndex..]);
            }
        }

        return NormalizePath(Path.GetRelativePath(basePath, fullPath));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string[] NormalizePatterns(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return [];
        }

        var normalizedPatterns = new string[patterns.Count];
        for (var index = 0; index < patterns.Count; index++)
        {
            normalizedPatterns[index] = NormalizePath(patterns[index]);
        }

        return normalizedPatterns;
    }

    private static IEnumerable<FileSystemEntrySnapshot> EnumerateEntries(string directoryPath) =>
        new FileSystemEnumerable<FileSystemEntrySnapshot>(
            directoryPath,
            static (ref FileSystemEntry entry) => new FileSystemEntrySnapshot(
                entry.ToFullPath(),
                entry.Attributes,
                entry.IsDirectory,
                entry.IsDirectory ? 0 : entry.Length),
            EntryEnumerationOptions);

    private static long TryGetFileLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static bool LooksBinary(FileStream stream)
    {
        Span<byte> sample = stackalloc byte[4096];
        var bytesRead = stream.Read(sample);
        stream.Position = 0;

        return LooksBinary(sample[..bytesRead]);
    }

    private static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0)
        {
            return false;
        }

        var textBomLength = GetTextBomLength(sample);
        if (textBomLength is 2 or 4)
        {
            return false;
        }

        var suspiciousBytes = 0;

        for (var index = textBomLength; index < sample.Length; index++)
        {
            var current = sample[index];
            if (current == 0)
            {
                return true;
            }

            if (current < 8 || (current > 13 && current < 32))
            {
                suspiciousBytes++;
            }
        }

        return suspiciousBytes > sample.Length / 5;
    }

    private static int GetTextBomLength(ReadOnlySpan<byte> sample)
    {
        if (sample.Length >= 4)
        {
            if (sample[0] == 0xFF && sample[1] == 0xFE && sample[2] == 0x00 && sample[3] == 0x00)
            {
                return 4;
            }

            if (sample[0] == 0x00 && sample[1] == 0x00 && sample[2] == 0xFE && sample[3] == 0xFF)
            {
                return 4;
            }
        }

        if (sample.Length >= 3 &&
            sample[0] == 0xEF &&
            sample[1] == 0xBB &&
            sample[2] == 0xBF)
        {
            return 3;
        }

        if (sample.Length >= 2)
        {
            if ((sample[0] == 0xFF && sample[1] == 0xFE) ||
                (sample[0] == 0xFE && sample[1] == 0xFF))
            {
                return 2;
            }
        }

        return 0;
    }

    private readonly record struct SearchCandidate(string FullPath, string DisplayBasePath, long Length);

    private readonly record struct FileSystemEntrySnapshot(
        string FullPath,
        FileAttributes Attributes,
        bool IsDirectory,
        long Length);

    private sealed class SearchMetrics
    {
        private long _filesEnumerated;
        private long _filesSearched;
        private long _filesMatched;
        private long _matchLines;
        private long _matchCount;
        private long _bytesScanned;
        private long _filesSkippedHidden;
        private long _filesSkippedExcluded;
        private long _filesSkippedBinary;
        private long _missingPaths;
        private long _errors;
        private int _limitReached;

        public void IncrementFilesEnumerated() => Interlocked.Increment(ref _filesEnumerated);

        public void IncrementFilesSearched() => Interlocked.Increment(ref _filesSearched);

        public void IncrementFilesMatched() => Interlocked.Increment(ref _filesMatched);

        public void AddMatchLines(long value) => Interlocked.Add(ref _matchLines, value);

        public void AddMatchCount(long value) => Interlocked.Add(ref _matchCount, value);

        public void AddBytesScanned(long value) => Interlocked.Add(ref _bytesScanned, value);

        public void IncrementSkippedHidden() => Interlocked.Increment(ref _filesSkippedHidden);

        public void IncrementSkippedExcluded() => Interlocked.Increment(ref _filesSkippedExcluded);

        public void IncrementSkippedBinary() => Interlocked.Increment(ref _filesSkippedBinary);

        public void IncrementMissingPaths() => Interlocked.Increment(ref _missingPaths);

        public void IncrementErrors() => Interlocked.Increment(ref _errors);

        public long ReadMatchLines() => Interlocked.Read(ref _matchLines);

        public void MarkLimitReached() => Interlocked.Exchange(ref _limitReached, 1);

        public SearchSummary ToSummary(TimeSpan duration) =>
            new(
                FilesEnumerated: Interlocked.Read(ref _filesEnumerated),
                FilesSearched: Interlocked.Read(ref _filesSearched),
                FilesMatched: Interlocked.Read(ref _filesMatched),
                MatchLines: Interlocked.Read(ref _matchLines),
                MatchCount: Interlocked.Read(ref _matchCount),
                BytesScanned: Interlocked.Read(ref _bytesScanned),
                FilesSkippedHidden: Interlocked.Read(ref _filesSkippedHidden),
                FilesSkippedExcluded: Interlocked.Read(ref _filesSkippedExcluded),
                FilesSkippedBinary: Interlocked.Read(ref _filesSkippedBinary),
                MissingPaths: Interlocked.Read(ref _missingPaths),
                Errors: Interlocked.Read(ref _errors),
                LimitReached: Interlocked.CompareExchange(ref _limitReached, 0, 0) == 1,
                Duration: duration,
                RuntimeProfile: new SearchRuntimeProfile(
                    WorkerCount: Environment.ProcessorCount,
                    StreamBufferSize: StreamBufferSize,
                    TotalAvailableMemoryBytes: 0,
                    AutoTuned: false,
                    UsedFastLiteralFileMode: false));
    }

    private sealed class BaselineSearchPattern
    {
        private readonly string? _literal;
        private readonly int _literalLength;
        private readonly StringComparison _comparison;
        private readonly Regex? _regex;

        private BaselineSearchPattern(string literal, StringComparison comparison)
        {
            _literal = literal;
            _literalLength = literal.Length;
            _comparison = comparison;
        }

        private BaselineSearchPattern(Regex regex)
        {
            _regex = regex;
        }

        public static BaselineSearchPattern Create(SearchOptions options)
        {
            var ignoreCase = options.CaseMode switch
            {
                CaseMode.Insensitive => true,
                CaseMode.Sensitive => false,
                _ => ShouldIgnoreCaseInSmartMode(options.Pattern)
            };

            if (options.UseRegex)
            {
                var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (ignoreCase)
                {
                    regexOptions |= RegexOptions.IgnoreCase;
                }

                return new BaselineSearchPattern(new Regex(options.Pattern, regexOptions));
            }

            return new BaselineSearchPattern(
                options.Pattern,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        public BaselineLineMatch ScanLine(string line, bool stopAfterFirstMatch)
        {
            if (_regex is not null)
            {
                var count = 0;

                foreach (var match in _regex.EnumerateMatches(line))
                {
                    if (match.Length == 0)
                    {
                        continue;
                    }

                    count++;
                    if (stopAfterFirstMatch)
                    {
                        break;
                    }
                }

                return new BaselineLineMatch(count);
            }

            ArgumentNullException.ThrowIfNull(_literal);

            var needle = _literal.AsSpan();
            var cursor = 0;
            var countLiteral = 0;

            while (cursor <= line.Length - _literalLength)
            {
                var index = line.AsSpan(cursor).IndexOf(needle, _comparison);
                if (index < 0)
                {
                    break;
                }

                countLiteral++;
                if (stopAfterFirstMatch)
                {
                    break;
                }

                cursor += index + _literalLength;
            }

            return new BaselineLineMatch(countLiteral);
        }

        private static bool ShouldIgnoreCaseInSmartMode(string pattern)
        {
            foreach (var character in pattern)
            {
                if (char.IsUpper(character))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private readonly record struct BaselineLineMatch(int MatchCount)
    {
        public bool HasMatches => MatchCount > 0;
    }
}
