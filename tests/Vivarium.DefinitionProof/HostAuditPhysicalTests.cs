using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class HostAuditPhysicalTests
{
    [Test]
    public async Task Account_mutation_and_shared_bounded_audit_commit_or_rollback_together_and_history_is_append_only()
    {
        await using var postgres = new OwnedPostgres();
        await postgres.StartAsync();
        VivariumDbContext Open() => new(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        var userId = Guid.NewGuid();
        var context = ManagementRequestContext.System("owned-r0-audit");
        AuditEventDraft Event() => AuditEventDraft.Create(context, DateTimeOffset.UtcNow,
            "account.update", "user", userId.ToString());
        await using (var database = Open())
        {
            await PostgresSchema.MigrateAsync(database);
            database.Users.Add(new VivariumUser { Id = userId, UserName = "owned-user", DisplayName = "before" });
            await database.SaveChangesAsync();
            await using var transaction = await database.Database.BeginTransactionAsync();
            (await database.Users.SingleAsync(user => user.Id == userId)).DisplayName = "rolled-back";
            new PostgresAuditStore(database).Stage(Event());
            await database.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        string eventId;
        await using (var database = Open())
        {
            var user = await database.Users.SingleAsync(user => user.Id == userId);
            Assert.That(user.DisplayName, Is.EqualTo("before"));
            Assert.That(await database.AuditEvents.CountAsync(), Is.Zero);
            await using var transaction = await database.Database.BeginTransactionAsync();
            user.DisplayName = "committed";
            var draft = Event(); eventId = draft.AuditEventId;
            new PostgresAuditStore(database).Stage(draft);
            await database.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using (var database = Open())
        {
            Assert.That((await database.Users.SingleAsync(user => user.Id == userId)).DisplayName, Is.EqualTo("committed"));
            var audit = await database.AuditEvents.SingleAsync();
            Assert.Multiple(() =>
            {
                Assert.That(audit.Id, Is.EqualTo(eventId));
                Assert.That(audit.ActorId, Is.EqualTo(context.Principal.ActorId));
                Assert.That(audit.CorrelationId, Is.EqualTo(context.CorrelationId));
                Assert.That(audit.TargetId, Is.EqualTo(userId.ToString()));
                Assert.That(audit.Outcome, Is.EqualTo("SUCCEEDED"));
            });
            var invalid = Event() with { Details = new Dictionary<string, string> { ["password"] = "owned-synthetic-value" } };
            Assert.Throws<ArgumentException>(() => new PostgresAuditStore(database).Stage(invalid));
            await database.SaveChangesAsync();
            Assert.That(await database.AuditEvents.CountAsync(), Is.EqualTo(1));
            var update = Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync(
                "UPDATE \"AuditEvents\" SET \"Outcome\" = 'DENIED'"));
            Assert.That(update!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
            var delete = Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync("DELETE FROM \"AuditEvents\""));
            Assert.That(delete!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
            var truncate = Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync("TRUNCATE \"AuditEvents\""));
            Assert.That(truncate!.SqlState, Is.EqualTo(PostgresErrorCodes.CheckViolation));
        }
    }
}
