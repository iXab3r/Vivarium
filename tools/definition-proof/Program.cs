using System.Text.Json;
using DefinitionProof;

var project = Control.Declare();
var settings = Managed.Declare();
if (project.Id != settings.ProjectId) throw new InvalidOperationException("Settings owner mismatch.");
if (settings.Configurations.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != settings.Configurations.Length)
    throw new InvalidOperationException("Duplicate configuration identity.");
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
    parameters = settings.Parameters.Where(p => !c.Parameters.Any(local => local.Name == p.Name))
        .Select(p => new ResolvedParameter(p.Name, Resolve(p), true))
        .Concat(c.Parameters.Select(p => new ResolvedParameter(p.Name, Resolve(p), false)))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(),
    steps = c.Steps.Select(s => new { s.Id, s.Name, s.Runner,
        properties = s.Properties.Select(p => new ResolvedParameter(p.Name, Resolve(p), false)).ToArray() }).ToArray()
}).ToArray();
Console.Write(JsonSerializer.Serialize(new { project, settings.Source, configurations = effective }, options));
