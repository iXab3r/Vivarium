using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vivarium.Definitions;

namespace Vivarium.Controller.Catalog;

/// <summary>Returned C# output is data; it cannot select another owner or add authority.</summary>
public static class DefinitionValidation
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 32,
    };

    public static Project ParseControl(string output, string expectedProject)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(output) > 64 * 1024) Reject();
            var project = JsonSerializer.Deserialize<Project>(output, Json);
            if (project is null) Reject();
            Identity(project!.Id); Identity(project.Parent); Text(project.Name);
            if (project.Id != expectedProject || project.Description is null) Reject();
            if (project.Binding is { } binding)
            {
                Text(binding.Repository); Text(binding.Path); Text(binding.Branch); Text(binding.TrustReference);
                if (binding.CredentialReference is null) Reject();
                Unique((binding.Context ?? []).Select(p => p.Name));
                foreach (var parameter in binding.Context ?? []) if (parameter.Value is null) Reject();
            }
            return project;
        }
        catch (Exception error) when (error is JsonException or NullReferenceException or ArgumentException)
        { throw new InvalidDataException("The evaluator returned an invalid control declaration."); }
    }

    public static EvaluatedDefinition Parse(string output, string expectedProject)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(output) > 64 * 1024) Reject();
            var value = JsonSerializer.Deserialize<EvaluatedDefinition>(output, Json);
            if (value is null) Reject();
            Validate(value!, expectedProject);
            return value!;
        }
        catch (Exception error) when (error is JsonException or NullReferenceException
            or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("The evaluator returned an invalid definition."); }
    }

    private static void Validate(EvaluatedDefinition value, string expectedProject)
    {
        var project = value.Project;
        Identity(project.Id); Identity(project.Parent); Text(project.Name);
        if (project.Id != expectedProject || value.Declared.ProjectId != expectedProject) Reject();
        if (project.Description is null) Reject();
        if (project.Binding is { } binding)
        {
            Text(binding.Repository); Text(binding.Path); Text(binding.Branch);
            Text(binding.TrustReference);
            if (binding.CredentialReference is null) Reject();
            Unique((binding.Context ?? []).Select(p => p.Name));
            foreach (var parameter in binding.Context ?? [])
                if (parameter.Value is null) Reject();
        }
        var declared = value.Declared;
        Parameters(declared.Parameters);
        var templates = declared.Templates ?? [];
        Unique(templates.Select(t => t.Id));
        foreach (var template in templates) { Identity(template.Id); Text(template.Name); Parameters(template.Parameters); }
        Unique(declared.Configurations.Select(c => c.Id).Concat(templates.Select(t => t.Id)));
        var edited = DefinitionEdits.Apply(declared, value.ManagedEdits);
        foreach (var configuration in edited.Configurations)
        {
            Identity(configuration.Id); Text(configuration.Name); Parameters(configuration.Parameters);
            if (configuration.TimeoutMinutes < 0) Reject();
            Unique(configuration.Steps.Select(s => s.Id));
            foreach (var step in configuration.Steps)
            { Identity(step.Id); Text(step.Name); Text(step.Runner); Parameters(step.Properties); }
            Unique(configuration.Requirements.Select(r => r.Name));
            foreach (var requirement in configuration.Requirements) if (requirement.Value is null) Reject();
            Unique(configuration.Snapshots.Select(s => s.Configuration));
            foreach (var relation in configuration.Snapshots)
            {
                if (!edited.Configurations.Any(c => c.Id == relation.Configuration)) Reject();
                Text(relation.Failure); Text(relation.Cancel);
            }
            foreach (var relation in configuration.Artifacts)
            {
                if (!edited.Configurations.Any(c => c.Id == relation.Configuration)) Reject();
                Text(relation.Selector); Text(relation.Rules);
            }
            Unique(configuration.Templates ?? []);
            if ((configuration.Templates ?? []).Any(id => !templates.Any(t => t.Id == id))) Reject();
            foreach (var trigger in configuration.Triggers)
            {
                Text(trigger.Type);
                if (trigger.BranchFilter is null || trigger.QuietSeconds < 0) Reject();
            }
        }
        if (value.Source != edited.Source) Reject();
        if (value.Source is { } source)
        { Identity(source.RootId); Text(source.Url); Text(source.Branch); if (source.CheckoutRules is null) Reject(); }
        if (!value.Configurations.Select(c => c.Id).SequenceEqual(edited.Configurations.Select(c => c.Id))) Reject();
        foreach (var effective in value.Configurations)
        {
            var definition = edited.Configurations.Single(c => c.Id == effective.Id);
            if (effective.Name != definition.Name || effective.TimeoutMinutes != definition.TimeoutMinutes
                || effective.CheckoutMode != definition.CheckoutMode
                || !effective.Templates.SequenceEqual(definition.Templates ?? [])
                || !effective.Requirements.SequenceEqual(definition.Requirements)
                || !effective.Snapshots.SequenceEqual(definition.Snapshots)
                || !effective.Artifacts.SequenceEqual(definition.Artifacts)
                || !effective.Triggers.SequenceEqual(definition.Triggers)
                || !effective.Steps.Select(s => s.Id).SequenceEqual(definition.Steps.Select(s => s.Id))) Reject();
            Resolved(effective.Parameters);
            foreach (var step in effective.Steps)
            {
                var original = definition.Steps.Single(s => s.Id == step.Id);
                if (step.Name != original.Name || step.Runner != original.Runner
                    || !step.Properties.Select(p => p.Name).SequenceEqual(original.Properties.Select(p => p.Name))) Reject();
                Resolved(step.Properties);
            }
        }
        foreach (var read in value.ContextReads) { Text(read.Name); if (read.Value is null) Reject(); }
    }

    private static void Parameters(Parameter[] parameters)
    {
        Unique(parameters.Select(p => p.Name));
        foreach (var parameter in parameters)
            if ((parameter.Literal is null) == (parameter.Expression is null)) Reject();
    }
    private static void Resolved(ResolvedParameter[] parameters)
    {
        Unique(parameters.Select(p => p.Name));
        foreach (var parameter in parameters) if (parameter.Value is null) Reject();
    }
    private static void Unique(IEnumerable<string> values)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) { Text(value); if (!set.Add(value)) Reject(); }
    }
    internal static void Identity(string value)
    {
        Text(value);
        if (value.Length > 512 || value.Any(c => c is '/' or '\\' || char.IsControl(c)) || value is "." or "..") Reject();
    }
    private static void Text(string value) { if (string.IsNullOrWhiteSpace(value)) Reject(); }
    private static void Reject() => throw new InvalidDataException("The evaluator returned an invalid definition.");
}
