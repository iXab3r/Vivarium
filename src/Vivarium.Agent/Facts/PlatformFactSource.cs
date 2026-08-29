using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Vivarium.Agent.Facts;

public sealed record PlatformFactReadResult(string? Value, PlatformFactIssue? Issue = null)
{
    public static PlatformFactReadResult Available(string value) => new(value);

    public static PlatformFactReadResult Unavailable(
        string code,
        string field,
        string? nativeCode = null,
        string? message = null) =>
        new(null, new PlatformFactIssue(code, field, nativeCode, message));
}

public interface IPlatformFactSource
{
    PlatformFamily Family { get; }

    Architecture OsArchitecture { get; }

    Architecture ProcessArchitecture { get; }

    Version OperatingSystemVersion { get; }

    string Hostname { get; }

    ValueTask<PlatformFactReadResult> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    ValueTask<PlatformFactReadResult> ReadWindowsRegistryValueAsync(
        string keyPath,
        string valueName,
        CancellationToken cancellationToken = default);

    ValueTask<PlatformFactReadResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class SystemPlatformFactSource : IPlatformFactSource
{
    private const int MaxFileChars = 64 * 1024;
    private const int MaxCommandChars = 4 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    public PlatformFamily Family =>
        OperatingSystem.IsWindows() ? PlatformFamily.Windows :
        OperatingSystem.IsLinux() ? PlatformFamily.Linux :
        OperatingSystem.IsMacOS() ? PlatformFamily.MacOS :
        PlatformFamily.Unknown;

    public Architecture OsArchitecture => RuntimeInformation.OSArchitecture;

    public Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    public Version OperatingSystemVersion => Environment.OSVersion.Version;

    public string Hostname => Environment.MachineName;

    public async ValueTask<PlatformFactReadResult> ReadTextFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var read = await ReadBoundedAsync(reader, MaxFileChars, cancellationToken);
            return read.Truncated
                ? PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.ResourceExhausted,
                    path,
                    message: "The fact source exceeded its read limit.")
                : PlatformFactReadResult.Available(read.Value);
        }
        catch (FileNotFoundException)
        {
            return PlatformFactReadResult.Unavailable(PlatformFactIssueCodes.NotFound, path);
        }
        catch (DirectoryNotFoundException)
        {
            return PlatformFactReadResult.Unavailable(PlatformFactIssueCodes.NotFound, path);
        }
        catch (UnauthorizedAccessException)
        {
            return PlatformFactReadResult.Unavailable(PlatformFactIssueCodes.AccessDenied, path);
        }
        catch (IOException ex)
        {
            return PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.NativeFailure,
                path,
                ex.HResult.ToString(CultureInfo.InvariantCulture),
                "The fact source could not be read.");
        }
    }

    public ValueTask<PlatformFactReadResult> ReadWindowsRegistryValueAsync(
        string keyPath,
        string valueName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.NotSupported,
                $"registry:{valueName}"));
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
            var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return ValueTask.FromResult(value is null
                ? PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.NotFound,
                    $"registry:{valueName}")
                : PlatformFactReadResult.Available(Convert.ToString(value, CultureInfo.InvariantCulture)!));
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.AccessDenied,
                $"registry:{valueName}"));
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
        {
            return ValueTask.FromResult(PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.NativeFailure,
                $"registry:{valueName}",
                ex.HResult.ToString(CultureInfo.InvariantCulture),
                "The registry fact could not be read."));
        }
    }

    public async ValueTask<PlatformFactReadResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.NativeFailure,
                    executable,
                    message: "The fact command did not start.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            var stdout = ReadBoundedAsync(process.StandardOutput, MaxCommandChars, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, MaxCommandChars, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.TemporarilyUnavailable,
                    executable,
                    message: "The fact command exceeded its time limit.");
            }

            var output = await stdout;
            var error = await stderr;
            if (output.Truncated || error.Truncated)
            {
                return PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.ResourceExhausted,
                    executable,
                    message: "The fact command exceeded its output limit.");
            }

            if (process.ExitCode != 0)
            {
                return PlatformFactReadResult.Unavailable(
                    PlatformFactIssueCodes.NativeFailure,
                    executable,
                    process.ExitCode.ToString(CultureInfo.InvariantCulture),
                    "The fact command returned a nonzero exit code.");
            }

            return PlatformFactReadResult.Available(output.Value.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Win32Exception ex)
        {
            return PlatformFactReadResult.Unavailable(
                ex.NativeErrorCode is 5 or 13
                    ? PlatformFactIssueCodes.AccessDenied
                    : PlatformFactIssueCodes.NativeFailure,
                executable,
                ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture),
                "The fact command could not be executed.");
        }
        catch (InvalidOperationException)
        {
            return PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.NativeFailure,
                executable,
                message: "The fact command could not be executed.");
        }
        catch (IOException ex)
        {
            return PlatformFactReadResult.Unavailable(
                PlatformFactIssueCodes.NativeFailure,
                executable,
                ex.HResult.ToString(CultureInfo.InvariantCulture),
                "The fact command could not be read.");
        }
    }

    private static async Task<(string Value, bool Truncated)> ReadBoundedAsync(
        TextReader reader,
        int maximumChars,
        CancellationToken cancellationToken)
    {
        var buffer = new char[maximumChars + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var truncated = total > maximumChars;
        return (new string(buffer, 0, Math.Min(total, maximumChars)), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
