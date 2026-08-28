using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vivarium.Cli.Configuration;

public static partial class VivariumDefinitionParser
{
    private const string ConfigurationFilePath = "vivarium.yaml";

    private static readonly IReadOnlyDictionary<string, RidDescriptor> SupportedRids =
        new Dictionary<string, RidDescriptor>(StringComparer.Ordinal)
        {
            ["win-x64"] = new("windows", "x64", ".exe"),
            ["linux-x64"] = new("linux", "x64", ""),
            ["linux-arm64"] = new("linux", "arm64", ""),
            ["osx-arm64"] = new("macos", "arm64", ""),
        };

    public static ResolvedVivariumRun ParseFile(
        string filePath,
        string configuration,
        IEnumerable<string>? onlyCells = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var root = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The configuration file must have a parent directory.", nameof(filePath));
        return Parse(File.ReadAllText(fullPath), root, configuration, onlyCells);
    }

    public static ResolvedVivariumRun Parse(
        string yaml,
        string configurationRoot,
        string configuration,
        IEnumerable<string>? onlyCells = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var root = ReadDocument(yaml).AsMapping(ConfigurationFilePath);
        var rootValues = ReadFields(root, ConfigurationFilePath, ["project", "configurations"]);
        var project = RequiredScalar(rootValues, "project", ConfigurationFilePath);
        RequireNonEmpty(project, "project");

        var configurationsPath = "configurations";
        var configurations = Required(rootValues, "configurations", ConfigurationFilePath)
            .AsMapping(configurationsPath);
        var namedConfigurations = ReadNamedValues(configurations, configurationsPath);
        var parsedConfigurations = namedConfigurations.ToDictionary(
            entry => entry.Key,
            entry => ParseConfiguration(entry.Value, entry.Key),
            StringComparer.Ordinal);
        if (!parsedConfigurations.TryGetValue(configuration, out var definition))
        {
            throw Error(configurationsPath, $"configuration '{configuration}' does not exist");
        }

        var selectedCells = SelectCells(definition.Cells, onlyCells);
        var fullRoot = Path.GetFullPath(configurationRoot);
        var resolvedCells = selectedCells
            .Select(cell => ResolveCell(definition, cell, fullRoot))
            .ToArray();

        return new ResolvedVivariumRun(project, configuration, resolvedCells);
    }

    private static RawConfiguration ParseConfiguration(YamlValue node, string name)
    {
        var path = $"configurations.{name}";
        var values = ReadFields(
            node.AsMapping(path),
            path,
            ["matrix", "payload", "steps", "collect", "queue_timeout", "clean", "on_fail"]);

        var cells = ParseCells(Required(values, "matrix", path), $"{path}.matrix");
        var payload = RequiredScalar(values, "payload", path);
        RequireNonEmpty(payload, $"{path}.payload");
        var steps = ParseSteps(Required(values, "steps", path), $"{path}.steps");
        var collect = values.TryGetValue("collect", out var collectNode)
            ? ReadScalarSequence(collectNode, $"{path}.collect")
            : [];
        TimeSpan? queueTimeout = values.TryGetValue("queue_timeout", out var queueTimeoutNode)
            ? ParseDuration(queueTimeoutNode.AsScalar($"{path}.queue_timeout"), $"{path}.queue_timeout")
            : null;

        if (values.TryGetValue("clean", out var cleanNode))
        {
            var clean = cleanNode.AsScalar($"{path}.clean");
            if (!clean.Equals("none", StringComparison.Ordinal))
            {
                throw Error($"{path}.clean", "only 'none' is supported in Phase 1");
            }
        }

        var onFail = VivariumOnFail.None;
        if (values.TryGetValue("on_fail", out var onFailNode))
        {
            onFail = onFailNode.AsScalar($"{path}.on_fail") switch
            {
                "none" => VivariumOnFail.None,
                "keep" => VivariumOnFail.Keep,
                _ => throw Error($"{path}.on_fail", "expected 'none' or 'keep'"),
            };
        }

        return new RawConfiguration(path, cells, payload, steps, collect, queueTimeout, onFail);
    }

    private static IReadOnlyList<RawCell> ParseCells(YamlValue node, string path)
    {
        var mapping = node.AsMapping(path);
        var namedCells = ReadNamedValues(mapping, path);
        if (namedCells.Count == 0)
        {
            throw Error(path, "at least one named matrix cell is required");
        }

        var result = new List<RawCell>(namedCells.Count);
        foreach (var (name, cellNode) in namedCells)
        {
            var cellPath = $"{path}.{name}";
            if (!NamePattern().IsMatch(name))
            {
                throw Error(cellPath, "cell names may contain only letters, digits, '.', '_' and '-'");
            }

            var values = ReadFields(cellNode.AsMapping(cellPath), cellPath, ["agent", "rid"]);
            var agent = RequiredScalar(values, "agent", cellPath);
            RequireNonEmpty(agent, $"{cellPath}.agent");

            string? rid = null;
            if (values.TryGetValue("rid", out var ridNode))
            {
                rid = ridNode.AsScalar($"{cellPath}.rid");
                if (!SupportedRids.ContainsKey(rid))
                {
                    throw Error(
                        $"{cellPath}.rid",
                        $"unsupported RID '{rid}'; expected one of {string.Join(", ", SupportedRids.Keys)}");
                }
            }

            result.Add(new RawCell(name, agent, rid, cellPath));
        }

        return result;
    }

    private static IReadOnlyList<RawStep> ParseSteps(YamlValue node, string path)
    {
        var sequence = node.AsSequence(path);
        if (sequence.Values.Count == 0)
        {
            throw Error(path, "at least one step is required");
        }

        var result = new List<RawStep>(sequence.Values.Count);
        for (var index = 0; index < sequence.Values.Count; index++)
        {
            var stepPath = $"{path}[{index}]";
            var values = ReadFields(
                sequence.Values[index].AsMapping(stepPath),
                stepPath,
                ["program", "args", "env", "cwd", "timeout", "policy"]);
            var program = RequiredScalar(values, "program", stepPath);
            RequireNonEmpty(program, $"{stepPath}.program");
            var arguments = values.TryGetValue("args", out var argsNode)
                ? ReadScalarSequence(argsNode, $"{stepPath}.args")
                : [];
            var environment = values.TryGetValue("env", out var envNode)
                ? ReadStringMap(envNode, $"{stepPath}.env")
                : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
            var cwd = values.TryGetValue("cwd", out var cwdNode)
                ? cwdNode.AsScalar($"{stepPath}.cwd")
                : ".";
            TimeSpan? timeout = values.TryGetValue("timeout", out var timeoutNode)
                ? ParseDuration(timeoutNode.AsScalar($"{stepPath}.timeout"), $"{stepPath}.timeout")
                : null;
            var policy = values.TryGetValue("policy", out var policyNode)
                ? ParsePolicy(policyNode.AsScalar($"{stepPath}.policy"), $"{stepPath}.policy")
                : VivariumStepPolicy.Default;

            result.Add(new RawStep(program, arguments, environment, cwd, timeout, policy, stepPath));
        }

        return result;
    }

    private static IReadOnlyList<RawCell> SelectCells(
        IReadOnlyList<RawCell> cells,
        IEnumerable<string>? onlyCells)
    {
        if (onlyCells is null)
        {
            return cells;
        }

        var requested = onlyCells.ToArray();
        if (requested.Length == 0)
        {
            return cells;
        }

        var requestedSet = new HashSet<string>(StringComparer.Ordinal);
        var known = cells.Select(cell => cell.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Error("--only", "cell name must not be empty");
            }

            if (!requestedSet.Add(name))
            {
                throw Error("--only", $"cell '{name}' was selected more than once");
            }

            if (!known.Contains(name))
            {
                throw Error("--only", $"matrix cell '{name}' does not exist");
            }
        }

        return cells.Where(cell => requestedSet.Contains(cell.Name)).ToArray();
    }

    private static ResolvedVivariumCell ResolveCell(
        RawConfiguration configuration,
        RawCell cell,
        string configurationRoot)
    {
        var rid = cell.RuntimeIdentifier is null ? null : SupportedRids[cell.RuntimeIdentifier];
        var context = new TemplateContext(cell.RuntimeIdentifier, rid);
        var payloadText = ResolveTemplates(configuration.Payload, context, $"{configuration.Path}.payload");
        var payload = ResolvePayload(payloadText, configurationRoot, $"{configuration.Path}.payload");
        var steps = configuration.Steps
            .Select(step => ResolveStep(step, context))
            .ToArray();
        var collect = configuration.Collect
            .Select((glob, index) => ResolveCollectGlob(
                ResolveTemplates(glob, context, $"{configuration.Path}.collect[{index}]"),
                $"{configuration.Path}.collect[{index}]"))
            .ToArray();

        return new ResolvedVivariumCell(
            cell.Name,
            cell.AgentRequirement,
            cell.RuntimeIdentifier,
            payload,
            steps,
            collect,
            configuration.QueueTimeout,
            configuration.OnFail);
    }

    private static ResolvedVivariumStep ResolveStep(RawStep step, TemplateContext context)
    {
        var program = ResolveTemplates(step.Program, context, $"{step.Path}.program");
        RequireNonEmpty(program, $"{step.Path}.program");
        var arguments = step.Arguments
            .Select((argument, index) => ResolveTemplates(argument, context, $"{step.Path}.args[{index}]"))
            .ToArray();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in step.Environment)
        {
            environment[name] = ResolveTemplates(value, context, $"{step.Path}.env.{name}");
        }

        var cwd = ResolveWorkdirPath(
            ResolveTemplates(step.WorkingDirectory, context, $"{step.Path}.cwd"),
            $"{step.Path}.cwd");
        return new ResolvedVivariumStep(
            program,
            arguments,
            new ReadOnlyDictionary<string, string>(environment),
            cwd,
            step.Timeout,
            step.Policy);
    }

    private static string ResolveTemplates(string value, TemplateContext context, string path)
    {
        var resolved = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '}')
            {
                throw Error(path, "unmatched or malformed template braces");
            }

            if (value[index] != '{')
            {
                resolved.Append(value[index]);
                continue;
            }

            var close = value.IndexOf('}', index + 1);
            if (close < 0 || value.AsSpan(index + 1, close - index - 1).Contains('{'))
            {
                throw Error(path, "unmatched or malformed template braces");
            }

            var template = value[(index + 1)..close];
            resolved.Append(template switch
            {
                "rid" => context.RuntimeIdentifier
                    ?? throw Error(path, "template '{rid}' requires the matrix cell to declare 'rid'"),
                "os" => context.Rid?.Os
                    ?? throw Error(path, "template '{os}' requires the matrix cell to declare 'rid'"),
                "arch" => context.Rid?.Architecture
                    ?? throw Error(path, "template '{arch}' requires the matrix cell to declare 'rid'"),
                "exe" => context.Rid?.ExecutableSuffix
                    ?? throw Error(path, "template '{exe}' requires the matrix cell to declare 'rid'"),
                "results" => "results",
                "workdir" => ".",
                _ when template.Length == 0 => throw Error(path, "unmatched or malformed template braces"),
                _ => throw Error(path, $"unknown template '{{{template}}}'"),
            });
            index = close;
        }

        return resolved.ToString();
    }

    private static ResolvedPayload ResolvePayload(string value, string configurationRoot, string path)
    {
        var normalized = value.Replace('\\', '/');
        if (normalized.EndsWith("/**", StringComparison.Ordinal))
        {
            normalized = normalized[..^3];
        }

        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0 || normalized.Equals(".", StringComparison.Ordinal))
        {
            throw Error(path, "payload must name a directory below the configuration root");
        }

        if (normalized.IndexOfAny(['*', '?', '[', ']']) >= 0)
        {
            throw Error(path, "payload supports only a directory or a directory ending in '/**'");
        }

        EnsureSafeRelativePath(normalized, path, allowCurrentDirectory: false);
        var platformPath = normalized.Replace('/', Path.DirectorySeparatorChar);
        var sourceDirectory = Path.GetFullPath(Path.Combine(configurationRoot, platformPath));
        if (!IsStrictDescendant(configurationRoot, sourceDirectory))
        {
            throw Error(path, "payload directory escapes the configuration root");
        }

        RejectLinkedPayloadPath(configurationRoot, normalized, path);

        return new ResolvedPayload(sourceDirectory, normalized);
    }

    private static void RejectLinkedPayloadPath(string configurationRoot, string relativePath, string path)
    {
        var current = configurationRoot;
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new VivariumConfigurationException(
                    path,
                    $"cannot validate payload directory '{relativePath}'",
                    exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Error(path, "payload directory path must not contain symbolic links or reparse points");
            }
        }
    }

    private static string ResolveWorkdirPath(string value, string path)
    {
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            normalized = ".";
        }

        if (normalized.IndexOfAny(['*', '?', '[', ']']) >= 0)
        {
            throw Error(path, "working directory must not contain wildcards");
        }

        EnsureSafeRelativePath(normalized, path, allowCurrentDirectory: true);
        return normalized;
    }

    private static string ResolveCollectGlob(string value, string path)
    {
        var normalized = value.Replace('\\', '/');
        if (normalized.Length == 0)
        {
            throw Error(path, "collect glob must not be empty");
        }

        EnsureSafeRelativePath(normalized, path, allowCurrentDirectory: true);
        return normalized;
    }

    private static void EnsureSafeRelativePath(string value, string path, bool allowCurrentDirectory)
    {
        if (value.IndexOf('\0') >= 0 ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            WindowsDrivePattern().IsMatch(value) ||
            Path.IsPathRooted(value))
        {
            throw Error(path, "path must be relative");
        }

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Equals("..", StringComparison.Ordinal)))
        {
            throw Error(path, "path must not escape its root with '..'");
        }

        if (!allowCurrentDirectory && segments.Any(segment =>
                segment.Length == 0 || segment.Equals(".", StringComparison.Ordinal)))
        {
            throw Error(path, "payload directory must not contain empty or '.' segments");
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !relative.Equals(".", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static VivariumStepPolicy ParsePolicy(string value, string path) => value switch
    {
        "default" => VivariumStepPolicy.Default,
        "even-if-failed" => VivariumStepPolicy.EvenIfFailed,
        "always" => VivariumStepPolicy.Always,
        _ => throw Error(path, "expected 'default', 'even-if-failed' or 'always'"),
    };

    private static TimeSpan ParseDuration(string value, string path)
    {
        var match = DurationPattern().Match(value);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var amount))
        {
            throw Error(path, "expected a positive integer followed by s, m, h or d (for example '30m')");
        }

        long seconds;
        try
        {
            var secondsPerUnit = match.Groups[2].Value switch
            {
                "s" => 1L,
                "m" => 60L,
                "h" => 60L * 60,
                "d" => 24L * 60 * 60,
                _ => 0L,
            };
            seconds = checked(amount * secondsPerUnit);
        }
        catch (OverflowException)
        {
            throw Error(path, "duration is too large");
        }

        if (seconds > int.MaxValue)
        {
            throw Error(path, "duration exceeds the Phase 1 protocol limit");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static IReadOnlyList<string> ReadScalarSequence(YamlValue node, string path)
    {
        var sequence = node.AsSequence(path);
        var values = new string[sequence.Values.Count];
        for (var index = 0; index < sequence.Values.Count; index++)
        {
            values[index] = sequence.Values[index].AsScalar($"{path}[{index}]");
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(YamlValue node, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in node.AsMapping(path).Entries)
        {
            var name = entry.Key.AsScalar(path);
            if (!values.TryAdd(name, entry.Value.AsScalar($"{path}.{name}")))
            {
                throw Error($"{path}.{name}", "duplicate key");
            }
        }

        return new ReadOnlyDictionary<string, string>(values);
    }

    private static Dictionary<string, YamlValue> ReadFields(
        YamlMapping mapping,
        string path,
        IReadOnlyCollection<string> allowed)
    {
        var values = ReadNamedValues(mapping, path);
        foreach (var key in values.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw Error($"{path}.{key}", "unknown key (not supported in Phase 1)");
            }
        }

        return values;
    }

    private static Dictionary<string, YamlValue> ReadNamedValues(YamlMapping mapping, string path)
    {
        var values = new Dictionary<string, YamlValue>(StringComparer.Ordinal);
        foreach (var entry in mapping.Entries)
        {
            var name = entry.Key.AsScalar(path);
            if (name.Length == 0)
            {
                throw Error(path, "mapping keys must not be empty");
            }

            if (!values.TryAdd(name, entry.Value))
            {
                throw Error($"{path}.{name}", "duplicate key");
            }
        }

        return values;
    }

    private static YamlValue Required(
        IReadOnlyDictionary<string, YamlValue> values,
        string name,
        string path) =>
        values.TryGetValue(name, out var value)
            ? value
            : throw Error($"{path}.{name}", "required key is missing");

    private static string RequiredScalar(
        IReadOnlyDictionary<string, YamlValue> values,
        string name,
        string path) =>
        Required(values, name, path).AsScalar($"{path}.{name}");

    private static void RequireNonEmpty(string value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Error(path, "value must not be empty");
        }
    }

    private static VivariumConfigurationException Error(string path, string message) => new(path, message);

    private static YamlValue ReadDocument(string yaml)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            var result = ReadValue(parser);
            parser.Consume<DocumentEnd>();
            parser.Consume<StreamEnd>();
            return result;
        }
        catch (VivariumConfigurationException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new VivariumConfigurationException(
                ConfigurationFilePath,
                $"invalid YAML at line {exception.Start.Line + 1}, column {exception.Start.Column + 1}",
                exception);
        }
    }

    private static YamlValue ReadValue(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new YamlScalar(scalar.Value ?? "");
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            var entries = new List<YamlEntry>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (!parser.TryConsume<Scalar>(out var key))
                {
                    throw Error(ConfigurationFilePath, "mapping keys must be scalars");
                }

                entries.Add(new YamlEntry(new YamlScalar(key.Value ?? ""), ReadValue(parser)));
            }

            return new YamlMapping(entries);
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            var values = new List<YamlValue>();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                values.Add(ReadValue(parser));
            }

            return new YamlSequence(values);
        }

        if (parser.TryConsume<AnchorAlias>(out _))
        {
            throw Error(ConfigurationFilePath, "YAML aliases are not supported");
        }

        throw Error(ConfigurationFilePath, $"unsupported YAML event '{parser.Current?.GetType().Name ?? "end of input"}'");
    }

    [GeneratedRegex(@"^([1-9][0-9]*)(s|m|h|d)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePattern();

    private sealed record RidDescriptor(string Os, string Architecture, string ExecutableSuffix);

    private sealed record TemplateContext(string? RuntimeIdentifier, RidDescriptor? Rid);

    private sealed record RawConfiguration(
        string Path,
        IReadOnlyList<RawCell> Cells,
        string Payload,
        IReadOnlyList<RawStep> Steps,
        IReadOnlyList<string> Collect,
        TimeSpan? QueueTimeout,
        VivariumOnFail OnFail);

    private sealed record RawCell(
        string Name,
        string AgentRequirement,
        string? RuntimeIdentifier,
        string Path);

    private sealed record RawStep(
        string Program,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        string WorkingDirectory,
        TimeSpan? Timeout,
        VivariumStepPolicy Policy,
        string Path);

    private abstract record YamlValue
    {
        public string AsScalar(string path) => this is YamlScalar scalar
            ? scalar.Value
            : throw Error(path, "expected a scalar value");

        public YamlMapping AsMapping(string path) => this as YamlMapping
            ?? throw Error(path, "expected a mapping");

        public YamlSequence AsSequence(string path) => this as YamlSequence
            ?? throw Error(path, "expected a sequence");
    }

    private sealed record YamlScalar(string Value) : YamlValue;

    private sealed record YamlMapping(IReadOnlyList<YamlEntry> Entries) : YamlValue;

    private sealed record YamlSequence(IReadOnlyList<YamlValue> Values) : YamlValue;

    private sealed record YamlEntry(YamlValue Key, YamlValue Value);
}
