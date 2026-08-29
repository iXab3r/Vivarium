using System.Diagnostics;
using System.Text;

internal static class BuildProcess
{
    public static async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutSeconds = 1800,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var args = arguments.ToArray();
        Console.WriteLine($"> {fileName} {string.Join(" ", args.Select(DisplayArgument))}");

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = PumpAsync(process.StandardOutput, Console.Out);
        var stderr = PumpAsync(process.StandardError, Console.Error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdout, stderr);
            throw new TimeoutException($"{fileName} exceeded the {timeoutSeconds}-second timeout.");
        }

        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
        }
    }

    public static async Task<string> CaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutSeconds = 60)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdout, stderr);
            throw new TimeoutException($"{fileName} exceeded the {timeoutSeconds}-second timeout.");
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}: {error.Trim()}");
        }

        return output.Trim();
    }

    public static async Task<BuildProcessResult> CaptureResultAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutSeconds = 60,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdout, stderr);
            throw new TimeoutException($"{fileName} exceeded the {timeoutSeconds}-second timeout.");
        }

        return new BuildProcessResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    public static async Task RunExpectingExitCodeAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        int expectedExitCode,
        int timeoutSeconds = 60)
    {
        var args = arguments.ToArray();
        Console.WriteLine($"> {fileName} {string.Join(" ", args.Select(DisplayArgument))}");
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = PumpAsync(process.StandardOutput, Console.Out);
        var stderr = PumpAsync(process.StandardError, Console.Error);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdout, stderr);
            throw new TimeoutException($"{fileName} exceeded the {timeoutSeconds}-second timeout.");
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != expectedExitCode)
        {
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}; expected {expectedExitCode}.");
        }
    }

    private static async Task PumpAsync(StreamReader reader, TextWriter writer)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
        }
    }

    private static string DisplayArgument(string argument)
    {
        if (argument.Length != 0 && argument.All(character =>
                char.IsAsciiLetterOrDigit(character) || "-._/:=+*".IndexOf(character) >= 0))
        {
            return argument;
        }

        return '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process won the race and already exited.
        }
    }

    private static async Task KillAndDrainAsync(Process process, params Task[] outputTasks)
    {
        TryKill(process);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputTasks);
    }
}

internal sealed record BuildProcessResult(int ExitCode, string StandardOutput, string StandardError);
