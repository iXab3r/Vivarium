using System.Text.Json;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Vivarium.Controller.Tokens;

/// <summary>Stores display metadata on the framework token entry, after the framework generates it.</summary>
internal sealed class PersistTokenLabel(IOpenIddictTokenManager tokens) : IOpenIddictServerHandler<ProcessSignInContext>
{
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.ClientId != VivariumTokenProfile.Client || context.AccessTokenPrincipal is null) return;
        var id = context.AccessTokenPrincipal.GetTokenId();
        if (id is null) throw new InvalidOperationException("The issued access token has no persistent entry.");
        var entry = await tokens.FindByIdAsync(id, context.CancellationToken)
            ?? throw new InvalidOperationException("The issued access token entry is unavailable.");
        var descriptor = new OpenIddictTokenDescriptor();
        await tokens.PopulateAsync(descriptor, entry, context.CancellationToken);
        descriptor.Properties[VivariumTokenProfile.Label] = JsonSerializer.SerializeToElement(
            context.Principal?.GetClaim(VivariumTokenProfile.CodeLabel) ?? "");
        await tokens.UpdateAsync(entry, descriptor, context.CancellationToken);
    }
}
