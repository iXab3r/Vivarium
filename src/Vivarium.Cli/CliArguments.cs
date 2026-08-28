namespace Vivarium.Cli;

internal abstract record CliCommand;

internal sealed record VersionCommand : CliCommand;

internal sealed record LoginCommand(
    string Url,
    string? Token,
    string? Fingerprint) : CliCommand;

internal sealed record RunCommand(
    string Configuration,
    string FilePath,
    IReadOnlyList<string> OnlyCells,
    bool NoWait,
    string? Url,
    string? Token,
    string? Fingerprint) : CliCommand;

internal sealed record CancelCommand(
    string BuildId,
    string Reason,
    string? Url,
    string? Token,
    string? Fingerprint) : CliCommand;

internal sealed class CliUsageException(string message) : Exception(message);

internal static class CliArguments
{
    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] == "--version")
        {
            return new VersionCommand();
        }

        if (args.Count == 0)
        {
            throw new CliUsageException(Usage);
        }

        return args[0] switch
        {
            "login" => ParseLogin(args),
            "run" => ParseRun(args),
            "cancel" => ParseCancel(args),
            "--help" or "-h" => throw new CliUsageException(Usage),
            _ => throw new CliUsageException($"unknown command '{args[0]}'.{Environment.NewLine}{Usage}"),
        };
    }

    private static LoginCommand ParseLogin(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliUsageException("login requires a controller URL");
        }

        string? token = null;
        string? fingerprint = null;
        for (var index = 2; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--token":
                    token = ReadSingleValue(args, ref index, "--token", token);
                    break;
                case "--fingerprint":
                    fingerprint = ReadSingleValue(args, ref index, "--fingerprint", fingerprint);
                    break;
                default:
                    throw new CliUsageException($"unknown login option '{args[index]}'");
            }
        }

        return new LoginCommand(args[1], token, fingerprint);
    }

    private static RunCommand ParseRun(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliUsageException("run requires a configuration name");
        }

        var filePath = "vivarium.yaml";
        var fileSet = false;
        var only = new List<string>();
        var noWait = false;
        string? url = null;
        string? token = null;
        string? fingerprint = null;
        for (var index = 2; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--file":
                    filePath = ReadSingleValue(args, ref index, "--file", fileSet ? filePath : null);
                    fileSet = true;
                    break;
                case "--only":
                    only.Add(ReadRequiredValue(args, ref index, "--only"));
                    break;
                case "--no-wait":
                    if (noWait)
                    {
                        throw new CliUsageException("--no-wait may be specified only once");
                    }

                    noWait = true;
                    break;
                case "--url":
                    url = ReadSingleValue(args, ref index, "--url", url);
                    break;
                case "--token":
                    token = ReadSingleValue(args, ref index, "--token", token);
                    break;
                case "--fingerprint":
                    fingerprint = ReadSingleValue(args, ref index, "--fingerprint", fingerprint);
                    break;
                default:
                    throw new CliUsageException($"unknown run option '{args[index]}'");
            }
        }

        return new RunCommand(args[1], filePath, only, noWait, url, token, fingerprint);
    }

    private static CancelCommand ParseCancel(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliUsageException("cancel requires a matrix build id");
        }

        var reason = "Cancelled by viv CLI";
        var reasonSet = false;
        string? url = null;
        string? token = null;
        string? fingerprint = null;
        for (var index = 2; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--reason":
                    reason = ReadSingleValue(args, ref index, "--reason", reasonSet ? reason : null);
                    reasonSet = true;
                    break;
                case "--url":
                    url = ReadSingleValue(args, ref index, "--url", url);
                    break;
                case "--token":
                    token = ReadSingleValue(args, ref index, "--token", token);
                    break;
                case "--fingerprint":
                    fingerprint = ReadSingleValue(args, ref index, "--fingerprint", fingerprint);
                    break;
                default:
                    throw new CliUsageException($"unknown cancel option '{args[index]}'");
            }
        }

        return new CancelCommand(args[1], reason, url, token, fingerprint);
    }

    private static string ReadSingleValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        string? previous)
    {
        if (previous is not null)
        {
            throw new CliUsageException($"{option} may be specified only once");
        }

        return ReadRequiredValue(args, ref index, option);
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new CliUsageException($"{option} requires a value");
        }

        return args[index];
    }

    private const string Usage = """
        Usage:
          viv --version
          viv login <url> [--token <token>] [--fingerprint SHA256:...]
          viv run <configuration> [--file vivarium.yaml] [--only <cell>]... [--no-wait]
                  [--url <url>] [--token <token>] [--fingerprint SHA256:...]
          viv cancel <matrix-build-id> [--reason <text>]
                  [--url <url>] [--token <token>] [--fingerprint SHA256:...]
        """;
}
