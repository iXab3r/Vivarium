using System.Diagnostics;

namespace Vivarium.Controller.Catalog;

internal sealed record DockerCommandResult(int ExitCode, byte[] Output, byte[] Error);

// Only coordinator-owned Docker management arguments reach this process.
internal static class DockerCommand
{
    internal static Process Start(IEnumerable<string> arguments, bool input = false)
    {
        var info = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info) ?? throw new IOException("Docker did not start."); }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("Docker is unavailable."); }
    }

    internal static async Task<DockerCommandResult> RunAsync(IEnumerable<string> arguments,
        bool allowFailure = false, CancellationToken token = default)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Start(arguments);
        async Task<byte[]> ReadAsync(Stream stream)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, timer.Token)) != 0)
            {
                if (bytes.Length + count > 1024 * 1024)
                {
                    await timer.CancelAsync();
                    throw new IOException("Docker management output exceeded its bound.");
                }
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }
        var output = ReadAsync(process.StandardOutput.BaseStream);
        var error = ReadAsync(process.StandardError.BaseStream);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timer.Token), output, error);
            var result = new DockerCommandResult(process.ExitCode, await output, await error);
            if (!allowFailure && result.ExitCode != 0) throw new IOException("Docker management command failed.");
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            // Observe both reads even when cancellation terminated the management process.
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (timer.IsCancellationRequested) { }
        }
    }
}
