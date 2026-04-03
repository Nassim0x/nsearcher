using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace NSearcher.Cli;

internal static class LocalDaemon
{
    private const string InternalDaemonArgument = "--internal-daemon";

    public static bool TryGetInternalPipeName(string[] args, out string pipeName)
    {
        pipeName = string.Empty;
        if (args.Length == 2 && string.Equals(args[0], InternalDaemonArgument, StringComparison.Ordinal))
        {
            pipeName = args[1];
            return true;
        }

        return false;
    }

    public static async Task<DaemonResponse?> TryExecuteAsync(
        string[] args,
        string workingDirectory,
        bool disableColor,
        CancellationToken cancellationToken)
    {
        var pipeName = CreatePipeName();
        var request = new DaemonRequest(args, workingDirectory, disableColor);

        if (await TryExchangeAsync(pipeName, request, cancellationToken) is { } cachedResponse)
        {
            return cachedResponse;
        }

        if (!await TryStartDaemonAsync(pipeName, cancellationToken))
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

    public static async Task<int> RunServerAsync(string pipeName)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        while (true)
        {
            using var idleTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            try
            {
                await server.WaitForConnectionAsync(idleTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            DaemonResponse response;

            try
            {
                var request = await ReadRequestAsync(server, CancellationToken.None);
                response = await ExecuteRequestAsync(request);
            }
            catch (Exception exception)
            {
                response = new DaemonResponse(2, string.Empty, exception.Message + Environment.NewLine);
            }

            try
            {
                await WriteResponseAsync(server, response, CancellationToken.None);
                await server.FlushAsync(CancellationToken.None);
            }
            catch (IOException)
            {
            }
            finally
            {
                if (server.IsConnected)
                {
                    try
                    {
                        server.Disconnect();
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }

    private static async Task<DaemonResponse> ExecuteRequestAsync(DaemonRequest request)
    {
        var previousDirectory = Directory.GetCurrentDirectory();
        using var directOutput = DirectOutputWriters.TryCreate(request);
        using var capturedStdout = directOutput?.Stdout is null ? new StringWriter() : null;
        using var capturedStderr = directOutput?.Stderr is null ? new StringWriter() : null;
        var stdout = directOutput?.Stdout ?? capturedStdout!;
        var stderr = directOutput?.Stderr ?? capturedStderr!;

        try
        {
            Directory.SetCurrentDirectory(request.WorkingDirectory);
            var forwardedArgs = PrepareForwardedArguments(request.Arguments, request.DisableColor);
            var exitCode = await CliApplication.RunAsync(forwardedArgs, stdout, stderr, CancellationToken.None);
            await stdout.FlushAsync();
            await stderr.FlushAsync();
            return new DaemonResponse(
                exitCode,
                capturedStdout?.ToString() ?? string.Empty,
                capturedStderr?.ToString() ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            await stdout.FlushAsync();
            await stderr.FlushAsync();
            return new DaemonResponse(
                130,
                capturedStdout?.ToString() ?? string.Empty,
                capturedStderr?.ToString() ?? string.Empty);
        }
        catch (Exception exception)
        {
            stderr.WriteLine(exception.Message);
            await stdout.FlushAsync();
            await stderr.FlushAsync();
            return new DaemonResponse(
                2,
                capturedStdout?.ToString() ?? string.Empty,
                capturedStderr?.ToString() ?? string.Empty);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    private static async Task<bool> TryStartDaemonAsync(string pipeName, CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(processPath) ?? Directory.GetCurrentDirectory(),
                ArgumentList = { InternalDaemonArgument, pipeName }
            });

            await Task.Delay(2, cancellationToken);
            return process is not null;
        }
        catch
        {
            return false;
        }
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

    private static async Task<DaemonRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken);
        using var buffer = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: false);
        var workingDirectory = reader.ReadString();
        var disableColor = reader.ReadBoolean();
        var arguments = ReadStringList(reader);
        var directOutput = DirectOutputFlags.None;
        var clientProcessId = 0;
        long stdoutHandle = 0;
        long stderrHandle = 0;

        if (buffer.Position < buffer.Length)
        {
            directOutput = (DirectOutputFlags)reader.ReadByte();
            clientProcessId = reader.ReadInt32();
            stdoutHandle = reader.ReadInt64();
            stderrHandle = reader.ReadInt64();
        }

        return new DaemonRequest(
            arguments,
            workingDirectory,
            disableColor,
            directOutput,
            clientProcessId,
            stdoutHandle,
            stderrHandle);
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        DaemonResponse response,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(response.ExitCode);
            writer.Write(response.StandardOutput);
            writer.Write(response.StandardError);
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

    private static string[] PrepareForwardedArguments(string[] args, bool disableColor)
    {
        if (!disableColor || args.Any(argument => string.Equals(argument, "--no-color", StringComparison.Ordinal)))
        {
            return args;
        }

        var forwardedArgs = new string[args.Length + 1];
        Array.Copy(args, forwardedArgs, args.Length);
        forwardedArgs[^1] = "--no-color";
        return forwardedArgs;
    }

    private static void WriteStringList(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static string[] ReadStringList(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var values = new string[count];

        for (var index = 0; index < count; index++)
        {
            values[index] = reader.ReadString();
        }

        return values;
    }
}

internal sealed record DaemonRequest(
    string[] Arguments,
    string WorkingDirectory,
    bool DisableColor,
    DirectOutputFlags DirectOutput = DirectOutputFlags.None,
    int ClientProcessId = 0,
    long StdoutHandle = 0,
    long StderrHandle = 0);

internal sealed record DaemonResponse(int ExitCode, string StandardOutput, string StandardError);
