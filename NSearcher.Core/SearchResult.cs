namespace NSearcher.Core;

public enum SearchOutputLineKind
{
    Match,
    Context
}

public readonly record struct MatchOccurrence(int Start, int Length);

internal readonly record struct SearchLineMatch(
    int MatchCount,
    int FirstMatchStart,
    int FirstMatchLength,
    IReadOnlyList<MatchOccurrence>? Occurrences)
{
    public bool HasMatches => MatchCount > 0;
}

public sealed record SearchOutputLine(
    int LineNumber,
    string Text,
    SearchOutputLineKind Kind,
    int FirstMatchStart,
    int FirstMatchLength,
    IReadOnlyList<MatchOccurrence>? Occurrences);

public sealed record SearchFileResult(
    string FilePath,
    int MatchLineCount,
    int MatchCount,
    long BytesRead,
    IReadOnlyList<SearchOutputLine> OutputLines,
    string? ErrorMessage = null)
{
    public bool HasMatches => MatchLineCount > 0;

    public bool HasError => ErrorMessage is not null;
}

public sealed record SearchRuntimeProfile(
    int WorkerCount,
    int StreamBufferSize,
    long TotalAvailableMemoryBytes,
    bool AutoTuned,
    bool UsedFastLiteralFileMode);

public sealed record SearchSummary(
    long FilesEnumerated,
    long FilesSearched,
    long FilesMatched,
    long MatchLines,
    long MatchCount,
    long BytesScanned,
    long FilesSkippedHidden,
    long FilesSkippedExcluded,
    long FilesSkippedBinary,
    long MissingPaths,
    long Errors,
    bool LimitReached,
    TimeSpan Duration,
    SearchRuntimeProfile RuntimeProfile);
