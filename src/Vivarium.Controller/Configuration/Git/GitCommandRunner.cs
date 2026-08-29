using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Vivarium.Controller.Configuration.Git;

internal sealed class GitCommandRunner(string repositoryPath)
{
    private const int MaxDiagnosticLength = 2048;
    private const int MaxStandardOutputBytes = 8 * 1024 * 1024;
    private const int MaxStandardErrorBytes = 64 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        ReadOnlyMemory<byte>? standardInput = null,
        bool allowFailure = false,
        CancellationToken cancellationToken = default)
    {
        var output = await RunBytesAsync(
            arguments,
            environment,
            standardInput,
            allowFailure,
            cancellationToken);
        return Encoding.UTF8.GetString(output).TrimEnd('\r', '\n');
    }

    public async Task<byte[]> RunBytesAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        ReadOnlyMemory<byte>? standardInput = null,
        bool allowFailure = false,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardInput = standardInput.HasValue,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ConfigurationRepositoryException(
                    "CONFIG_GIT_UNAVAILABLE",
                    "The Git process could not be started.");
            }
        }
        catch (ConfigurationRepositoryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new ConfigurationRepositoryException(
                "CONFIG_GIT_UNAVAILABLE",
                "A compatible Git executable is required for the managed configuration repository.",
                exception);
        }

        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCancellation.CancelAfter(CommandTimeout);
        var commandToken = commandCancellation.Token;
        var outputTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            MaxStandardOutputBytes,
            commandToken);
        var errorTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            MaxStandardErrorBytes,
            commandToken);

        try
        {
            if (standardInput is { } input)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, commandToken);
                    await process.StandardInput.BaseStream.FlushAsync(commandToken);
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }

            await process.WaitForExitAsync(commandToken);
            var output = await outputTask;
            var error = await errorTask;

            if (output.Exceeded || error.Exceeded)
            {
                throw new ConfigurationRepositoryException(
                    "CONFIG_GIT_OUTPUT_LIMIT",
                    "Git produced more output than the bounded repository operation permits.");
            }

            if (process.ExitCode != 0 && !allowFailure)
            {
                throw new ConfigurationRepositoryException(
                    "CONFIG_GIT_COMMAND_FAILED",
                    $"Git could not complete the repository operation: {BoundDiagnostic(error.Bytes)}");
            }

            if (process.ExitCode != 0)
            {
                return [];
            }

            return output.Bytes;
        }
        catch (OperationCanceledException) when (commandToken.IsCancellationRequested)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // The original cancellation or timeout remains the useful failure.
            }

            try
            {
                await Task.WhenAll(outputTask, errorTask);
            }
            catch
            {
                // Draining after process termination is best effort.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new ConfigurationRepositoryException(
                "CONFIG_GIT_TIMEOUT",
                "Git did not complete the repository operation within 30 seconds.");
        }
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
            // The process exited while cancellation was being handled.
        }
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(limit, 16 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var exceeded = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var remaining = limit - (int)output.Length;
                if (remaining > 0)
                {
                    output.Write(buffer, 0, Math.Min(read, remaining));
                }

                exceeded |= read > remaining;
            }

            return new BoundedOutput(output.ToArray(), exceeded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string BoundDiagnostic(ReadOnlySpan<byte> value)
    {
        var oneLine = Encoding.UTF8.GetString(value)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (oneLine.Length == 0)
        {
            return "the command returned a nonzero exit code";
        }

        return oneLine.Length <= MaxDiagnosticLength
            ? oneLine
            : oneLine[..MaxDiagnosticLength];
    }

    private sealed record BoundedOutput(byte[] Bytes, bool Exceeded);
}
