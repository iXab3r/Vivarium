using System.Text.Json;
using DefinitionProof;

var project = Control.Declare();
DefinitionContext.Initialize(project.Binding?.Context ?? []);
var settings = Managed.Declare();
if (project.Id != settings.ProjectId) throw new InvalidOperationException("Settings owner mismatch.");
var templates = settings.Templates ?? [];
var identities = settings.Configurations.Select(c => c.Id).Concat(templates.Select(t => t.Id)).ToArray();
if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
    throw new InvalidOperationException("Duplicate configuration identity.");
if (settings.Configurations.Any(c => (c.Templates ?? []).Any(id => !templates.Any(t => t.Id == id))))
    throw new InvalidOperationException("Missing parameter template.");
if (settings.Configurations.Any(c => (c.Templates ?? []).Distinct(StringComparer.Ordinal).Count() != (c.Templates ?? []).Length))
    throw new InvalidOperationException("Duplicate template attachment.");
if (settings.Configurations.Select(c => c.Parameters).Concat(templates.Select(t => t.Parameters)).Append(settings.Parameters)
    .Any(parameters => parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parameters.Length))
    throw new InvalidOperationException("Duplicate parameter declaration.");
if (settings.Configurations.Any(c => c.Snapshots.Any(s => !settings.Configurations.Any(p => p.Id == s.Configuration))))
    throw new InvalidOperationException("Missing snapshot producer.");
string Resolve(Parameter p)
{
    if ((p.Literal is null) == (p.Expression is null)) throw new InvalidOperationException("Exactly one parameter source required.");
    return p.Literal ?? UserCode.Evaluate(p.Expression!);
}
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (args.SequenceEqual(new[] { "declared" }))
{
    var managedEdits = typeof(Managed).GetProperty("Edits")?.GetValue(null);
    Console.Write(JsonSerializer.Serialize(new { project, settings, managedEdits }, options));
    return;
}
var effective = settings.Configurations.Select(c => new
{
    c.Id, c.Name, c.TimeoutMinutes, c.CheckoutMode, c.Requirements, c.Snapshots, c.Artifacts, c.Triggers,
    templates = c.Templates ?? [],
    parameters = c.Parameters.Select(p => (Parameter: p, Inherited: false))
        .Concat(settings.Parameters.Select(p => (Parameter: p, Inherited: true)))
        .Concat((c.Templates ?? []).SelectMany(id => templates.Single(t => t.Id == id).Parameters)
            .Select(p => (Parameter: p, Inherited: true)))
        .DistinctBy(p => p.Parameter.Name, StringComparer.Ordinal)
        .Select(p => new ResolvedParameter(p.Parameter.Name, Resolve(p.Parameter), p.Inherited))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(),
    steps = c.Steps.Select(s => new { s.Id, s.Name, s.Runner,
        properties = s.Properties.Select(p => new ResolvedParameter(p.Name, Resolve(p), false)).ToArray() }).ToArray()
}).ToArray();
Console.Write(JsonSerializer.Serialize(new { project, settings.Source, configurations = effective,
    contextReads = DefinitionContext.Reads }, options));
