using System.Text;
using Vivarium.Contracts;

namespace Vivarium.Agent;

/// <summary>The selected literal-file publishArtifacts profile; output remains byte-for-byte intact.</summary>
internal sealed class LiteralArtifactPublisher(bool allowRules = false)
{
    private const string Prefix = "##teamcity[publishArtifacts '";
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder line = new();
    private readonly HashSet<string> paths = new(StringComparer.Ordinal);
    private bool discardLine;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 65536) throw new InvalidDataException("Publisher input exceeds its chunk bound.");
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, characters, flush: false);
        foreach (var character in characters.AsSpan(0, count))
        {
            if (character == '\n') { FinishLine(); continue; }
            if (discardLine) continue;
            if (line.Length == 65536)
            {
                if (line.ToString().StartsWith(Prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Artifact publication exceeds its line bound.");
                line.Clear(); discardLine = true; continue;
            }
            line.Append(character);
        }
    }

    internal void FinishStep()
    {
        var final = new char[4]; var count = decoder.GetChars([], final, flush: true);
        line.Append(final, 0, count); FinishLine(); decoder.Reset();
    }

    private void FinishLine()
    {
        var message = line.ToString().TrimEnd('\r'); line.Clear();
        if (discardLine) { discardLine = false; return; }
        if (!message.StartsWith(Prefix, StringComparison.Ordinal)) return;
        if (!message.EndsWith("']", StringComparison.Ordinal)) throw new InvalidDataException("Artifact publication is malformed.");
        var value = message[Prefix.Length..^2]; var decoded = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '|') { decoded.Append(character); continue; }
            if (++index == value.Length) throw new InvalidDataException("Artifact publication escape is incomplete.");
            decoded.Append(value[index] switch
            {
                '|' => '|', '\'' => '\'', '[' => '[', ']' => ']', 'n' => '\n', 'r' => '\r',
                _ => throw new InvalidDataException("Artifact publication escape is not supported."),
            });
        }
        if (!allowRules)
        {
            var literal = decoded.ToString().Replace('\\', '/');
            LiteralArtifactPath.Validate(literal);
            if (paths.Count >= 128 && !paths.Contains(literal)) throw new InvalidDataException("Artifact publication exceeds its file bound.");
            paths.Add(literal);
            return;
        }
        if (decoded.Length == 0) throw new InvalidDataException("Artifact publication is empty.");
        foreach (var ruleText in decoded.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = ruleText.Replace('\\', '/');
            _ = ArtifactRuleProfile.Parse(path);
            if (paths.Count >= 128 && !paths.Contains(path)) throw new InvalidDataException("Artifact publication exceeds its rule bound.");
            paths.Add(path);
        }
    }

    internal string[] Files(string workdir)
    {
        if (allowRules) return PublishedFiles(workdir).Select(f => f.Source).Distinct(StringComparer.Ordinal).ToArray();
        var result = new List<string>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            var current = Path.GetFullPath(workdir);
            foreach (var part in path.Split('/'))
            {
                current = Path.Combine(current, part);
                if (!File.Exists(current) && !Directory.Exists(current)) break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Artifact publication cannot follow links.");
            }
            if (Directory.Exists(current)) throw new InvalidDataException("The selected publisher requires literal files.");
            if (File.Exists(current)) result.Add(path);
        }
        return result.ToArray();
    }

    internal (string Source, string Path)[] PublishedFiles(string workdir, CancellationToken token = default)
    {
        if (!allowRules) return Files(workdir).Select(p => (p, p)).ToArray();
        var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = 0;
        foreach (var text in paths.Order(StringComparer.Ordinal))
        {
            var rule = ArtifactRuleProfile.Parse(text);
            var slash = rule.Pattern.LastIndexOf('/');
            var prefix = slash < 0 ? "" : rule.Pattern[..(slash + 1)];
            var directory = Path.Combine(workdir, prefix.Replace('/', Path.DirectorySeparatorChar));
            var current = workdir;
            foreach (var part in prefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Directory.Exists(current)) break;
                RejectLink(current);
            }
            if (!Directory.Exists(directory)) continue;
            foreach (var source in Visit(directory, prefix, rule.Pattern.EndsWith("**", StringComparison.Ordinal), 0))
            {
                var target = rule.Map(source, OperatingSystem.IsWindows());
                if (target is null) continue;
                RejectLink(Path.Combine(workdir, source));
                if (published.TryGetValue(target, out var prior) && prior != source)
                    throw new InvalidDataException("Artifact publication paths conflict.");
                if (published.Count >= 128 && !published.ContainsKey(target)) throw new InvalidDataException("Artifact publication exceeds its file bound.");
                published[target] = source;
            }
        }
        return published.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Value, p.Key)).ToArray();

        IEnumerable<string> Visit(string directory, string relative, bool recursive, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Artifact discovery exceeds its directory bound.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 100000) throw new InvalidDataException("Artifact discovery exceeds its entry bound.");
                var attributes = File.GetAttributes(entry);
                var path = relative + Path.GetFileName(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!recursive) continue;
                    RejectLink(entry);
                    foreach (var nested in Visit(entry, path + '/', recursive, depth + 1)) yield return nested;
                }
                else
                {
                    yield return path;
                }
            }
        }
        static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Artifact publication cannot follow links.");
        }
    }

}
