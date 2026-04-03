using NSearcher.Cli;

if (LocalDaemon.TryGetInternalPipeName(args, out var pipeName))
{
    return await LocalDaemon.RunServerAsync(pipeName);
}

using var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    return await CliApplication.RunAsync(
        args,
        Console.Out,
        Console.Error,
        cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
