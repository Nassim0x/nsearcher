using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;

namespace NSearcher.Core;

public sealed class SearchEngine
{
    private const int LiteralPrefilterThresholdBytes = 128 * 1024;
    private const int WholeFileLiteralPrefilterMaxBytes = 8 * 1024 * 1024;
    private const int BinarySampleBytes = 4096;
    // FileStream buffering is redundant here because the engine already batches reads
    // through StreamReader or explicit chunk scans.
    private const int FileStreamInternalBufferSize = 1;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly SearchValues<byte> SuspiciousBinaryBytes = SearchValues.Create(CreateSuspiciousBinaryByteSet());
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

    public Task<SearchSummary> ExecuteAsync(
        SearchOptions options,
        Func<SearchFileResult, CancellationToken, ValueTask>? onResult = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();

        var pattern = SearchPattern.Create(options);
        var metrics = new SearchMetrics();
        var callback = onResult ?? IgnoreResultAsync;
        var emitResults = onResult is not null;
        var stopwatch = Stopwatch.StartNew();
        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var normalizedIncludeGlobs = NormalizePatterns(options.IncludeGlobs);
        var normalizedExcludeGlobs = NormalizePatterns(options.ExcludeGlobs);
        var exactLiteralUtf8Bytes = pattern.TryGetLiteralUtf8Bytes(out var exactUtf8Bytes) ? exactUtf8Bytes : null;
        var filePrefilterUtf8Bytes = pattern.TryGetPrefilterUtf8Bytes(out var prefilterUtf8Bytes) ? prefilterUtf8Bytes : null;
        var candidates = MaterializeCandidates(
            EnumerateCandidates(options, metrics, currentDirectory, normalizedIncludeGlobs, normalizedExcludeGlobs, cancellationToken));
        var canUseFastLiteralFileMode = exactLiteralUtf8Bytes is not null &&
            options.BeforeContext == 0 &&
            options.AfterContext == 0 &&
            (options.FilesWithMatches || options.CountOnly);
        var canDeferSummaryResultEmission = emitResults &&
            options.BeforeContext == 0 &&
            options.AfterContext == 0 &&
            (options.FilesWithMatches || options.CountOnly);
        var runtimeProfile = SearchRuntimeTuner.CreateProfile(
            options,
            candidates,
            canUseFastLiteralFileMode);
        var executionPartition = CreateExecutionPartition(candidates, runtimeProfile);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cts.Token,
            MaxDegreeOfParallelism = runtimeProfile.WorkerCount
        };

        try
        {
            if (!emitResults)
            {
                Parallel.ForEach(
                    executionPartition,
                    parallelOptions,
                    candidate =>
                    {
                        SearchFileMetricsOnly(candidate, pattern, exactLiteralUtf8Bytes, filePrefilterUtf8Bytes, runtimeProfile, options, metrics, cts);
                    });
            }
            else if (canDeferSummaryResultEmission)
            {
                var deferredResults = new List<SearchFileResult>();
                var deferredResultsSync = new object();

                Parallel.ForEach(
                    executionPartition,
                    parallelOptions,
                    static () => new List<SearchFileResult>(capacity: 4),
                    (candidate, _, localResults) =>
                    {
                        var result = SearchFile(candidate, pattern, exactLiteralUtf8Bytes, filePrefilterUtf8Bytes, runtimeProfile, options, metrics, cts);
                        if (result is not null && (result.HasMatches || result.HasError))
                        {
                            localResults.Add(result);
                        }

                        return localResults;
                    },
                    localResults =>
                    {
                        if (localResults.Count == 0)
                        {
                            return;
                        }

                        lock (deferredResultsSync)
                        {
                            deferredResults.AddRange(localResults);
                        }
                    });

                foreach (var result in deferredResults)
                {
                    CompleteSynchronously(callback(result, cts.Token));
                }
            }
            else
            {
                Parallel.ForEach(
                    executionPartition,
                    parallelOptions,
                    candidate =>
                    {
                        var result = SearchFile(candidate, pattern, exactLiteralUtf8Bytes, filePrefilterUtf8Bytes, runtimeProfile, options, metrics, cts);
                        if (result is not null && (result.HasMatches || result.HasError))
                        {
                            CompleteSynchronously(callback(result, cts.Token));
                        }
                    });
            }
        }
        catch (AggregateException exception)
            when (cts.IsCancellationRequested &&
                  !cancellationToken.IsCancellationRequested &&
                  exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }

        stopwatch.Stop();
        return Task.FromResult(metrics.ToSummary(stopwatch.Elapsed, runtimeProfile));
    }

    private static SearchWorkloadSnapshot MaterializeCandidates(IEnumerable<SearchCandidate> candidates)
    {
        var materialized = new List<SearchCandidate>();
        long totalBytes = 0;
        long largestFileBytes = 0;
        long filesWithKnownLength = 0;

        foreach (var candidate in candidates)
        {
            materialized.Add(candidate);

            if (candidate.Length < 0)
            {
                continue;
            }

            totalBytes += candidate.Length;
            filesWithKnownLength++;
            largestFileBytes = Math.Max(largestFileBytes, candidate.Length);
        }

        return new SearchWorkloadSnapshot(
            materialized.ToArray(),
            materialized.Count,
            totalBytes,
            largestFileBytes,
            filesWithKnownLength);
    }

    private static OrderablePartitioner<SearchCandidate> CreateExecutionPartition(
        SearchWorkloadSnapshot workload,
        SearchRuntimeProfile runtimeProfile)
    {
        if (workload.CandidateCount <= 1)
        {
            return Partitioner.Create(workload.Candidates, loadBalance: true);
        }

        var candidates = workload.Candidates;
        var averageFileBytes = workload.AverageFileBytes;
        var shouldFrontLoadLargeFiles =
            averageFileBytes > 0 &&
            workload.LargestFileBytes >= averageFileBytes * 4 &&
            workload.CandidateCount >= runtimeProfile.WorkerCount * 2;

        if (!shouldFrontLoadLargeFiles)
        {
            return Partitioner.Create(candidates, loadBalance: true);
        }

        var ordered = candidates.ToArray();
        Array.Sort(
            ordered,
            static (left, right) => right.Length.CompareTo(left.Length));

        return Partitioner.Create(ordered, loadBalance: true);
    }

    private static void CompleteSynchronously(ValueTask valueTask)
    {
        if (valueTask.IsCompletedSuccessfully)
        {
            return;
        }

        valueTask.AsTask().GetAwaiter().GetResult();
    }

    private static void SearchFileMetricsOnly(
        SearchCandidate candidate,
        SearchPattern pattern,
        byte[]? exactLiteralUtf8Bytes,
        byte[]? filePrefilterUtf8Bytes,
        SearchRuntimeProfile runtimeProfile,
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
                FileStreamInternalBufferSize,
                FileOptions.SequentialScan);

            var fileLength = candidate.Length >= 0 ? candidate.Length : stream.Length;
            if (runtimeProfile.UsedFastLiteralFileMode && exactLiteralUtf8Bytes is not null)
            {
                var fastResult = FastSearchLiteralFile(
                    stream,
                    exactLiteralUtf8Bytes,
                    fileLength,
                    runtimeProfile.StreamBufferSize,
                    options.FilesWithMatches);

                if (fastResult.Status == FastLiteralFileStatus.SkipBinary)
                {
                    metrics.IncrementSkippedBinary();
                    return;
                }

                metrics.IncrementFilesSearched();
                metrics.AddBytesScanned(fileLength);

                if (fastResult.Status == FastLiteralFileStatus.NoMatch)
                {
                    return;
                }

                if (fastResult.Status != FastLiteralFileStatus.FallbackToText)
                {
                    metrics.IncrementFilesMatched();
                    metrics.AddMatchLines(fastResult.MatchLineCount);
                    metrics.AddMatchCount(fastResult.MatchCount);

                    if (options.MaxResults is int fastMaxResults && metrics.ReadMatchLines() >= fastMaxResults)
                    {
                        metrics.MarkLimitReached();
                        cts.Cancel();
                    }

                    return;
                }

                stream.Position = 0;
            }

            var probeResult = ProbeFileStream(stream, fileLength, filePrefilterUtf8Bytes, options, runtimeProfile.StreamBufferSize);
            if (probeResult == FileProbeResult.SkipBinary)
            {
                metrics.IncrementSkippedBinary();
                return;
            }

            metrics.IncrementFilesSearched();
            metrics.AddBytesScanned(fileLength);

            if (probeResult == FileProbeResult.NoMatch)
            {
                return;
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: runtimeProfile.StreamBufferSize,
                leaveOpen: false);

            var simpleScan = ScanReaderWithoutOutput(
                reader,
                pattern,
                options,
                GetSummaryScanBufferSize(runtimeProfile.StreamBufferSize),
                cts.Token);

            if (simpleScan.MatchLineCount == 0)
            {
                return;
            }

            metrics.IncrementFilesMatched();
            metrics.AddMatchLines(simpleScan.MatchLineCount);
            metrics.AddMatchCount(simpleScan.MatchCount);

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

    private static SearchFileResult? SearchFile(
        SearchCandidate candidate,
        SearchPattern pattern,
        byte[]? exactLiteralUtf8Bytes,
        byte[]? filePrefilterUtf8Bytes,
        SearchRuntimeProfile runtimeProfile,
        SearchOptions options,
        SearchMetrics metrics,
        CancellationTokenSource cts)
    {
        try
        {
            var captureOccurrences = options.UseColor && !options.CountOnly && !options.FilesWithMatches;
            var trackFirstMatchMetadata = !options.CountOnly && !options.FilesWithMatches;

            using var stream = new FileStream(
                candidate.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileStreamInternalBufferSize,
                FileOptions.SequentialScan);

            var fileLength = candidate.Length >= 0 ? candidate.Length : stream.Length;
            if (runtimeProfile.UsedFastLiteralFileMode && exactLiteralUtf8Bytes is not null)
            {
                var fastResult = FastSearchLiteralFile(
                    stream,
                    exactLiteralUtf8Bytes,
                    fileLength,
                    runtimeProfile.StreamBufferSize,
                    options.FilesWithMatches);

                if (fastResult.Status == FastLiteralFileStatus.SkipBinary)
                {
                    metrics.IncrementSkippedBinary();
                    return null;
                }

                metrics.IncrementFilesSearched();
                metrics.AddBytesScanned(fileLength);

                if (fastResult.Status == FastLiteralFileStatus.NoMatch)
                {
                    return null;
                }

                if (fastResult.Status == FastLiteralFileStatus.FallbackToText)
                {
                    stream.Position = 0;
                }
                else
                {
                    metrics.IncrementFilesMatched();
                    metrics.AddMatchLines(fastResult.MatchLineCount);
                    metrics.AddMatchCount(fastResult.MatchCount);

                    if (options.MaxResults is int fastMaxResults && metrics.ReadMatchLines() >= fastMaxResults)
                    {
                        metrics.MarkLimitReached();
                        cts.Cancel();
                    }

                    var fastDisplayPath = GetNormalizedRelativePath(candidate.DisplayBasePath, candidate.FullPath);
                    return new SearchFileResult(
                        fastDisplayPath,
                        fastResult.MatchLineCount,
                        fastResult.MatchCount,
                        fileLength,
                        Array.Empty<SearchOutputLine>());
                }
            }

            var probeResult = ProbeFileStream(stream, fileLength, filePrefilterUtf8Bytes, options, runtimeProfile.StreamBufferSize);
            if (probeResult == FileProbeResult.SkipBinary)
            {
                metrics.IncrementSkippedBinary();
                return null;
            }

            metrics.IncrementFilesSearched();
            metrics.AddBytesScanned(fileLength);

            if (probeResult == FileProbeResult.NoMatch)
            {
                return null;
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: runtimeProfile.StreamBufferSize,
                leaveOpen: false);

            if (options.CountOnly || options.FilesWithMatches)
            {
                var simpleScan = ScanReaderWithoutOutput(
                    reader,
                    pattern,
                    options,
                    GetSummaryScanBufferSize(runtimeProfile.StreamBufferSize),
                    cts.Token);

                if (simpleScan.MatchLineCount == 0)
                {
                    return null;
                }

                metrics.IncrementFilesMatched();
                metrics.AddMatchLines(simpleScan.MatchLineCount);
                metrics.AddMatchCount(simpleScan.MatchCount);

                if (options.MaxResults is int simpleMaxResults && metrics.ReadMatchLines() >= simpleMaxResults)
                {
                    metrics.MarkLimitReached();
                    cts.Cancel();
                }

                var simpleDisplayPath = GetNormalizedRelativePath(candidate.DisplayBasePath, candidate.FullPath);
                return new SearchFileResult(
                    simpleDisplayPath,
                    simpleScan.MatchLineCount,
                    simpleScan.MatchCount,
                    fileLength,
                    Array.Empty<SearchOutputLine>());
            }

            var outputLines = new List<SearchOutputLine>(capacity: 16);
            var beforeContext = options.BeforeContext == 0
                ? null
                : new Queue<SearchOutputLine>(options.BeforeContext);

            var lineNumber = 0;
            var lastEmittedLineNumber = 0;
            var remainingAfterContext = 0;
            var matchLineCount = 0;
            var matchCount = 0;
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                cts.Token.ThrowIfCancellationRequested();
                lineNumber++;

                var lineMatch = pattern.ScanLine(
                    line,
                    captureOccurrences,
                    trackFirstMatchMetadata,
                    stopAfterFirstMatch: options.FilesWithMatches);

                if (lineMatch.HasMatches)
                {
                    matchLineCount++;
                    matchCount += lineMatch.MatchCount;

                    FlushBeforeContext(beforeContext, outputLines, ref lastEmittedLineNumber);

                    if (lineNumber > lastEmittedLineNumber)
                    {
                        outputLines.Add(new SearchOutputLine(
                            lineNumber,
                            line,
                            SearchOutputLineKind.Match,
                            lineMatch.FirstMatchStart,
                            lineMatch.FirstMatchLength,
                            lineMatch.Occurrences));
                        lastEmittedLineNumber = lineNumber;
                    }

                    if (options.FilesWithMatches)
                    {
                        break;
                    }

                    remainingAfterContext = Math.Max(remainingAfterContext, options.AfterContext);
                }
                else if (remainingAfterContext > 0)
                {
                    if (lineNumber > lastEmittedLineNumber)
                    {
                        outputLines.Add(new SearchOutputLine(
                            lineNumber,
                            line,
                            SearchOutputLineKind.Context,
                            -1,
                            0,
                            null));
                        lastEmittedLineNumber = lineNumber;
                    }

                    remainingAfterContext--;
                }

                if (beforeContext is not null)
                {
                    beforeContext.Enqueue(new SearchOutputLine(
                        lineNumber,
                        line,
                        SearchOutputLineKind.Context,
                        -1,
                        0,
                        null));

                    while (beforeContext.Count > options.BeforeContext)
                    {
                        beforeContext.Dequeue();
                    }
                }
            }

            if (matchLineCount == 0)
            {
                return null;
            }

            metrics.IncrementFilesMatched();
            metrics.AddMatchLines(matchLineCount);
            metrics.AddMatchCount(matchCount);

            if (options.MaxResults is int maxResults && metrics.ReadMatchLines() >= maxResults)
            {
                metrics.MarkLimitReached();
                cts.Cancel();
            }

            var displayPath = GetNormalizedRelativePath(candidate.DisplayBasePath, candidate.FullPath);

            return new SearchFileResult(
                displayPath,
                matchLineCount,
                matchCount,
                fileLength,
                outputLines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            metrics.IncrementErrors();

            if (!options.ReportErrors)
            {
                return null;
            }

            var displayPath = GetNormalizedRelativePath(candidate.DisplayBasePath, candidate.FullPath);

            return new SearchFileResult(
                displayPath,
                0,
                0,
                0,
                Array.Empty<SearchOutputLine>(),
                exception.Message);
        }
    }

    private static SimpleScanResult ScanReaderWithoutOutput(
        StreamReader reader,
        SearchPattern pattern,
        SearchOptions options,
        int scanBufferSize,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(1024, scanBufferSize));
        var matchLineCount = 0;
        var matchCount = 0;

        try
        {
            var carryLength = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (carryLength == buffer.Length)
                {
                    var expandedBuffer = ArrayPool<char>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, carryLength).CopyTo(expandedBuffer);
                    ArrayPool<char>.Shared.Return(buffer);
                    buffer = expandedBuffer;
                }

                var charsRead = reader.Read(buffer, carryLength, buffer.Length - carryLength);
                if (charsRead == 0)
                {
                    if (carryLength > 0)
                    {
                        CountLineMatches(
                            buffer.AsSpan(0, carryLength),
                            pattern,
                            options.FilesWithMatches,
                            ref matchLineCount,
                            ref matchCount);
                    }

                    return new SimpleScanResult(matchLineCount, matchCount);
                }

                var totalChars = carryLength + charsRead;
                var chunk = buffer.AsSpan(0, totalChars);
                var lineStart = 0;

                while (lineStart < totalChars)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var newlineOffset = chunk[lineStart..].IndexOf('\n');
                    if (newlineOffset < 0)
                    {
                        break;
                    }

                    CountLineMatches(
                        chunk.Slice(lineStart, newlineOffset),
                        pattern,
                        options.FilesWithMatches,
                        ref matchLineCount,
                        ref matchCount);

                    if (options.FilesWithMatches && matchLineCount > 0)
                    {
                        return new SimpleScanResult(matchLineCount, matchCount);
                    }

                    lineStart += newlineOffset + 1;
                }

                carryLength = totalChars - lineStart;
                if (carryLength > 0)
                {
                    chunk[lineStart..].CopyTo(buffer);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static int GetSummaryScanBufferSize(int streamBufferSize) =>
        Math.Max(4096, streamBufferSize / sizeof(char));

    private static void CountLineMatches(
        ReadOnlySpan<char> line,
        SearchPattern pattern,
        bool stopAfterFirstMatch,
        ref int matchLineCount,
        ref int matchCount)
    {
        if (!line.IsEmpty && line[^1] == '\r')
        {
            line = line[..^1];
        }

        var lineMatchCount = pattern.CountLineMatches(line, stopAfterFirstMatch);
        if (lineMatchCount == 0)
        {
            return;
        }

        matchLineCount++;
        matchCount += lineMatchCount;
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

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                foreach (var candidate in EnumerateDirectory(fullPath, options, metrics, currentDirectory, normalizedIncludeGlobs, normalizedExcludeGlobs, cancellationToken))
                {
                    yield return candidate;
                }

                continue;
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

        candidate = new SearchCandidate(
            filePath,
            currentDirectory,
            length);
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

    private static void FlushBeforeContext(
        Queue<SearchOutputLine>? beforeContext,
        List<SearchOutputLine> outputLines,
        ref int lastEmittedLineNumber)
    {
        if (beforeContext is null)
        {
            return;
        }

        foreach (var contextLine in beforeContext)
        {
            if (contextLine.LineNumber <= lastEmittedLineNumber)
            {
                continue;
            }

            outputLines.Add(contextLine);
            lastEmittedLineNumber = contextLine.LineNumber;
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

    private static ValueTask IgnoreResultAsync(SearchFileResult _, CancellationToken __) => ValueTask.CompletedTask;

    private static FileProbeResult ProbeFileStream(
        FileStream stream,
        long fileLength,
        byte[]? prefilterUtf8Bytes,
        SearchOptions options,
        int streamBufferSize)
    {
        if (!options.IncludeBinary &&
            prefilterUtf8Bytes is not null &&
            ShouldUseLiteralPrefilter(fileLength, prefilterUtf8Bytes.Length))
        {
            var prefilterResult = ScanStreamForLiteral(stream, prefilterUtf8Bytes, streamBufferSize);
            stream.Position = 0;
            return prefilterResult;
        }

        if (!options.IncludeBinary && LooksBinary(stream))
        {
            return FileProbeResult.SkipBinary;
        }

        return FileProbeResult.SearchText;
    }

    private static bool ShouldUseLiteralPrefilter(long fileLength, int needleLength) =>
        fileLength >= LiteralPrefilterThresholdBytes && needleLength >= 3 && fileLength >= needleLength;

    private static FileProbeResult ScanStreamForLiteral(FileStream stream, byte[] needle, int streamBufferSize)
    {
        if (stream.Length < needle.Length)
        {
            return FileProbeResult.NoMatch;
        }

        if (stream.Length <= WholeFileLiteralPrefilterMaxBytes)
        {
            var wholeFileLength = (int)stream.Length;
            var wholeFileBuffer = ArrayPool<byte>.Shared.Rent(wholeFileLength);

            try
            {
                var totalBytesRead = 0;
                while (totalBytesRead < wholeFileLength)
                {
                    var bytesRead = stream.Read(wholeFileBuffer, totalBytesRead, wholeFileLength - totalBytesRead);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                }

                var wholeFile = wholeFileBuffer.AsSpan(0, totalBytesRead);
                var bomLength = GetTextBomLength(wholeFile);
                if (bomLength is 2 or 4)
                {
                    return FileProbeResult.FallbackToText;
                }

                if (ContainsBinarySample(wholeFile, bomLength))
                {
                    return FileProbeResult.SkipBinary;
                }

                return wholeFile[bomLength..].IndexOf(needle) >= 0
                    ? FileProbeResult.SearchText
                    : FileProbeResult.NoMatch;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(wholeFileBuffer);
            }
        }

        var rentedBuffer = ArrayPool<byte>.Shared.Rent(streamBufferSize + needle.Length);

        try
        {
            var buffer = rentedBuffer.AsSpan();
            var carryLength = 0;
            var isFirstChunk = true;

            while (true)
            {
                var bytesRead = stream.Read(rentedBuffer, carryLength, streamBufferSize);
                if (bytesRead == 0)
                {
                    return FileProbeResult.NoMatch;
                }

                var totalBytes = carryLength + bytesRead;
                var chunk = buffer[..totalBytes];
                var incomingBytes = buffer.Slice(carryLength, bytesRead);

                if (isFirstChunk)
                {
                    var bomLength = GetTextBomLength(chunk);
                    if (bomLength is 2 or 4)
                    {
                        return FileProbeResult.FallbackToText;
                    }

                    if (ContainsBinaryData(chunk, bomLength))
                    {
                        return FileProbeResult.SkipBinary;
                    }

                    if (chunk[bomLength..].IndexOf(needle) >= 0)
                    {
                        return FileProbeResult.SearchText;
                    }
                }
                else if (ContainsBinaryData(incomingBytes, 0))
                {
                    return FileProbeResult.SkipBinary;
                }

                if (!isFirstChunk && chunk.IndexOf(needle) >= 0)
                {
                    return FileProbeResult.SearchText;
                }

                carryLength = Math.Min(needle.Length - 1, totalBytes);
                if (carryLength > 0)
                {
                    chunk[^carryLength..].CopyTo(buffer);
                }

                isFirstChunk = false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static FastLiteralFileResult FastSearchLiteralFile(
        FileStream stream,
        byte[] needle,
        long fileLength,
        int streamBufferSize,
        bool stopAfterFirstMatchingLine)
    {
        if (stopAfterFirstMatchingLine)
        {
            return FastSearchLiteralPresence(stream, needle, fileLength, streamBufferSize);
        }

        if (fileLength < needle.Length)
        {
            return FastLiteralFileResult.NoMatch;
        }

        if (fileLength <= WholeFileLiteralPrefilterMaxBytes)
        {
            var wholeFileBuffer = ArrayPool<byte>.Shared.Rent((int)fileLength);

            try
            {
                var totalBytesRead = 0;
                while (totalBytesRead < fileLength)
                {
                    var bytesRead = stream.Read(wholeFileBuffer, totalBytesRead, (int)fileLength - totalBytesRead);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                }

                return ProcessLiteralBytes(
                    wholeFileBuffer.AsSpan(0, totalBytesRead),
                    needle,
                    stopAfterFirstMatchingLine);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(wholeFileBuffer);
            }
        }

        var buffer = ArrayPool<byte>.Shared.Rent(streamBufferSize + needle.Length);

        try
        {
            var carryLength = 0;
            var matchLineCount = 0;
            var matchCount = 0;
            var isFirstChunk = true;
            var startOffset = 0;

            while (true)
            {
                var bytesRead = stream.Read(buffer, carryLength, streamBufferSize);
                if (bytesRead == 0)
                {
                    if (carryLength > 0)
                    {
                        var finalLineResult = CountMatchesInByteLine(
                            buffer.AsSpan(0, carryLength),
                            needle,
                            stopAfterFirstMatchingLine);

                        if (finalLineResult.LineHasMatch)
                        {
                            matchLineCount++;
                            matchCount += finalLineResult.MatchCount;
                        }
                    }

                    return matchLineCount > 0
                        ? new FastLiteralFileResult(FastLiteralFileStatus.Match, matchLineCount, matchCount)
                        : FastLiteralFileResult.NoMatch;
                }

                var totalBytes = carryLength + bytesRead;
                var chunk = buffer.AsSpan(0, totalBytes);
                var incomingBytes = buffer.AsSpan(carryLength, bytesRead);

                if (isFirstChunk)
                {
                    var bomLength = GetTextBomLength(chunk);
                    if (bomLength is 2 or 4)
                    {
                        return FastLiteralFileResult.FallbackToText;
                    }

                    if (ContainsBinaryData(chunk, bomLength))
                    {
                        return FastLiteralFileResult.SkipBinary;
                    }

                    startOffset = bomLength;
                    isFirstChunk = false;
                }
                else if (ContainsBinaryData(incomingBytes, 0))
                {
                    return FastLiteralFileResult.SkipBinary;
                }

                var lineStart = startOffset;
                startOffset = 0;

                while (lineStart < totalBytes)
                {
                    var newlineOffset = chunk[lineStart..].IndexOf((byte)'\n');
                    if (newlineOffset < 0)
                    {
                        break;
                    }

                    var lineEnd = lineStart + newlineOffset;
                    var lineResult = CountMatchesInByteLine(
                        chunk[lineStart..lineEnd],
                        needle,
                        stopAfterFirstMatchingLine);

                    if (lineResult.LineHasMatch)
                    {
                        matchLineCount++;
                        matchCount += lineResult.MatchCount;

                        if (stopAfterFirstMatchingLine)
                        {
                            return new FastLiteralFileResult(FastLiteralFileStatus.Match, matchLineCount, matchCount);
                        }
                    }

                    lineStart = lineEnd + 1;
                }

                carryLength = totalBytes - lineStart;
                if (carryLength > 0)
                {
                    chunk[lineStart..].CopyTo(buffer);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FastLiteralFileResult FastSearchLiteralPresence(
        FileStream stream,
        byte[] needle,
        long fileLength,
        int streamBufferSize)
    {
        if (fileLength < needle.Length)
        {
            return FastLiteralFileResult.NoMatch;
        }

        if (fileLength <= WholeFileLiteralPrefilterMaxBytes)
        {
            var wholeFileBuffer = ArrayPool<byte>.Shared.Rent((int)fileLength);

            try
            {
                var totalBytesRead = 0;
                while (totalBytesRead < fileLength)
                {
                    var bytesRead = stream.Read(wholeFileBuffer, totalBytesRead, (int)fileLength - totalBytesRead);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytesRead += bytesRead;
                }

                var wholeFile = wholeFileBuffer.AsSpan(0, totalBytesRead);
                var bomLength = GetTextBomLength(wholeFile);
                if (bomLength is 2 or 4)
                {
                    return FastLiteralFileResult.FallbackToText;
                }

                var sampleLength = Math.Min(wholeFile.Length, 4096);
                if (sampleLength > 0 && LooksBinary(wholeFile[..sampleLength]))
                {
                    return FastLiteralFileResult.SkipBinary;
                }

                return wholeFile[bomLength..].IndexOf(needle) >= 0
                    ? new FastLiteralFileResult(FastLiteralFileStatus.Match, 1, 1)
                    : FastLiteralFileResult.NoMatch;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(wholeFileBuffer);
            }
        }

        var buffer = ArrayPool<byte>.Shared.Rent(streamBufferSize + needle.Length);

        try
        {
            var carryLength = 0;
            var isFirstChunk = true;

            while (true)
            {
                var bytesRead = stream.Read(buffer, carryLength, streamBufferSize);
                if (bytesRead == 0)
                {
                    return FastLiteralFileResult.NoMatch;
                }

                var totalBytes = carryLength + bytesRead;
                var chunk = buffer.AsSpan(0, totalBytes);

                if (isFirstChunk)
                {
                    var bomLength = GetTextBomLength(chunk);
                    if (bomLength is 2 or 4)
                    {
                        return FastLiteralFileResult.FallbackToText;
                    }

                    var sampleLength = Math.Min(totalBytes, 4096);
                    if (LooksBinary(chunk[..sampleLength]))
                    {
                        return FastLiteralFileResult.SkipBinary;
                    }

                    if (chunk[bomLength..].IndexOf(needle) >= 0)
                    {
                        return new FastLiteralFileResult(FastLiteralFileStatus.Match, 1, 1);
                    }

                    isFirstChunk = false;
                }
                else if (chunk.IndexOf(needle) >= 0)
                {
                    return new FastLiteralFileResult(FastLiteralFileStatus.Match, 1, 1);
                }

                carryLength = Math.Min(needle.Length - 1, totalBytes);
                if (carryLength > 0)
                {
                    chunk[^carryLength..].CopyTo(buffer);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FastLiteralFileResult ProcessLiteralBytes(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> needle,
        bool stopAfterFirstMatchingLine)
    {
        var bomLength = GetTextBomLength(bytes);
        if (bomLength is 2 or 4)
        {
            return FastLiteralFileResult.FallbackToText;
        }

        if (ContainsBinarySample(bytes, bomLength))
        {
            return FastLiteralFileResult.SkipBinary;
        }

        var matchLineCount = 0;
        var matchCount = 0;
        var lineStart = bomLength;

        while (lineStart < bytes.Length)
        {
            var newlineOffset = bytes[lineStart..].IndexOf((byte)'\n');
            if (newlineOffset < 0)
            {
                break;
            }

            var lineEnd = lineStart + newlineOffset;
            var lineResult = CountMatchesInByteLine(
                bytes[lineStart..lineEnd],
                needle,
                stopAfterFirstMatchingLine);

            if (lineResult.LineHasMatch)
            {
                matchLineCount++;
                matchCount += lineResult.MatchCount;

                if (stopAfterFirstMatchingLine)
                {
                    return new FastLiteralFileResult(FastLiteralFileStatus.Match, matchLineCount, matchCount);
                }
            }

            lineStart = lineEnd + 1;
        }

        if (lineStart < bytes.Length)
        {
            var finalLineResult = CountMatchesInByteLine(
                bytes[lineStart..],
                needle,
                stopAfterFirstMatchingLine);

            if (finalLineResult.LineHasMatch)
            {
                matchLineCount++;
                matchCount += finalLineResult.MatchCount;
            }
        }

        return matchLineCount > 0
            ? new FastLiteralFileResult(FastLiteralFileStatus.Match, matchLineCount, matchCount)
            : FastLiteralFileResult.NoMatch;
    }

    private static ByteLineMatchResult CountMatchesInByteLine(
        ReadOnlySpan<byte> line,
        ReadOnlySpan<byte> needle,
        bool stopAfterFirstMatchingLine)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        var matchCount = 0;
        var cursor = 0;

        while (cursor <= line.Length - needle.Length)
        {
            var offset = line[cursor..].IndexOf(needle);
            if (offset < 0)
            {
                break;
            }

            matchCount++;
            if (stopAfterFirstMatchingLine)
            {
                return new ByteLineMatchResult(true, 1);
            }

            cursor += offset + needle.Length;
        }

        return new ByteLineMatchResult(matchCount > 0, matchCount);
    }


    private static bool ContainsBinaryData(ReadOnlySpan<byte> sample, int startIndex)
    {
        return startIndex < sample.Length &&
            sample[startIndex..].IndexOfAny(SuspiciousBinaryBytes) >= 0;
    }

    private static bool ContainsBinarySample(ReadOnlySpan<byte> sample, int startIndex)
    {
        if (startIndex >= sample.Length)
        {
            return false;
        }

        var sampleLength = Math.Min(sample.Length - startIndex, BinarySampleBytes);
        return ContainsBinaryData(sample.Slice(startIndex, sampleLength), 0);
    }

    private static byte[] CreateSuspiciousBinaryByteSet()
    {
        var bytes = new byte[26];
        var position = 0;

        for (byte value = 0; value < 8; value++)
        {
            bytes[position++] = value;
        }

        for (byte value = 14; value < 32; value++)
        {
            bytes[position++] = value;
        }

        return bytes;
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

    private readonly record struct FileSystemEntrySnapshot(
        string FullPath,
        FileAttributes Attributes,
        bool IsDirectory,
        long Length);

    private enum FileProbeResult
    {
        SearchText,
        NoMatch,
        SkipBinary,
        FallbackToText
    }

    private readonly record struct SimpleScanResult(int MatchLineCount, int MatchCount);

    private readonly record struct ByteLineMatchResult(bool LineHasMatch, int MatchCount);

    private readonly record struct FastLiteralFileResult(
        FastLiteralFileStatus Status,
        int MatchLineCount,
        int MatchCount)
    {
        public static FastLiteralFileResult NoMatch => new(FastLiteralFileStatus.NoMatch, 0, 0);

        public static FastLiteralFileResult SkipBinary => new(FastLiteralFileStatus.SkipBinary, 0, 0);

        public static FastLiteralFileResult FallbackToText => new(FastLiteralFileStatus.FallbackToText, 0, 0);
    }

    private enum FastLiteralFileStatus
    {
        Match,
        NoMatch,
        SkipBinary,
        FallbackToText
    }

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

        public SearchSummary ToSummary(TimeSpan duration, SearchRuntimeProfile runtimeProfile) =>
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
                RuntimeProfile: runtimeProfile);
    }

}
