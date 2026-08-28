using Grpc.Core;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Management;

internal sealed class ControlPlaneAuthorizer(TokenStore tokens)
{
    public async Task DemandAsync(BearerScope required, ServerCallContext context)
    {
        var header = context.RequestHeaders.FirstOrDefault(entry =>
            entry.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase))?.Value;
        const string prefix = "Bearer ";
        if (header == null ||
            !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            header.Length == prefix.Length)
        {
            throw Unauthenticated();
        }

        var scope = await tokens.ResolveBearerScopeAsync(header[prefix.Length..].Trim());
        if (scope == null)
        {
            throw Unauthenticated();
        }

        if (!Allows(scope.Value, required))
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                $"{required.ToString().ToLowerInvariant()} scope required"));
        }
    }

    private static bool Allows(BearerScope actual, BearerScope required) => required switch
    {
        BearerScope.Admin => actual == BearerScope.Admin,
        BearerScope.Submit => actual is BearerScope.Submit or BearerScope.Admin,
        BearerScope.Agent => actual is BearerScope.Agent or BearerScope.Submit or BearerScope.Admin,
        _ => false,
    };

    private static RpcException Unauthenticated() => new(new Status(
        StatusCode.Unauthenticated, "valid bearer token required"));
}
