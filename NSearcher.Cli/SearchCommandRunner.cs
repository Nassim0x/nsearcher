using NSearcher.Core;

namespace NSearcher.Cli;

internal static class SearchCommandRunner
{
    public static async Task<int> RunAsync(
        SearchOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var renderer = new ConsoleRenderer(options.UseColor, stdout, stderr);
        var engine = new SearchEngine();
        var batchSummaryOutput =
            options.BeforeContext == 0 &&
            options.AfterContext == 0 &&
            (options.CountOnly || options.FilesWithMatches);
        var deferredResults = batchSummaryOutput ? new List<SearchFileResult>() : null;
        var summary = await engine.ExecuteAsync(
            options,
            batchSummaryOutput
                ? (result, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    deferredResults!.Add(result);
                    return ValueTask.CompletedTask;
                }
                : (result, token) => renderer.WriteResultAsync(result, options, token),
            cancellationToken);

        if (deferredResults is not null && deferredResults.Count > 0)
        {
            renderer.WriteResults(deferredResults, options);
        }

        if (options.ShowStats)
        {
            renderer.WriteStats(summary);
        }

        if (summary.Errors > 0)
        {
            return 2;
        }

        return summary.FilesMatched > 0 ? 0 : 1;
    }
}
