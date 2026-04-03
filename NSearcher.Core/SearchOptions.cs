namespace NSearcher.Core;

public enum CaseMode
{
    Smart,
    Sensitive,
    Insensitive
}

public sealed class SearchOptions
{
    public required string Pattern { get; init; }

    public IReadOnlyList<string> Paths { get; init; } = [Directory.GetCurrentDirectory()];

    public bool UseRegex { get; init; }

    public CaseMode CaseMode { get; init; } = CaseMode.Smart;

    public int BeforeContext { get; init; }

    public int AfterContext { get; init; }

    public bool FilesWithMatches { get; init; }

    public bool CountOnly { get; init; }

    public bool IncludeHidden { get; init; }

    public bool IncludeBinary { get; init; }

    public bool UseDefaultExcludes { get; init; } = true;

    public int MaxDegreeOfParallelism { get; init; }

    public int? MaxResults { get; init; }

    public bool ShowStats { get; init; }

    public bool UseColor { get; init; } = true;

    public bool ReportErrors { get; init; } = true;

    public IReadOnlyList<string> IncludeGlobs { get; init; } = [];

    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            throw new ArgumentException("The search pattern cannot be empty.", nameof(Pattern));
        }

        if (Paths.Count == 0)
        {
            throw new ArgumentException("At least one file or directory must be provided.", nameof(Paths));
        }

        if (BeforeContext < 0 || AfterContext < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BeforeContext), "Context values must be positive or zero.");
        }

        if (MaxDegreeOfParallelism < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), "Thread count must be zero (auto) or greater than zero.");
        }

        if (MaxResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResults), "Maximum results must be greater than zero.");
        }

        if (CountOnly && FilesWithMatches)
        {
            throw new ArgumentException("Cannot combine count-only output with files-with-matches mode.");
        }
    }
}
