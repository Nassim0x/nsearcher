using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

using var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationTokenSource.Cancel();
};

try
{
    return await ClientBridge.RunAsync(args, cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
    return 130;
}

internal static class ClientBridge
{
    private const string InternalDaemonArgument = "--internal-daemon";
    private const string ServerExecutableName = "NSearcher.Server.exe";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await RunServerDirectAsync(args, cancellationToken);
        }

        if (args.Length == 0 ||
            args.Any(argument => string.Equals(argument, "--help", StringComparison.Ordinal) ||
                                 string.Equals(argument, "-h", StringComparison.Ordinal)))
        {
            return await RunServerDirectAsync(args, cancellationToken);
        }

        var request = new DaemonRequest(args, Directory.GetCurrentDirectory(), Console.IsOutputRedirected);
        var pipeName = CreatePipeName();
        var response = await TryExchangeAsync(pipeName, request, cancellationToken);
        DaemonResponse? startedResponse = null;

        if (response is not null)
        {
            startedResponse = response;
        }
        else
        {
            startedResponse = await TryStartServerAndExchangeAsync(pipeName, request, cancellationToken);
        }

        if (startedResponse is not null)
        {
            if (!string.IsNullOrEmpty(startedResponse.StandardOutput))
            {
                Console.Out.Write(startedResponse.StandardOutput);
            }

            if (!string.IsNullOrEmpty(startedResponse.StandardError))
            {
                Console.Error.Write(startedResponse.StandardError);
            }

            return startedResponse.ExitCode;
        }

        return await RunServerDirectAsync(args, cancellationToken);
    }

    private static async Task<DaemonResponse?> TryStartServerAndExchangeAsync(
        string pipeName,
        DaemonRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryStartServer(pipeName))
        {
            return null;
        }

        for (var attempt = 0; attempt < 24; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await TryExchangeAsync(pipeName, request, cancellationToken);
            if (response is not null)
            {
                return response;
            }

            await Task.Delay(5, cancellationToken);
        }

        return null;
    }

    private static bool TryStartServer(string pipeName)
    {
        var serverPath = GetInstalledServerPath();
        if (serverPath is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = serverPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(serverPath) ?? Directory.GetCurrentDirectory(),
                ArgumentList = { InternalDaemonArgument, pipeName }
            });

            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunServerDirectAsync(string[] args, CancellationToken cancellationToken)
    {
        var serverPath = GetInstalledServerPath();
        if (serverPath is null)
        {
            Console.Error.WriteLine("Unable to locate NSearcher.Server.exe.");
            return 2;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            }
        };

        foreach (var argument in args)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        Console.Out.Write(await stdoutTask);
        Console.Error.Write(await stderrTask);
        return process.ExitCode;
    }

    private static async Task<DaemonResponse?> TryExchangeAsync(
        string pipeName,
        DaemonRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(25, cancellationToken);
            await WriteRequestAsync(client, request, cancellationToken);
            await client.FlushAsync(cancellationToken);
            return await ReadResponseAsync(client, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteRequestAsync(
        Stream stream,
        DaemonRequest request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(request.WorkingDirectory);
            writer.Write(request.DisableColor);
            WriteStringList(writer, request.Arguments);
        }

        await WriteFrameAsync(stream, buffer.ToArray(), cancellationToken);
    }

    private static async Task<DaemonResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: false);
        return new DaemonResponse(
            reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadString());
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken);
        var payloadLength = BitConverter.ToInt32(lengthBytes, 0);
        var payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            offset += bytesRead;
        }
    }

    private static void WriteStringList(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static string? GetInstalledServerPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var serverPath = Path.Combine(directory, ServerExecutableName);
        return File.Exists(serverPath) ? serverPath : null;
    }

    private static string CreatePipeName()
    {
        var builder = new StringBuilder("nsearcher-");
        foreach (var character in Environment.UserName)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        if (builder.Length == "nsearcher-".Length)
        {
            builder.Append("user");
        }

        return builder.ToString();
    }
}

internal sealed record DaemonRequest(string[] Arguments, string WorkingDirectory, bool DisableColor);

internal sealed record DaemonResponse(int ExitCode, string StandardOutput, string StandardError);
