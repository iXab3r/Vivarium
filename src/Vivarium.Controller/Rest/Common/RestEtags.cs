using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vivarium.Controller.Rest.Common;

public static class RestEtags
{
    public static string FromRevision(string revision) => FromBytes(Encoding.UTF8.GetBytes(revision));

    public static string FromValue<T>(T value) =>
        FromBytes(JsonSerializer.SerializeToUtf8Bytes(value, RestJson.SerializerOptions));

    public static IResult ApplyConditionalGet<T>(HttpContext context, string etag, T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateEtag(etag);
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, no-cache";
        return Matches(context.Request, etag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Json(value);
    }

    public static bool Matches(HttpRequest request, string etag)
    {
        ValidateEtag(etag);
        var candidates = request.Headers.IfNoneMatch;
        return candidates.Any(header => header!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => candidate == "*" ||
                string.Equals(NormalizeWeak(candidate), NormalizeWeak(etag), StringComparison.Ordinal)));
    }

    private static string FromBytes(ReadOnlySpan<byte> value) =>
        $"\"{Convert.ToHexStringLower(SHA256.HashData(value))}\"";

    private static string NormalizeWeak(string value) =>
        value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private static void ValidateEtag(string etag)
    {
        if (string.IsNullOrWhiteSpace(etag) || etag[0] != '"' || etag[^1] != '"')
        {
            throw new ArgumentException("ETag must be a quoted entity tag", nameof(etag));
        }
    }
}
