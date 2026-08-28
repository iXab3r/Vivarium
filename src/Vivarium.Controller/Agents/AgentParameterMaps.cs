using System.Collections.ObjectModel;

namespace Vivarium.Controller.Agents;

internal static class AgentParameterMaps
{
    public static IReadOnlyDictionary<string, string> Normalize(
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            ordered.Add(key, value);
        }

        return new ReadOnlyDictionary<string, string>(ordered);
    }

    public static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> reported,
        IReadOnlyDictionary<string, string> custom)
    {
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in reported)
        {
            ordered.Add(key, value);
        }

        foreach (var (key, value) in custom)
        {
            if (!ordered.TryAdd(key, value))
            {
                throw new InvalidDataException(
                    $"custom agent parameter '{key}' conflicts with a reported parameter");
            }
        }

        return new ReadOnlyDictionary<string, string>(ordered);
    }

    public static (string Key, string Value) ValidateCustom(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var normalizedKey = key.Trim();
        var normalizedValue = value.Trim();
        if (normalizedKey.Length is 0 or > 128 ||
            !normalizedKey.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "custom parameter key must be 1-128 ASCII letters, digits, '.', '_' or '-'",
                nameof(key));
        }

        if (string.Equals(normalizedKey, "name", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "'name' is reserved for the agent display-name selector",
                nameof(key));
        }

        if (normalizedValue.Length is 0 or > 1024 ||
            !normalizedValue.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or '@' or '/' or ':' or '+'))
        {
            throw new ArgumentException(
                "custom parameter value must be 1-1024 selector-safe ASCII characters",
                nameof(value));
        }

        return (normalizedKey, normalizedValue);
    }

    public static string ValidateCustomKey(string key) => ValidateCustom(key, "value").Key;
}
