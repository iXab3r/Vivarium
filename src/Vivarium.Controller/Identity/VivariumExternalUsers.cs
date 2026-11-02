using Vivarium.Controller.Persistence;
using Vivarium.Controller.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vivarium.Controller.Identity;

/// <summary>Atomically provision a passwordless user plus stock issuer/subject login; never link by email.</summary>
public sealed class VivariumExternalUsers(VivariumDbContext database, UserManager<VivariumUser> users)
{
    /// <summary>Resolve an already validated trusted binding, or create it with no password, roles or private rights.</summary>
    public async Task<VivariumUser> FindOrCreateAsync(string issuer, string subject, string? displayName,
        CancellationToken cancellationToken)
    {
        var existing = await users.FindByLoginAsync(issuer, subject);
        if (existing is not null) return existing;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var id = Guid.NewGuid();
            var user = new VivariumUser
            {
                Id = id,
                UserName = "user-" + id.ToString("N"),
                DisplayName = displayName is { Length: > 256 } ? displayName[..256] : displayName
            };
            Require(await users.CreateAsync(user));
            var login = await users.AddLoginAsync(user, new UserLoginInfo(issuer, subject, issuer));
            if (!login.Succeeded && login.Errors.Any(error => error.Code == "LoginAlreadyAssociated"))
            {
                await transaction.RollbackAsync(cancellationToken);
                database.ChangeTracker.Clear();
                return await users.FindByLoginAsync(issuer, subject)
                    ?? throw new InvalidOperationException("External login could not be resolved.");
            }
            Require(login);
            await transaction.CommitAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            return await users.FindByLoginAsync(issuer, subject)
                ?? throw new InvalidOperationException("External login could not be resolved.");
        }
    }

    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("External login could not be created.");
    }
}
