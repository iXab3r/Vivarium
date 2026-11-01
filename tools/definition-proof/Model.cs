namespace DefinitionProof;

// P0 representability candidate; not the public SDK or a second product catalog.
public sealed record Binding(string Repository, string Path, string Branch, string TrustReference, string CredentialReference);
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
    string? CheckoutMode = null);
public sealed record Settings(string ProjectId, Source? Source, Parameter[] Parameters, Configuration[] Configurations);
public sealed record ResolvedParameter(string Name, string Value, bool Inherited);

// Alternative source-edit candidate, used only by the computed-source feasibility study.
// The one declaration is C# in Managed.Edits; JSON is a transient read/editor projection.
public sealed record ParameterEdit(string Name, string? Value = null, bool Remove = false);
public sealed record ConfigurationEdit(string Configuration, string? Name = null,
    ParameterEdit[]? Parameters = null, Step[]? Steps = null, int? TimeoutMinutes = null);

public static class ProofEdits
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
            var changed = original with
            {
                Name = edit.Name ?? original.Name,
                Parameters = parameters,
                Steps = edit.Steps ?? original.Steps,
                TimeoutMinutes = edit.TimeoutMinutes ?? original.TimeoutMinutes,
            };
            settings = settings with
            {
                Configurations = settings.Configurations.Select(c => c.Id == edit.Configuration ? changed : c).ToArray(),
            };
        }
        return settings;
    }
}
