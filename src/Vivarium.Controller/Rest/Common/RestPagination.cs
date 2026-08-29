using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vivarium.Controller.Rest.Common;

public static class RestPagination
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static int ParseLimit(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var supplied = request.Query["limit"];
        if (supplied.Count == 0)
        {
            return DefaultLimit;
        }

        if (supplied.Count != 1 ||
            !int.TryParse(supplied[0], NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 1 or > MaxLimit)
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "invalid_limit",
                "The page limit is invalid",
                $"The limit query parameter must be an integer from 1 through {MaxLimit}.",
                errors:
                [
                    new RestProblemError("limit", "out_of_range", $"Choose a value from 1 through {MaxLimit}."),
                ]);
        }

        return limit;
    }
}

public static class RestQueryFingerprint
{
    private static readonly HashSet<string> StandardExcludedNames =
        new(StringComparer.OrdinalIgnoreCase) { "cursor", "limit", "sort" };

    public static string Create(HttpRequest request, params string[] excludedNames)
    {
        ArgumentNullException.ThrowIfNull(request);
        var excluded = new HashSet<string>(StandardExcludedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in excludedNames)
        {
            excluded.Add(name);
        }

        var canonical = request.Query
            .Where(pair => !excluded.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => pair.Value
                .Order(StringComparer.Ordinal)
                .Select(value => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value ?? string.Empty)}"));
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("&", canonical))));
    }
}
