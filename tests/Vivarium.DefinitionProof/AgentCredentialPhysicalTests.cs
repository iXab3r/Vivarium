using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.DefinitionProof;

[NonParallelizable]
public sealed class AgentCredentialPhysicalTests
{
    [Test]
    public async Task Agent_enrollment_delivery_and_bearer_proof_keep_one_identity_and_commit_with_audit()
    {
        await using var postgres = new OwnedPostgres(); await postgres.StartAsync();
        var factory = new PooledDbContextFactory<VivariumDbContext>(new DbContextOptionsBuilder<VivariumDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var ownerId = Guid.NewGuid(); var outsiderId = Guid.NewGuid();
        ManagementRequestContext Actor(Guid id) => new(new("user", id.ToString(), "physical-framework-test", null),
            Guid.NewGuid().ToString(), null, "owned-agent-credentials");
        await using (var database = await factory.CreateDbContextAsync())
        {
            await PostgresSchema.MigrateAsync(database);
            database.Users.AddRange(new VivariumUser { Id = ownerId, UserName = "agent-owner", IsEnabled = true },
                new VivariumUser { Id = outsiderId, UserName = "project-only-user", IsEnabled = true });
            database.UserRoleGrants.Add(new UserRoleGrant { UserId = ownerId, RoleId = "SYSTEM_ADMIN", Scope = "g" });
            await database.SaveChangesAsync();
        }
        var parent = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts/r0-agents"));
        var keys = Path.Combine(parent, "credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keys);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        IDataProtectionProvider Protection() => DataProtectionProvider.Create(new DirectoryInfo(keys),
            builder => builder.SetApplicationName("Vivarium"));
        var clock = new CredentialClock();
        try
        {
            var service = new AgentCredentials(factory, configuration, Protection(), clock);
            Assert.ThrowsAsync<AccessPermissionException>(() => service.IssueEnrollmentAsync(Actor(outsiderId)));
            var issued = await service.IssueEnrollmentAsync(Actor(ownerId));
            Assert.That(issued.Token.Length == 48 && issued.Token.All(Uri.IsHexDigit), Is.True);
            var agentId = Guid.NewGuid().ToString();
            Hello Hello(string? auth = null, string? enroll = null, string? id = null) => new()
            {
                AgentId = id ?? agentId, SessionId = Guid.NewGuid().ToString(),
                AuthToken = auth ?? "", EnrollToken = enroll ?? "",
            };
            var first = await service.AdmitAsync(Hello(enroll: issued.Token));
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.Admission.Authorization, Is.EqualTo(AgentAuth.Unauthorized));
            Assert.That(first.BearerProven, Is.False);
            Assert.That(first.Admission.CredentialGeneration, Is.Zero);
            Assert.That(await service.AdmitAsync(Hello(enroll: issued.Token, id: Guid.NewGuid().ToString())), Is.Null);
            int numericId;
            await using (var database = await factory.CreateDbContextAsync())
            {
                var row = await database.AgentRegistrations.SingleAsync(); numericId = row.TeamCityId;
                Assert.That(numericId, Is.Positive);
                Assert.That(row.DesiredRevision, Is.Null, "Enrollment cannot invent a Git enablement revision.");
                var stored = await database.AgentEnrollments.SingleAsync();
                Assert.That(stored.ClaimedAgentId, Is.EqualTo(agentId));
                Assert.That(stored.TokenHash == issued.Token, Is.False);
            }
            var denied = await service.SetAuthorizedAsync(Actor(outsiderId), agentId, true);
            Assert.That(denied.Outcome, Is.EqualTo(AccessMutationResult.Denied));
            var granted = await service.SetAuthorizedAsync(Actor(ownerId), agentId, true);
            Assert.That(granted.Outcome, Is.EqualTo(AccessMutationResult.Applied));
            Assert.That(granted.Token is not null && granted.Token.Length == 48, Is.True);
            Assert.That(granted.Generation, Is.EqualTo(1));
            await using (var database = await factory.CreateDbContextAsync())
            {
                var row = await database.AgentRegistrations.SingleAsync();
                Assert.That(row.ProtectedPendingAuthToken is not null && !row.ProtectedPendingAuthToken.Contains(granted.Token!), Is.True);
                Assert.That(row.AuthTokenHash == granted.Token, Is.False);
            }
            // A claimed delivery channel survives original ticket expiry and a cold key-ring reopen.
            clock.Advance(TimeSpan.FromHours(25));
            var cold = new AgentCredentials(factory, configuration, Protection(), clock);
            var retry = await cold.AdmitAsync(Hello(enroll: issued.Token));
            Assert.That(retry is not null && retry.Admission.AuthTokenToDeliver == granted.Token, Is.True);
            Assert.That(retry!.BearerProven, Is.False);
            Assert.That(retry.Admission.Authorization, Is.EqualTo(AgentAuth.Unauthorized));
            var expired = await cold.IssueEnrollmentAsync(Actor(ownerId));
            clock.Advance(TimeSpan.FromHours(25));
            Assert.That(await cold.AdmitAsync(Hello(enroll: expired.Token, id: Guid.NewGuid().ToString())), Is.Null);

            await using (var database = await factory.CreateDbContextAsync())
                await database.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION fixture_agent_audit_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                      IF NEW."Action" = 'agent.authorize' AND NEW."Outcome" = 'SUCCEEDED' THEN
                        RAISE EXCEPTION 'owned agent authorization audit fault';
                      END IF;
                      RETURN NEW;
                    END $$;
                    CREATE TRIGGER fixture_agent_audit_failure BEFORE INSERT ON "AuditEvents"
                      FOR EACH ROW EXECUTE FUNCTION fixture_agent_audit_failure();
                    """);
            Assert.ThrowsAsync<DbUpdateException>(() => cold.SetAuthorizedAsync(Actor(ownerId), agentId, false));
            Assert.That((await cold.ResolveAsync(granted.Token!))?.Authorized, Is.True);
            await using (var database = await factory.CreateDbContextAsync())
                await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_agent_audit_failure ON \"AuditEvents\"; DROP FUNCTION fixture_agent_audit_failure();");
            var proven = await cold.AdmitAsync(Hello(auth: granted.Token));
            Assert.That(proven!.BearerProven, Is.True);
            Assert.That(proven.Admission.Authorization, Is.EqualTo(AgentAuth.Authorized));
            Assert.That(await cold.AdmitAsync(Hello(enroll: issued.Token)), Is.Null,
                "The original enrollment channel must be consumed after the bearer is proven.");
            var blocked = await cold.SetAuthorizedAsync(Actor(ownerId), agentId, false);
            Assert.That(blocked.Outcome, Is.EqualTo(AccessMutationResult.Applied));
            Assert.That((await cold.ResolveAsync(granted.Token!))?.Authorized, Is.False,
                "Unauthorization blocks new work without replacing the existing completion credential.");
            var restored = await cold.SetAuthorizedAsync(Actor(ownerId), agentId, true);
            Assert.That(restored.Generation, Is.EqualTo(granted.Generation));
            Assert.That(restored.Token, Is.Null);
            await using (var database = await factory.CreateDbContextAsync())
            {
                var row = await database.AgentRegistrations.SingleAsync();
                Assert.That(row.TeamCityId, Is.EqualTo(numericId));
                Assert.That(row.ProtectedPendingAuthToken, Is.Null);
                Assert.That(row.EnrollmentTokenHash, Is.Null);
                var details = await database.AuditEvents.Select(a => a.DetailsJson).ToArrayAsync();
                Assert.That(details.Any(d => d.Contains(issued.Token) || d.Contains(granted.Token!)), Is.False);
            }
        }
        finally
        {
            var target = Path.GetFullPath(keys);
            if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target), "^credentials-[a-f0-9]{32}$"))
                throw new InvalidDataException("Owned credential fixture cleanup target differs.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    private sealed class CredentialClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan amount) => now += amount;
    }
}
