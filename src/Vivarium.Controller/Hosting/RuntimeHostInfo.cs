using System.Reflection;
using Vivarium.Contracts;

namespace Vivarium.Controller.Hosting;

/// <summary>Release identity and lifecycle of this Host instance, including multiple Hosts in one process.</summary>
internal sealed class RuntimeHostInfo(TimeProvider time)
{
    internal string Version { get; } = VivariumProductVersion.FromAssembly(typeof(VivariumRuntime).Assembly);
    internal string Build { get; } = typeof(VivariumRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VivariumRuntime).Assembly.GetName().Version?.ToString() ?? "unknown";
    internal DateTimeOffset? StartedAt { get; private set; }
    internal DateTimeOffset CurrentTime => time.GetUtcNow();
    internal void Started() => StartedAt = time.GetUtcNow();
}
