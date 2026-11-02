using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Vivarium.Controller.Tokens;

/// <summary>One EF transaction around framework entry creation, metadata and audit, before any credential disclosure.</summary>
internal sealed class TokenIssuanceTransaction(VivariumDbContext database, PostgresAuditStore audits,
    RuntimeRequestContext requestContext, TimeProvider time) : IAsyncDisposable
{
    private IDbContextTransaction? transaction;

    public async Task BeginAsync(CancellationToken token) => transaction = await database.Database.BeginTransactionAsync(token);

    public async Task CommitAsync(ProcessSignInContext context)
    {
        if (transaction is null) throw new InvalidOperationException("Framework issuance transaction is absent.");
        var accessToken = context.AccessTokenPrincipal;
        var principal = accessToken ?? context.AuthorizationCodePrincipal
            ?? throw new InvalidOperationException("Framework credential principal is absent.");
        if (!Guid.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), out var userId)
            || principal.GetTokenId() is not { } id)
            throw new InvalidOperationException("Framework credential identity is absent.");
        audits.Stage(AuditEventDraft.Create(requestContext.ForUser(userId,
            accessToken is null ? "identity-cookie" : "oauth-code", "my-keys"), time.GetUtcNow(),
            accessToken is null ? "credential.authorize_code" : "credential.issue", "oauth-token", id));
        await database.SaveChangesAsync(context.CancellationToken);
        await transaction.CommitAsync(context.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (transaction is not null) await transaction.DisposeAsync();
    }
}

internal sealed class BeginTokenIssuance(TokenIssuanceTransaction transaction) : IOpenIddictServerHandler<ProcessSignInContext>
{
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.ClientId == VivariumTokenProfile.Client)
            await transaction.BeginAsync(context.CancellationToken);
    }
}

internal sealed class CommitTokenIssuance(TokenIssuanceTransaction transaction) : IOpenIddictServerHandler<ProcessSignInContext>
{
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.ClientId == VivariumTokenProfile.Client) await transaction.CommitAsync(context);
    }
}
