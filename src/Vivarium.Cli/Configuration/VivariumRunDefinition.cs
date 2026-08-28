namespace Vivarium.Cli.Configuration;

public enum VivariumStepPolicy
{
    Default,
    EvenIfFailed,
    Always,
}

public enum VivariumOnFail
{
    None,
    Keep,
}

public sealed record ResolvedVivariumRun(
    string Project,
    string Configuration,
    IReadOnlyList<ResolvedVivariumCell> Cells);

public sealed record ResolvedVivariumCell(
    string Name,
    string AgentRequirement,
    string? RuntimeIdentifier,
    ResolvedPayload Payload,
    IReadOnlyList<ResolvedVivariumStep> Steps,
    IReadOnlyList<string> Collect,
    TimeSpan? QueueTimeout,
    VivariumOnFail OnFail);

public sealed record ResolvedPayload(
    string SourceDirectory,
    string RelativeDirectory);

public sealed record ResolvedVivariumStep(
    string Program,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    TimeSpan? Timeout,
    VivariumStepPolicy Policy);

public sealed class VivariumConfigurationException(string path, string message, Exception? innerException = null)
    : Exception($"{path}: {message}", innerException)
{
    public string ConfigurationPath { get; } = path;
}
