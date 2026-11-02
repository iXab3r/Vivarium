using System.Text.Json.Serialization;

namespace Vivarium.Definitions;

// The selected D44 declaration contract. This file is also compiled inside the pinned evaluator.
public sealed record ContextParameter(string Name, string Value);
public sealed record ContextRead(string Name, string Value, bool UsedDefault);
public sealed record Binding(string Repository, string Path, string Branch, string TrustReference,
    string CredentialReference, ContextParameter[]? Context = null);
public sealed record Project(string Id, string Parent, string Name, string Description, Binding? Binding);
public sealed record Parameter(string Name, string? Literal, string? Expression);
public sealed record Step(string Id, string Name, string Runner, Parameter[] Properties);
public sealed record Requirement(string Name, string Value);
public sealed record Snapshot(string Configuration, string Failure, string Cancel);
public sealed record Artifact(string Configuration, string Selector, string Rules, bool CleanDestination);
public sealed record Trigger(string Type, string BranchFilter, int QuietSeconds,
    string? QuietPeriodMode = null, string? Rules = null);
public sealed record Source(string RootId, string Url, string Branch, string CheckoutRules, string? Name = null);
public sealed record Configuration(string Id, string Name, Parameter[] Parameters, Step[] Steps,
    Requirement[] Requirements, Snapshot[] Snapshots, Artifact[] Artifacts, int TimeoutMinutes, Trigger[] Triggers,
    string? CheckoutMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Templates = null);
public sealed record Template(string Id, string Name, Parameter[] Parameters);
public sealed record Settings(string ProjectId, Source? Source, Parameter[] Parameters, Configuration[] Configurations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Template[]? Templates = null);
public sealed record ResolvedParameter(string Name, string Value, bool Inherited);
public sealed record ResolvedStep(string Id, string Name, string Runner, ResolvedParameter[] Properties);
public sealed record ResolvedConfiguration(string Id, string Name, ResolvedParameter[] Parameters,
    ResolvedStep[] Steps, Requirement[] Requirements, Snapshot[] Snapshots, Artifact[] Artifacts,
    int TimeoutMinutes, Trigger[] Triggers, string? CheckoutMode, string[] Templates);
public sealed record EvaluatedDefinition(Project Project, Settings Declared, ConfigurationEdit[] ManagedEdits,
    Source? Source, ResolvedConfiguration[] Configurations, ContextRead[] ContextReads);

public static class DefinitionContext
{
    private static Dictionary<string, string> values = new(StringComparer.Ordinal);
    private static readonly HashSet<ContextRead> reads = [];
    public static void Initialize(ContextParameter[] parameters)
    {
        values = parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        reads.Clear();
    }
    public static string Get(string name, string fallback)
    {
        var found = values.TryGetValue(name, out var value);
        value = found ? value! : fallback;
        reads.Add(new(name, value, !found));
        return value;
    }
    public static ContextRead[] Reads => reads.OrderBy(r => r.Name, StringComparer.Ordinal)
        .ThenBy(r => r.Value, StringComparer.Ordinal).ThenBy(r => r.UsedDefault).ToArray();
}

public sealed record ParameterEdit(string Name, string? Value = null, bool Remove = false);
public sealed record ConfigurationEdit(string Configuration, string? Name = null,
    ParameterEdit[]? Parameters = null, Step[]? Steps = null, int? TimeoutMinutes = null);

public static class DefinitionEdits
{
    public static Settings Apply(Settings settings, ConfigurationEdit[] edits)
    {
        if (edits.Select(e => e.Configuration).Distinct(StringComparer.Ordinal).Count() != edits.Length)
            throw new InvalidOperationException("Duplicate configuration edit.");
        foreach (var edit in edits)
        {
            var original = settings.Configurations.Single(c => c.Id == edit.Configuration);
            if (edit.TimeoutMinutes is < 0) throw new InvalidOperationException("Negative timeout edit.");
            var parameters = original.Parameters;
            if (edit.Parameters is { } changes)
            {
                if (changes.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != changes.Length ||
                    changes.Any(p => p.Remove == (p.Value is not null)))
                    throw new InvalidOperationException("Ambiguous parameter edit.");
                parameters = parameters.Where(p => !changes.Any(e => e.Name == p.Name))
                    .Concat(changes.Where(p => !p.Remove).Select(p => new Parameter(p.Name, p.Value, null))).ToArray();
            }
            var changed = original with { Name = edit.Name ?? original.Name, Parameters = parameters,
                Steps = edit.Steps ?? original.Steps, TimeoutMinutes = edit.TimeoutMinutes ?? original.TimeoutMinutes };
            settings = settings with { Configurations = settings.Configurations.Select(c => c.Id == edit.Configuration ? changed : c).ToArray() };
        }
        return settings;
    }
}
