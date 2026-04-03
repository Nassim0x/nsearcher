using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace NSearcher.Installer;

[SupportedOSPlatform("windows")]
internal static partial class Program
{
    private const string PayloadResourceName = "NSearcher.Installer.Payload.nsearcher-payload.zip";
    private const string ProductName = "NSearcher";
    private const string SetupDisplayName = "NSearcher Setup";
    private const string Publisher = "Nassim0x";
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NSearcher";
    private const string SupportScriptName = "install-support.ps1";
    private const string UninstallScriptName = "uninstall.ps1";
    private const string UninstallCommandName = "uninstall.cmd";

    public static int Main(string[] args)
    {
        try
        {
            var options = InstallerOptions.Parse(args);
            if (options.ShowHelp)
            {
                WriteHelp();
                return 0;
            }

            Install(options);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{SetupDisplayName} failed: {ex.Message}");
            return 1;
        }
    }

    private static void Install(InstallerOptions options)
    {
        var installDir = string.IsNullOrWhiteSpace(options.InstallDir)
            ? GetDefaultInstallDirectory()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.InstallDir));

        WriteStatus(options, $"Installing {ProductName} into {installDir}");

        var tempRoot = Path.Combine(Path.GetTempPath(), "nsearcher-setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            ExtractPayload(tempRoot);
            ValidateExtractedPayload(tempRoot);

            Directory.CreateDirectory(installDir);
            StopInstalledProcesses(installDir);
            DeleteDirectoryContents(installDir);
            CopyDirectory(tempRoot, installDir);
            WriteSupportScripts(installDir);
            AddDirectoryToUserPath(installDir);
            RegisterUninstallInformation(installDir);
            BroadcastEnvironmentChange();

            WriteStatus(options, $"{ProductName} installation completed successfully.");
            WriteStatus(options, "Open a new terminal, then run:");
            WriteStatus(options, "  NSearcher --help");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ExtractPayload(string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("Installer payload is missing.");
        using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(destinationPath, destinationRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Installer payload contains an invalid path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            using var sourceStream = entry.Open();
            using var destinationStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            sourceStream.CopyTo(destinationStream);
        }
    }

    private static void ValidateExtractedPayload(string payloadDirectory)
    {
        var requiredFiles = new[]
        {
            Path.Combine(payloadDirectory, "NSearcher.exe"),
            Path.Combine(payloadDirectory, "NSearcher.Server.exe")
        };

        foreach (var requiredFile in requiredFiles)
        {
            if (!File.Exists(requiredFile))
            {
                throw new InvalidOperationException($"Installer payload is missing required file '{Path.GetFileName(requiredFile)}'.");
            }
        }
    }

    private static void DeleteDirectoryContents(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            TryDeletePath(entry);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var parentDirectory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void WriteSupportScripts(string installDirectory)
    {
        File.WriteAllText(Path.Combine(installDirectory, SupportScriptName), BuildInstallSupportScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(installDirectory, UninstallScriptName), BuildUninstallScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(installDirectory, UninstallCommandName), "@echo off\r\npowershell -ExecutionPolicy Bypass -File \"%~dp0uninstall.ps1\" %*\r\n", Encoding.ASCII);
    }

    private static string BuildInstallSupportScript() =>
        """
        function Remove-DirectoryFromUserPath {
            param([string]$Directory)

            $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
            if ([string]::IsNullOrWhiteSpace($currentUserPath)) {
                return
            }

            $updatedEntries = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries) |
                Where-Object { $_.TrimEnd('\') -ine $Directory.TrimEnd('\') }

            [Environment]::SetEnvironmentVariable('Path', (($updatedEntries -join ';').Trim(';')), 'User')
        }

        function Stop-InstalledProcesses {
            param([string]$Directory)

            Get-Process -ErrorAction SilentlyContinue |
                Where-Object {
                    try {
                        $_.Path -and $_.Path.StartsWith($Directory, [System.StringComparison]::OrdinalIgnoreCase)
                    }
                    catch {
                        $false
                    }
                } |
                Stop-Process -Force -ErrorAction SilentlyContinue
        }
        """;

    private static string BuildUninstallScript() =>
        """
        param(
            [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NSearcher"
        )

        $ErrorActionPreference = 'Stop'
        . (Join-Path $PSScriptRoot 'install-support.ps1')

        Stop-InstalledProcesses -Directory $InstallDir
        Remove-DirectoryFromUserPath -Directory $InstallDir

        $registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NSearcher'
        if (Test-Path $registryPath) {
            Remove-Item -Path $registryPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path $registryPath) {
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & reg.exe delete 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\NSearcher' /f > $null 2>&1
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
        }

        if (Test-Path $InstallDir) {
            $escapedInstallDir = '"' + $InstallDir + '"'
            Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "ping 127.0.0.1 -n 2 > nul && rmdir /s /q $escapedInstallDir" -WindowStyle Hidden
        }
        """;

    private static void AddDirectoryToUserPath(string directory)
    {
        var currentUserPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = currentUserPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!entries.Any(entry => PathEquals(entry, directory)))
        {
            entries.Add(directory);
            var updatedPath = string.Join(';', entries.Where(entry => !string.IsNullOrWhiteSpace(entry)));
            Environment.SetEnvironmentVariable("Path", updatedPath, EnvironmentVariableTarget.User);
        }
    }

    private static void RegisterUninstallInformation(string installDirectory)
    {
        var uninstallScriptPath = Path.Combine(installDirectory, UninstallScriptName);
        var displayIconPath = Path.Combine(installDirectory, "NSearcher.exe");
        var displayVersion = GetDisplayVersion();
        var uninstallCommand = $"powershell.exe -ExecutionPolicy Bypass -File \"{uninstallScriptPath}\"";
        var quietUninstallCommand = uninstallCommand;

        using var uninstallKey = Registry.CurrentUser.CreateSubKey(UninstallRegistryKey, writable: true)
            ?? throw new InvalidOperationException("Unable to create the Windows uninstall registry entry.");

        uninstallKey.SetValue("DisplayName", ProductName, RegistryValueKind.String);
        uninstallKey.SetValue("DisplayVersion", displayVersion, RegistryValueKind.String);
        uninstallKey.SetValue("Publisher", Publisher, RegistryValueKind.String);
        uninstallKey.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
        uninstallKey.SetValue("DisplayIcon", displayIconPath, RegistryValueKind.String);
        uninstallKey.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        uninstallKey.SetValue("QuietUninstallString", quietUninstallCommand, RegistryValueKind.String);
        uninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
        uninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        uninstallKey.SetValue("EstimatedSize", CalculateEstimatedSizeInKilobytes(installDirectory), RegistryValueKind.DWord);
    }

    private static int CalculateEstimatedSizeInKilobytes(string installDirectory)
    {
        long totalBytes = 0;
        foreach (var file in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
        {
            totalBytes += new FileInfo(file).Length;
        }

        return (int)Math.Clamp(totalBytes / 1024L, 0L, int.MaxValue);
    }

    private static void StopInstalledProcesses(string directory)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath) &&
                    processPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            catch
            {
                // Best effort: inaccessible or already exited processes can be ignored.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Temporary extraction directories are best-effort cleanup.
            }
        }
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to remove '{path}': {ex.Message}", ex);
        }
    }

    private static string GetDisplayVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    }

    private static string GetDefaultInstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    private static bool PathEquals(string left, string right) =>
        left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

    private static void BroadcastEnvironmentChange()
    {
        const int hwndBroadcast = 0xffff;
        const int wmSettingChange = 0x001A;
        const int smtoAbortIfHung = 0x0002;

        SendMessageTimeout(
            new IntPtr(hwndBroadcast),
            wmSettingChange,
            IntPtr.Zero,
            "Environment",
            smtoAbortIfHung,
            5_000,
            out _);
    }

    private static void WriteStatus(InstallerOptions options, string message)
    {
        if (!options.Quiet)
        {
            Console.WriteLine(message);
        }
    }

    private static void WriteHelp()
    {
        Console.WriteLine("NSearcher Setup");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  NSearcher-Setup.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  /quiet, /silent, /verysilent  Run without progress output");
        Console.WriteLine("  /dir <path>                   Install to a custom directory");
        Console.WriteLine("  --install-dir <path>          Install to a custom directory");
        Console.WriteLine("  /help, /?                     Show this help");
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    private sealed record InstallerOptions(bool Quiet, string? InstallDir, bool ShowHelp)
    {
        public static InstallerOptions Parse(string[] args)
        {
            var quiet = false;
            string? installDir = null;
            var showHelp = false;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument.ToLowerInvariant())
                {
                    case "/quiet":
                    case "/silent":
                    case "/verysilent":
                    case "/s":
                    case "--quiet":
                        quiet = true;
                        break;

                    case "/help":
                    case "/?":
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;

                    case "/dir":
                    case "--install-dir":
                        if (index + 1 >= args.Length)
                        {
                            throw new ArgumentException($"Missing value for '{argument}'.");
                        }

                        installDir = args[++index];
                        break;

                    default:
                        if (argument.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase) ||
                            argument.StartsWith("/d=", StringComparison.OrdinalIgnoreCase))
                        {
                            installDir = argument[(argument.IndexOf('=') + 1)..];
                            break;
                        }

                        throw new ArgumentException($"Unknown option '{argument}'.");
                }
            }

            return new InstallerOptions(quiet, installDir, showHelp);
        }
    }
}
