using NSearcher.Core;

namespace NSearcher.Cli;

internal sealed record CliParseResult(SearchOptions? Options, bool ShowHelp, string? ErrorMessage);

internal static class CliParser
{
    public static string HelpText =>
        """
        NSearcher - ultra-fast recursive keyword search

        Usage:
          nsearcher <pattern> [path ...] [options]

        Examples:
          nsearcher invoice C:\Docs
          nsearcher "error \d+" . --regex
          nsearcher auth src tests -g "*.cs" --exclude "*Generated*"
          nsearcher TODO . -C 2 --stats

        Options:
          --regex, -e              Treat the pattern as a regular expression
          -i, --ignore-case        Force case-insensitive search
          -s, --case-sensitive     Force case-sensitive search
          -S, --smart-case         Ignore case only when the pattern is lowercase (default)
          -g, --glob <pattern>     Include only files matching a glob
          --exclude <pattern>      Exclude files or folders matching a glob
          -C, --context <n>        Show n lines before and after matches
          -A, --after-context <n>  Show n lines after matches
          -B, --before-context <n> Show n lines before matches
          -l, --files-with-matches Print only matching file paths
          -c, --count              Print the number of matching lines per file
          -j, --threads <n>        Number of worker threads
          -m, --max-results <n>    Stop after n matching lines
          --hidden                 Include hidden files and folders
          --binary                 Include binary files
          --no-default-excludes    Search inside .git, bin, obj, node_modules, ...
          --no-color               Disable ANSI colors
          --stats                  Print a performance summary to stderr
          -h, --help               Show this help
        """;

    public static CliParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliParseResult(null, true, null);
        }

        var positionals = new List<string>();
        var includeGlobs = new List<string>();
        var excludeGlobs = new List<string>();
        var caseMode = CaseMode.Smart;
        var useRegex = false;
        var beforeContext = 0;
        var afterContext = 0;
        var filesWithMatches = false;
        var countOnly = false;
        var includeHidden = false;
        var includeBinary = false;
        var useDefaultExcludes = true;
        var showStats = false;
        var noColor = false;
        int? maxResults = null;
        int? threadCount = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            switch (argument)
            {
                case "-h":
                case "--help":
                    return new CliParseResult(null, true, null);

                case "--regex":
                case "-e":
                    useRegex = true;
                    break;

                case "-i":
                case "--ignore-case":
                    caseMode = CaseMode.Insensitive;
                    break;

                case "-s":
                case "--case-sensitive":
                    caseMode = CaseMode.Sensitive;
                    break;

                case "-S":
                case "--smart-case":
                    caseMode = CaseMode.Smart;
                    break;

                case "-g":
                case "--glob":
                    if (!TryReadValue(args, ref index, out var includeGlob))
                    {
                        return Error("Missing value for --glob.");
                    }

                    includeGlobs.Add(includeGlob);
                    break;

                case "--exclude":
                    if (!TryReadValue(args, ref index, out var excludeGlob))
                    {
                        return Error("Missing value for --exclude.");
                    }

                    excludeGlobs.Add(excludeGlob);
                    break;

                case "-C":
                case "--context":
                    if (!TryReadInteger(args, ref index, out var context))
                    {
                        return Error("Missing or invalid value for --context.");
                    }

                    beforeContext = context;
                    afterContext = context;
                    break;

                case "-A":
                case "--after-context":
                    if (!TryReadInteger(args, ref index, out afterContext))
                    {
                        return Error("Missing or invalid value for --after-context.");
                    }

                    break;

                case "-B":
                case "--before-context":
                    if (!TryReadInteger(args, ref index, out beforeContext))
                    {
                        return Error("Missing or invalid value for --before-context.");
                    }

                    break;

                case "-l":
                case "--files-with-matches":
                    filesWithMatches = true;
                    break;

                case "-c":
                case "--count":
                    countOnly = true;
                    break;

                case "--hidden":
                    includeHidden = true;
                    break;

                case "--binary":
                    includeBinary = true;
                    break;

                case "--no-default-excludes":
                    useDefaultExcludes = false;
                    break;

                case "--stats":
                    showStats = true;
                    break;

                case "--no-color":
                    noColor = true;
                    break;

                case "-j":
                case "--threads":
                    if (!TryReadInteger(args, ref index, out var parsedThreadCount))
                    {
                        return Error("Missing or invalid value for --threads.");
                    }

                    threadCount = parsedThreadCount;
                    break;

                case "-m":
                case "--max-results":
                    if (!TryReadInteger(args, ref index, out var parsedMaxResults))
                    {
                        return Error("Missing or invalid value for --max-results.");
                    }

                    maxResults = parsedMaxResults;
                    break;

                case "--":
                    for (index++; index < args.Length; index++)
                    {
                        positionals.Add(args[index]);
                    }
                    break;

                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        return Error($"Unknown option '{argument}'.");
                    }

                    positionals.Add(argument);
                    break;
            }
        }

        if (positionals.Count == 0)
        {
            return Error("A search pattern is required.");
        }

        var pattern = positionals[0];
        var paths = positionals.Count > 1
            ? positionals.Skip(1).ToArray()
            : [Directory.GetCurrentDirectory()];

        try
        {
            var options = new SearchOptions
            {
                Pattern = pattern,
                Paths = paths,
                UseRegex = useRegex,
                CaseMode = caseMode,
                BeforeContext = beforeContext,
                AfterContext = afterContext,
                FilesWithMatches = filesWithMatches,
                CountOnly = countOnly,
                IncludeHidden = includeHidden,
                IncludeBinary = includeBinary,
                UseDefaultExcludes = useDefaultExcludes,
                ShowStats = showStats,
                UseColor = !Console.IsOutputRedirected && !noColor,
                IncludeGlobs = includeGlobs,
                ExcludeGlobs = excludeGlobs,
                MaxDegreeOfParallelism = threadCount ?? 0,
                MaxResults = maxResults
            };

            options.Validate();
            return new CliParseResult(options, false, null);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return Error(exception.Message);
        }
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInteger(string[] args, ref int index, out int value)
    {
        if (TryReadValue(args, ref index, out var rawValue) &&
            int.TryParse(rawValue, out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static CliParseResult Error(string message) =>
        new(null, false, message);
}
