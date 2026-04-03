using System.Text;
using NSearcher.Core;

namespace NSearcher.Cli;

internal sealed class ConsoleRenderer
{
    private const string Reset = "\u001b[0m";
    private const string BoldRed = "\u001b[1;31m";
    private const string Green = "\u001b[32m";
    private const string Cyan = "\u001b[36m";

    private readonly object _sync = new();
    private readonly bool _useColor;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    public ConsoleRenderer(bool useColor, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        _useColor = useColor;
        _stdout = stdout ?? Console.Out;
        _stderr = stderr ?? Console.Error;
    }

    public ValueTask WriteResultAsync(SearchFileResult result, SearchOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var outputBuilder = new StringBuilder(capacity: 256);
            var errorBuilder = new StringBuilder(capacity: 128);
            AppendResult(outputBuilder, errorBuilder, result, options);
            WriteBufferedOutput(outputBuilder, errorBuilder);
        }

        return ValueTask.CompletedTask;
    }

    public void WriteResults(IReadOnlyList<SearchFileResult> results, SearchOptions options)
    {
        lock (_sync)
        {
            if (results.Count == 0)
            {
                return;
            }

            var outputBuilder = new StringBuilder(results.Count * 64);
            var errorBuilder = new StringBuilder(256);
            foreach (var result in results)
            {
                AppendResult(outputBuilder, errorBuilder, result, options);
            }

            WriteBufferedOutput(outputBuilder, errorBuilder);
        }
    }

    public void WriteStats(SearchSummary summary)
    {
        lock (_sync)
        {
            _stderr.WriteLine(
                $"searched {summary.FilesSearched}/{summary.FilesEnumerated} files, " +
                $"matched {summary.FilesMatched} files, " +
                $"{summary.MatchLines} matching lines, " +
                $"{summary.MatchCount} total hits, " +
                $"skipped {summary.FilesSkippedBinary} binary, " +
                $"{summary.FilesSkippedExcluded} excluded, " +
                $"{summary.FilesSkippedHidden} hidden, " +
                $"{summary.Errors} errors in {summary.Duration.TotalMilliseconds:F1} ms");

            _stderr.WriteLine(
                $"runtime: {summary.RuntimeProfile.WorkerCount} workers, " +
                $"{summary.RuntimeProfile.StreamBufferSize / 1024} KiB buffers, " +
                $"{summary.RuntimeProfile.TotalAvailableMemoryBytes / (1024 * 1024)} MiB available memory, " +
                $"fast-literal-mode={(summary.RuntimeProfile.UsedFastLiteralFileMode ? "on" : "off")}, " +
                $"auto-tuned={(summary.RuntimeProfile.AutoTuned ? "yes" : "no")}");

            if (summary.MissingPaths > 0)
            {
                _stderr.WriteLine($"missing paths: {summary.MissingPaths}");
            }

            if (summary.LimitReached)
            {
                _stderr.WriteLine("result limit reached");
            }
        }
    }

    private string Highlight(string line, IReadOnlyList<MatchOccurrence>? occurrences)
    {
        if (!_useColor || occurrences is null || occurrences.Count == 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + (occurrences.Count * 12));
        var cursor = 0;

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Length <= 0 || occurrence.Start < cursor || occurrence.Start >= line.Length)
            {
                continue;
            }

            builder.Append(line.AsSpan(cursor, occurrence.Start - cursor));
            builder.Append(BoldRed);
            builder.Append(line.AsSpan(occurrence.Start, occurrence.Length));
            builder.Append(Reset);
            cursor = occurrence.Start + occurrence.Length;
        }

        builder.Append(line.AsSpan(cursor));
        return builder.ToString();
    }

    private string FormatPath(string path) => _useColor ? $"{Green}{path}{Reset}" : path;

    private string FormatLineNumber(int value) => _useColor ? $"{Cyan}{value}{Reset}" : value.ToString();

    private void AppendResult(
        StringBuilder outputBuilder,
        StringBuilder errorBuilder,
        SearchFileResult result,
        SearchOptions options)
    {
        if (result.HasError)
        {
            errorBuilder.Append("nsearcher: ");
            errorBuilder.Append(result.FilePath);
            errorBuilder.Append(": ");
            errorBuilder.Append(result.ErrorMessage);
            errorBuilder.AppendLine();
            return;
        }

        if (options.FilesWithMatches)
        {
            outputBuilder.AppendLine(result.FilePath);
            return;
        }

        if (options.CountOnly)
        {
            outputBuilder.Append(FormatPath(result.FilePath));
            outputBuilder.Append(':');
            outputBuilder.Append(result.MatchLineCount);
            outputBuilder.AppendLine();
            return;
        }

        var previousLineNumber = -1;

        foreach (var line in result.OutputLines)
        {
            if (previousLineNumber >= 0 && line.LineNumber > previousLineNumber + 1)
            {
                outputBuilder.AppendLine("--");
            }

            if (line.Kind == SearchOutputLineKind.Match)
            {
                var firstColumn = line.FirstMatchStart >= 0 ? line.FirstMatchStart + 1 : 1;
                outputBuilder.Append(FormatPath(result.FilePath));
                outputBuilder.Append(':');
                outputBuilder.Append(FormatLineNumber(line.LineNumber));
                outputBuilder.Append(':');
                outputBuilder.Append(FormatLineNumber(firstColumn));
                outputBuilder.Append(':');
                outputBuilder.Append(Highlight(line.Text, line.Occurrences));
                outputBuilder.AppendLine();
            }
            else
            {
                outputBuilder.Append(FormatPath(result.FilePath));
                outputBuilder.Append('-');
                outputBuilder.Append(FormatLineNumber(line.LineNumber));
                outputBuilder.Append('-');
                outputBuilder.Append(line.Text);
                outputBuilder.AppendLine();
            }

            previousLineNumber = line.LineNumber;
        }
    }

    private void WriteBufferedOutput(StringBuilder outputBuilder, StringBuilder errorBuilder)
    {
        if (outputBuilder.Length == 0)
        {
            if (errorBuilder.Length > 0)
            {
                _stderr.Write(errorBuilder.ToString());
            }

            return;
        }

        _stdout.Write(outputBuilder.ToString());

        if (errorBuilder.Length > 0)
        {
            _stderr.Write(errorBuilder.ToString());
        }
    }
}
