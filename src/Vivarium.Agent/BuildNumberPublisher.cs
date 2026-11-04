using System.Text;
using Google.Protobuf;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;

namespace Vivarium.Agent;

/// <summary>Bounded stdout framing; registered values are masked after decoding and before any metadata leaves.</summary>
internal sealed class BuildNumberPublisher(string workload, IReadOnlyList<ByteString> secrets)
{
    private const string Prefix = "##teamcity[buildNumber '";
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder line = new();
    private readonly string[] masks = secrets.Select(s => s.ToStringUtf8()).Distinct(StringComparer.Ordinal)
        .OrderByDescending(s => s.Length).ToArray();
    private readonly List<BuildNumberChanged> changes = [];
    private bool discard;
    internal IReadOnlyList<BuildNumberChanged> Changes => changes;

    internal BuildNumberChanged[] Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 65536) throw new InvalidDataException("Number input chunk exceeds its bound.");
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, characters, flush: false);
        var added = new List<BuildNumberChanged>();
        foreach (var character in characters.AsSpan(0, count))
        {
            if (character == '\n') { if (FinishLine() is { } value) added.Add(value); continue; }
            if (discard) continue;
            if (line.Length == 65536)
            {
                if (line.ToString().StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Number message exceeds its line bound.");
                line.Clear(); discard = true; continue;
            }
            line.Append(character);
        }
        return added.ToArray();
    }

    internal BuildNumberChanged? FinishStep()
    {
        var final = new char[4]; var count = decoder.GetChars([], final, flush: true);
        line.Append(final, 0, count); var value = FinishLine(); decoder.Reset(); return value;
    }

    private BuildNumberChanged? FinishLine()
    {
        var text = line.ToString().TrimEnd('\r'); line.Clear();
        if (discard) { discard = false; return null; }
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        if (!text.EndsWith("']", StringComparison.Ordinal)) throw new InvalidDataException("Number message is malformed.");
        var value = text[Prefix.Length..^2]; var decoded = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '|') { decoded.Append(value[index]); continue; }
            if (++index == value.Length) throw new InvalidDataException("Number escape is incomplete.");
            decoded.Append(value[index] switch { '|' => '|', '\'' => '\'', '[' => '[', ']' => ']', 'n' => '\n', 'r' => '\r',
                _ => throw new InvalidDataException("Number escape is outside the selected profile.") });
        }
        var masked = decoded.ToString();
        foreach (var secret in masks) masked = masked.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        var change = new BuildNumberChanged { BuildId = workload, Sequence = checked((uint)changes.Count + 1), Value = masked };
        BuildNumberProfile.Validate(change, workload); changes.Add(change); return change;
    }
}
