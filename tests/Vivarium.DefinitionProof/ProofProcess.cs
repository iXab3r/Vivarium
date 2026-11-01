using System.Diagnostics;
using System.Text;

namespace Vivarium.DefinitionProof;

internal sealed record ProofProcessResult(int ExitCode, byte[] Output, byte[] Error)
{
    public string Text => Encoding.UTF8.GetString(Output).TrimEnd('\r', '\n');
}

internal static class ProofProcess
{
    public static Process Start(string executable, IEnumerable<string> arguments, string? directory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory ?? Environment.CurrentDirectory,
        };
        // The worker receives this explicitly; Git and the source evaluator must not inherit it.
        info.Environment.Remove("VIVARIUM_PROOF_PG");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var entry in environment ?? new Dictionary<string, string>()) info.Environment[entry.Key] = entry.Value;
        return Process.Start(info) ?? throw new InvalidOperationException("Proof child did not start.");
    }

    public static async Task<ProofProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string? directory = null, IReadOnlyDictionary<string, string>? environment = null,
        bool allowFailure = false, TimeSpan? deadline = null)
    {
        using var child = Start(executable, arguments, directory, environment);
        return await ObserveAsync(child, allowFailure, deadline);
    }

    public static async Task<ProofProcessResult> ObserveAsync(Process child, bool allowFailure = false,
        TimeSpan? deadline = null)
    {
        using var timer = new CancellationTokenSource(deadline ?? TimeSpan.FromSeconds(40));
        async Task<byte[]> ReadAsync(Stream stream)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, timer.Token)) != 0)
            {
                if (bytes.Length + count > 2 * 1024 * 1024)
                {
                    timer.Cancel();
                    throw new InvalidDataException("Proof process output exceeded its bound.");
                }
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }
        var output = ReadAsync(child.StandardOutput.BaseStream);
        var error = ReadAsync(child.StandardError.BaseStream);
        try
        {
            await Task.WhenAll(child.WaitForExitAsync(timer.Token), output, error);
            var result = new ProofProcessResult(child.ExitCode, await output, await error);
            if (!allowFailure && result.ExitCode != 0)
                throw new InvalidOperationException($"Proof child exited {result.ExitCode}: " +
                    Encoding.UTF8.GetString(result.Error.AsSpan(0, Math.Min(2048, result.Error.Length))));
            return result;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
