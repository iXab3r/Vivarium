// Adapted from EyeAuras.Depot 20238384013326cddb3296963a6d101a0970efc0; consumer-owned fixture policy.
using CliWrap;
using EyeAuras.Automation;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Finite fixture command using the framework's private stdin and owned process lifetime.</summary>
public static class PrivateCommand
{
    /// <summary>Run to completion without placing private input in arguments, diagnostics or assertion messages.</summary>
    public static async Task<string> ExecuteAsync(AutomationRun run, Command command, string? input,
        CancellationToken token)
    {
        await using var process = await run.StartProcessAsync(command, token, standardInput: input);
        var status = await process.WaitForExitAsync(TimeSpan.FromSeconds(30), token);
        if (status.ExitCode != 0) throw new InvalidOperationException(
            $"Owned {Path.GetFileName(command.TargetFilePath)} command failed; inspect the private run diagnostics.");
        return (await process.ReadOutputAsync(token)).StandardOutput;
    }
}
