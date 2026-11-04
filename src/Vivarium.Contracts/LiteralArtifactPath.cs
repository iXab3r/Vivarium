namespace Vivarium.Contracts;

/// <summary>Portable relative files in teamcity.artifact-publisher.literal.v1, not rule expressions.</summary>
public static class LiteralArtifactPath
{
    public static void Validate(string path)
    {
        if (path.Length is < 1 or > 1024 || path.StartsWith('/') || path.Contains('\\')
            || path.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '!' or '[' or ']' or '|' or '>' or '<' or '"')
            || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || IsDevice(p.Split('.')[0])))
            throw new InvalidDataException("The selected publisher requires portable relative literal paths.");
    }

    private static bool IsDevice(string part) => part.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || part.Equals("PRN", StringComparison.OrdinalIgnoreCase) || part.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || part.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || part.Length == 4 && (part.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || part.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && part[3] is >= '1' and <= '9';
}
