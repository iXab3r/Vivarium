using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Common;

public sealed class RestCursorCodec
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int MaxCursorLength = 8 * 1024;
    private const int MaxPositionLength = 2 * 1024;
    private readonly IDataProtector protector;
    private readonly TimeProvider timeProvider;

    public RestCursorCodec(IDataProtectionProvider dataProtection, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(timeProvider);
        protector = dataProtection.CreateProtector("Vivarium.Rest.Cursor.v1");
        this.timeProvider = timeProvider;
    }

    public string Encode(
        string position,
        ManagementPrincipal principal,
        string resource,
        string queryFingerprint,
        string sort)
    {
        ValidateScope(position, principal, resource, queryFingerprint, sort);
        var payload = new CursorPayload(
            Version: 1,
            Principal: PrincipalIdentity(principal),
            Resource: resource,
            QueryFingerprint: queryFingerprint,
            Sort: sort,
            Position: position,
            IssuedUnixMilliseconds: timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return protector.Protect(JsonSerializer.Serialize(payload, RestJson.SerializerOptions));
    }

    public string Decode(
        string cursor,
        ManagementPrincipal principal,
        string resource,
        string queryFingerprint,
        string sort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ValidateScope(string.Empty, principal, resource, queryFingerprint, sort);
        if (cursor.Length > MaxCursorLength)
        {
            throw InvalidCursor();
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(
                protector.Unprotect(cursor), RestJson.SerializerOptions);
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException or FormatException)
        {
            throw InvalidCursor(exception);
        }

        if (payload is null || payload.Version != 1 ||
            payload.Position.Length > MaxPositionLength)
        {
            throw InvalidCursor();
        }

        var age = timeProvider.GetUtcNow() -
            DateTimeOffset.FromUnixTimeMilliseconds(payload.IssuedUnixMilliseconds);
        if (age < TimeSpan.Zero || age > Lifetime)
        {
            throw new RestApiException(
                StatusCodes.Status410Gone,
                "cursor_expired",
                "The pagination cursor has expired",
                "Restart the collection request without a cursor.");
        }

        if (!string.Equals(payload.Principal, PrincipalIdentity(principal), StringComparison.Ordinal) ||
            !string.Equals(payload.Resource, resource, StringComparison.Ordinal) ||
            !string.Equals(payload.QueryFingerprint, queryFingerprint, StringComparison.Ordinal) ||
            !string.Equals(payload.Sort, sort, StringComparison.Ordinal))
        {
            throw new RestApiException(
                StatusCodes.Status400BadRequest,
                "cursor_context_mismatch",
                "The pagination cursor does not match this request",
                "Use the cursor with the same authenticated principal, resource, filters, and sort order.");
        }

        return payload.Position;
    }

    private static string PrincipalIdentity(ManagementPrincipal principal) =>
        $"{principal.ActorType}\n{principal.ActorId}";

    private static void ValidateScope(
        string position,
        ManagementPrincipal principal,
        string resource,
        string queryFingerprint,
        string sort)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        ArgumentNullException.ThrowIfNull(sort);
        if (position.Length > MaxPositionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
    }

    private static RestApiException InvalidCursor(Exception? innerException = null) =>
        new(
            StatusCodes.Status400BadRequest,
            "invalid_cursor",
            "The pagination cursor is invalid",
            "Restart the collection request without a cursor.",
            innerException: innerException);

    private sealed record CursorPayload(
        int Version,
        string Principal,
        string Resource,
        string QueryFingerprint,
        string Sort,
        string Position,
        long IssuedUnixMilliseconds);
}
