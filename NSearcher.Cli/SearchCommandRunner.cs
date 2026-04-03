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
        var summary = await engine.ExecuteAsync(
            options,
            (result, token) => renderer.WriteResultAsync(result, options, token),
            cancellationToken);

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
