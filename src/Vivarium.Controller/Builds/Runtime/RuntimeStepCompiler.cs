using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Catalog;
using Vivarium.Definitions;
using AgentStep = Vivarium.Contracts.V1.Step;

namespace Vivarium.Controller.Builds.Runtime;

internal sealed record RuntimePreparedAssignment(BuildAssignment Assignment, RuntimeBuildPayload[] Payloads,
    RuntimeBuildConsumedArtifact[] ConsumedArtifacts);

/// <summary>Translate the selected C#/Git process contract to the existing files-in/process Agent SDK.</summary>
internal sealed class RuntimeStepCompiler(RuntimeBuildBytes bytes, RuntimeBuildSecrets secrets,
    IDbContextFactory<Vivarium.Controller.Persistence.VivariumDbContext> databases, RuntimeBuildSources sources)
{
    internal async Task<RuntimePreparedAssignment> PrepareAsync(RuntimeBuild build, RuntimeBuildInput input,
        CatalogBuildDefinition definition, RuntimeBuildExecution execution, string osFamily, CancellationToken token, bool artifactRules = false,
        bool buildNumbers = false)
    {
        var config = JsonSerializer.Deserialize<ResolvedConfiguration>(definition.PlanJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("The collected Build definition is invalid.");
        if (IsolatedDefinitionEvaluator.Hash(JsonSerializer.Serialize(config, DefinitionValidation.Json)) != definition.PlanHash)
            throw new InvalidDataException("The collected Build definition integrity differs.");
        var values = config.Parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(input.OverridesJson)
            ?? throw new InvalidDataException("Collected Build parameters are invalid.");
        foreach (var (name, value) in overrides) values[name] = value;
        var counter = build.Counter ?? throw new InvalidDataException("The claimed Build has no counter.");
        var number = build.Number ?? throw new InvalidDataException("The claimed Build has no number.");
        values["build.counter"] = counter.ToString(CultureInfo.InvariantCulture);
        values["build.number"] = number;
        values["teamcity.build.id"] = build.Id.ToString(CultureInfo.InvariantCulture);
        string Expand(string value) => Regex.Replace(value, "%([^%\\r\\n]+)%", match =>
            values.TryGetValue(match.Groups[1].Value, out var resolved) ? resolved : match.Value, RegexOptions.CultureInvariant);
        var assignment = new BuildAssignment { BuildId = execution.WorkloadId, OutputContractMajor = 1, ArtifactPublicationContractMajor = artifactRules ? 2u : 1u };
        assignment.NumberReportingContractMajor = buildNumbers ? 1u : 0u;
        assignment.VcsCheckout = await sources.SelectAsync(input, execution, token);
        var environment = secrets.ReadEnvironment(build.Id, input.ProtectedEnvironment);
        assignment.OutputSecrets.AddRange(environment.Values.Distinct(StringComparer.Ordinal).Select(Google.Protobuf.ByteString.CopyFromUtf8));
        var payloads = new List<RuntimeBuildPayload>();
        var consumed = new List<RuntimeBuildConsumedArtifact>();
        if (config.Artifacts.Length != 0)
        {
            await using var database = await databases.CreateDbContextAsync(token);
            var destinations = new HashSet<string>(osFamily == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var relation in config.Artifacts)
            {
                var selected = await RuntimeBuildService.DependencyHandles(database, build.Id)
                    .Join(database.CatalogBuildTypes, d => d.BuildTypeId, t => t.Id, (d, t) => new { Dependency = d, t.ExternalId })
                    .SingleAsync(d => d.Dependency.Kind == "ARTIFACT" && d.ExternalId == relation.Configuration, token);
                var producer = await database.RuntimeBuilds.AsNoTracking().SingleAsync(b => b.Id == selected.Dependency.Id, token);
                if (producer.State != "FINISHED" || producer.Status != "SUCCESS")
                    throw new InvalidDataException("The selected artifact producer is not complete.");
                var manifest = await database.RuntimeBuildArtifacts.AsNoTracking().Where(a => a.BuildId == producer.Id)
                    .OrderBy(a => a.Path).ToArrayAsync(token);
                foreach (var text in relation.Rules.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var rule = Vivarium.Contracts.ArtifactRuleProfile.Parse(text);
                    if (rule.Destination is null || !artifactRules && rule.Pattern.Contains('*'))
                        throw new InvalidDataException("The selected artifact rule profile is unavailable.");
                    var matched = 0;
                    foreach (var artifact in manifest)
                    {
                        var destination = rule.Map(artifact.Path, ignoreCase: false);
                        if (destination is null) continue;
                        if (destination.StartsWith(".vivarium/", StringComparison.OrdinalIgnoreCase) || !destinations.Add(destination) || consumed.Count >= 128)
                            throw new InvalidDataException("Artifact destinations conflict or exceed the current bound.");
                        var upload = await database.RuntimeArtifactUploads.AsNoTracking()
                            .SingleAsync(u => u.BuildId == producer.Id && u.Sha256 == artifact.Sha256, token);
                        assignment.Payload.Add(new Blob { Sha256 = artifact.Sha256, FileName = destination });
                        payloads.Add(new() { BuildId = build.Id, Sha256 = artifact.Sha256, ObjectKey = upload.ObjectKey, Size = artifact.Size });
                        consumed.Add(new() { BuildId = build.Id, Ordinal = consumed.Count, ProducerBuildId = producer.Id,
                            ProducerArtifactOrdinal = artifact.Ordinal, DestinationPath = destination });
                        matched++;
                    }
                    if (matched == 0) throw new RuntimeArtifactResolutionException();
                }
            }
        }
        for (var index = 0; index < config.Steps.Length; index++)
        {
            var declared = config.Steps[index];
            var properties = declared.Properties.ToDictionary(p => p.Name, p => Expand(p.Value), StringComparer.Ordinal);
            if (properties.ContainsKey("teamcity.step.mode"))
                throw new InvalidDataException("The selected runner profile is not implemented.");
            var windows = osFamily == "windows";
            AgentStep step;
            if (declared.Runner == "simpleRunner" && properties.TryGetValue("script.content", out var script))
            {
                var path = ".vivarium/steps/" + index.ToString(CultureInfo.InvariantCulture) + (windows ? ".cmd" : ".sh");
                var content = Encoding.UTF8.GetBytes(windows ? "@echo off\r\n" + script.ReplaceLineEndings("\r\n") : script.ReplaceLineEndings("\n"));
                var hash = Convert.ToHexStringLower(SHA256.HashData(content));
                var key = $"build-payloads/{build.Id}/{hash}";
                await bytes.PutAsync(key, content, token); // Durable preparation/Agent lease precedes Storage I/O.
                payloads.Add(new() { BuildId = build.Id, Sha256 = hash, ObjectKey = key, Size = content.LongLength });
                assignment.Payload.Add(new Blob { Sha256 = hash, FileName = path, PrivateInput = assignment.VcsCheckout is not null });
                step = new AgentStep { Program = windows ? "cmd.exe" : UnixInterpreter(script) };
                if (windows) step.Args.Add(["/d", "/c", path.Replace('/', '\\')]); else step.Args.Add(path);
                if (assignment.VcsCheckout is not null) { step.ScriptInput = path; step.ScriptArgumentIndex = step.Args.Count - 1; }
            }
            else if (declared.Runner == "jetbrains_powershell" && windows && assignment.VcsCheckout is not null
                && properties.GetValueOrDefault("jetbrains_powershell_script_mode") == "FILE"
                && properties.GetValueOrDefault("jetbrains_powershell_execution") == "PS1"
                && properties.GetValueOrDefault("jetbrains_powershell_noprofile") == "true"
                && properties.GetValueOrDefault("jetbrains_powershell_bitness") == "x64"
                && properties.TryGetValue("jetbrains_powershell_script_file", out var file)
                && properties.Count == 5 && file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                // The pinned 222742 plugin selects Desktop before Core when edition is absent.
                // Reuse the existing files/process SDK; FILE is an actual checked-out author script.
                step = new AgentStep { Program = "powershell.exe" };
                step.Args.Add(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "ByPass", "-File", RelativePath(file).Replace('/', '\\')]);
            }
            else throw new InvalidDataException("The selected runner profile is not implemented.");
            if (properties.Keys.Any(p => p.StartsWith("plugin.docker.", StringComparison.Ordinal)))
            {
                if (windows || declared.Runner != "simpleRunner")
                    throw new InvalidDataException("The selected container runner platform is not implemented.");
                step.Container = ContainerProfile(properties);
            }
            foreach (var (name, value) in values.Where(p => p.Key.StartsWith("env.", StringComparison.Ordinal)))
                step.Env[name[4..]] = Expand(value);
            step.Env["BUILD_NUMBER"] = number;
            step.Env["BUILD_ID"] = build.Id.ToString(CultureInfo.InvariantCulture);
            // Explicit run secrets override ordinary env parameters, including case aliases on
            // Windows. They never participate in script or parameter expansion.
            foreach (var (name, value) in environment)
            {
                foreach (var alias in step.Env.Keys.Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToArray())
                    step.Env.Remove(alias);
                step.Env[name] = value;
            }
            assignment.Steps.Add(step);
        }
        if (assignment.CalculateSize() > 3 * 1024 * 1024)
            throw new InvalidDataException("The prepared assignment exceeds the selected transport profile.");
        return new(assignment, payloads.DistinctBy(p => p.Sha256).ToArray(), consumed.ToArray());
    }

    internal static bool MatchesRunnerPlatform(ResolvedConfiguration config, IReadOnlyDictionary<string, string> reported, string? observation)
    {
        var powershell = config.Steps.Any(s => s.Runner == "jetbrains_powershell");
        var container = RequiresContainer(config);
        if (!powershell && !container) return true;
        var os = container ? "linux" : "windows";
        if (powershell && container || reported.GetValueOrDefault("system.os.family") != os || observation is null) return false;
        // Process architecture is an existing typed observation, not a freely writable custom
        // requirement or an invented fallback to OS architecture.
        var facts = JsonSerializer.Deserialize<Vivarium.Controller.Agents.AgentStaticObservation>(observation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return facts?.Facts is { ProcessArchitecture: "x64" } host && host.OsFamily == os;
    }

    internal static bool RequiresContainer(ResolvedConfiguration config) => config.Steps.Any(s =>
        s.Properties.Any(p => p.Name.StartsWith("plugin.docker.", StringComparison.Ordinal)));

    internal static bool RequiresArtifactRules(ResolvedConfiguration config) => config.Artifacts.Any(a => a.Rules.Contains('*') || a.Rules.Trim().Contains('\n'));

    private static ContainerStep ContainerProfile(IReadOnlyDictionary<string, string> properties)
    {
        var names = new[] { "plugin.docker.imageId", "plugin.docker.imagePlatform", "plugin.docker.run.parameters" };
        if (properties.Keys.Any(p => p.StartsWith("plugin.docker.", StringComparison.Ordinal) && !names.Contains(p))
            || properties.GetValueOrDefault("plugin.docker.imagePlatform") != "linux")
            throw new InvalidDataException("The selected Docker property profile is not implemented.");
        var value = new ContainerStep { Image = properties.GetValueOrDefault("plugin.docker.imageId") ?? "", Platform = "linux/amd64", User = "0" };
        var arguments = (properties.GetValueOrDefault("plugin.docker.run.parameters") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var flag = arguments[index];
            if (flag != "--volume" && !seen.Add(flag)) throw new InvalidDataException("Duplicate Docker option.");
            if (flag == "--init") { value.Init = true; continue; }
            if (++index == arguments.Length) throw new InvalidDataException("A Docker option value is missing.");
            var argument = arguments[index];
            switch (flag)
            {
                case "--user": value.User = argument; break;
                case "--platform" when argument == "linux/amd64": value.Platform = argument; break;
                case "--volume":
                    var pair = argument.Split(':');
                    if (pair.Length != 2) throw new InvalidDataException("Only scoped named Docker caches are implemented.");
                    value.Caches.Add(new ContainerCache { Name = pair[0], Target = pair[1] }); break;
                default: throw new InvalidDataException("The selected Docker option is not implemented.");
            }
        }
        Vivarium.Contracts.ContainerStepProfile.Validate(value);
        return value;
    }

    private static string RelativePath(string path)
    {
        var value = path.Replace('\\', '/');
        if (value.Length is 0 or > 2048 || value.Contains(':') || value.Any(char.IsControl)
            || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("An artifact rule path is not a bounded relative path.");
        return value;
    }

    private static string UnixInterpreter(string script)
    {
        if (!script.StartsWith("#!", StringComparison.Ordinal)) return "/bin/sh";
        var line = script.Split('\n', 2)[0].TrimEnd('\r');
        return line switch
        {
            "#!/bin/sh" or "#!/usr/bin/env sh" => "/bin/sh",
            "#!/bin/bash" or "#!/usr/bin/env bash" => "/bin/bash",
            _ => throw new InvalidDataException("The selected script interpreter is not implemented."),
        };
    }
}

internal sealed class RuntimeArtifactResolutionException : IOException;
