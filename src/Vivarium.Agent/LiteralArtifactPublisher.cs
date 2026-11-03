using System.Text;
using Vivarium.Contracts;

namespace Vivarium.Agent;

/// <summary>The selected literal-file publishArtifacts profile; output remains byte-for-byte intact.</summary>
internal sealed class LiteralArtifactPublisher
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
        var path = decoded.ToString().Replace('\\', '/');
        LiteralArtifactPath.Validate(path);
        if (paths.Count >= 128 && !paths.Contains(path)) throw new InvalidDataException("Artifact publication exceeds its file bound.");
        paths.Add(path);
    }

    internal string[] Files(string workdir)
    {
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

}
