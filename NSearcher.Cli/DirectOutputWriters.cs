using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NSearcher.Cli;

[Flags]
internal enum DirectOutputFlags : byte
{
    None = 0,
    Stdout = 1,
    Stderr = 2
}

internal sealed class DirectOutputWriters : IDisposable
{
    private const uint ProcessDuplicateHandleAccess = 0x0040;
    private const uint DuplicateSameAccess = 0x00000002;

    private readonly List<IDisposable> _resources;

    private DirectOutputWriters(TextWriter? stdout, TextWriter? stderr, List<IDisposable> resources)
    {
        Stdout = stdout;
        Stderr = stderr;
        _resources = resources;
    }

    public TextWriter? Stdout { get; }

    public TextWriter? Stderr { get; }

    public static DirectOutputWriters? TryCreate(DaemonRequest request)
    {
        if (!OperatingSystem.IsWindows() ||
            request.ClientProcessId <= 0 ||
            request.DirectOutput == DirectOutputFlags.None)
        {
            return null;
        }

        var sourceProcess = NativeMethods.OpenProcess(ProcessDuplicateHandleAccess, false, request.ClientProcessId);
        if (sourceProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var resources = new List<IDisposable>(capacity: 2);
            var stdout = (request.DirectOutput & DirectOutputFlags.Stdout) != 0
                ? TryCreateWriter(sourceProcess, request.StdoutHandle, resources)
                : null;
            var stderr = (request.DirectOutput & DirectOutputFlags.Stderr) != 0
                ? TryCreateWriter(sourceProcess, request.StderrHandle, resources)
                : null;

            if (stdout is null && stderr is null)
            {
                DisposeResources(resources);
                return null;
            }

            return new DirectOutputWriters(stdout, stderr, resources);
        }
        finally
        {
            NativeMethods.CloseHandle(sourceProcess);
        }
    }

    public void Dispose()
    {
        DisposeResources(_resources);
    }

    private static TextWriter? TryCreateWriter(IntPtr sourceProcess, long remoteHandleValue, List<IDisposable> resources)
    {
        if (remoteHandleValue == 0)
        {
            return null;
        }

        if (!NativeMethods.DuplicateHandle(
                sourceProcess,
                new IntPtr(remoteHandleValue),
                NativeMethods.GetCurrentProcess(),
                out var duplicatedHandle,
                0,
                false,
                DuplicateSameAccess))
        {
            return null;
        }

        try
        {
            var safeHandle = new SafeFileHandle(duplicatedHandle, ownsHandle: true);
            var stream = new FileStream(safeHandle, FileAccess.Write, bufferSize: 64 * 1024, isAsync: false);
            var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: false);
            resources.Add(writer);
            return writer;
        }
        catch
        {
            NativeMethods.CloseHandle(duplicatedHandle);
            return null;
        }
    }

    private static void DisposeResources(List<IDisposable> resources)
    {
        for (var index = resources.Count - 1; index >= 0; index--)
        {
            resources[index].Dispose();
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            IntPtr sourceHandle,
            IntPtr targetProcessHandle,
            out IntPtr targetHandle,
            uint desiredAccess,
            bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();
    }
}
