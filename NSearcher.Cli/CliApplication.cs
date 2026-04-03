using NSearcher.Core;

namespace NSearcher.Cli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parseResult = CliParser.Parse(args);

        if (parseResult.ShowHelp)
        {
            await stdout.WriteLineAsync(CliParser.HelpText);
            return 0;
        }

        if (parseResult.ErrorMessage is not null)
        {
            await stderr.WriteLineAsync(parseResult.ErrorMessage);
            await stderr.WriteLineAsync();
            await stderr.WriteLineAsync(CliParser.HelpText);
            return 2;
        }

        return await SearchCommandRunner.RunAsync(
            parseResult.Options!,
            stdout,
            stderr,
            cancellationToken);
    }
}
