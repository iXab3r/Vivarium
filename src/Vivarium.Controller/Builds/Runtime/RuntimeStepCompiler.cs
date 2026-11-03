using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Catalog;
using Vivarium.Definitions;
using AgentStep = Vivarium.Contracts.V1.Step;

namespace Vivarium.Controller.Builds.Runtime;

internal sealed record RuntimePreparedAssignment(BuildAssignment Assignment, RuntimeBuildPayload[] Payloads);

/// <summary>Translate the selected C#/Git process contract to the existing files-in/process Agent SDK.</summary>
internal sealed class RuntimeStepCompiler(RuntimeBuildBytes bytes, RuntimeBuildSecrets secrets)
{
    internal async Task<RuntimePreparedAssignment> PrepareAsync(RuntimeBuild build, RuntimeBuildInput input,
        CatalogBuildDefinition definition, RuntimeBuildExecution execution, string osFamily, CancellationToken token)
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
        var assignment = new BuildAssignment { BuildId = execution.WorkloadId, OutputContractMajor = 1, ArtifactPublicationContractMajor = 1 };
        var environment = secrets.ReadEnvironment(build.Id, input.ProtectedEnvironment);
        assignment.OutputSecrets.AddRange(environment.Values.Distinct(StringComparer.Ordinal).Select(Google.Protobuf.ByteString.CopyFromUtf8));
        var payloads = new List<RuntimeBuildPayload>();
        for (var index = 0; index < config.Steps.Length; index++)
        {
            var declared = config.Steps[index];
            var properties = declared.Properties.ToDictionary(p => p.Name, p => Expand(p.Value), StringComparer.Ordinal);
            if (declared.Runner != "simpleRunner" || !properties.TryGetValue("script.content", out var script)
                || properties.ContainsKey("teamcity.step.mode") || properties.Keys.Any(p => p.StartsWith("plugin.docker.", StringComparison.Ordinal)))
                throw new InvalidDataException("The selected runner profile is not implemented.");
            var windows = osFamily == "windows";
            var path = ".vivarium/steps/" + index.ToString(CultureInfo.InvariantCulture) + (windows ? ".cmd" : ".sh");
            var content = Encoding.UTF8.GetBytes(windows ? "@echo off\r\n" + script.ReplaceLineEndings("\r\n") : script.ReplaceLineEndings("\n"));
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            var key = $"build-payloads/{build.Id}/{hash}";
            await bytes.PutAsync(key, content, token); // Preparation intent/Agent lease is already durable; no SQL transaction spans this I/O.
            payloads.Add(new() { BuildId = build.Id, Sha256 = hash, ObjectKey = key, Size = content.LongLength });
            assignment.Payload.Add(new Blob { Sha256 = hash, FileName = path });
            var step = new AgentStep { Program = windows ? "cmd.exe" : UnixInterpreter(script) };
            if (windows) step.Args.Add(["/d", "/c", path.Replace('/', '\\')]); else step.Args.Add(path);
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
        return new(assignment, payloads.DistinctBy(p => p.Sha256).ToArray());
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
