using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;
using Vivarium.Definitions;

namespace Vivarium.Controller.Builds.Runtime;

internal sealed record RuntimeVcsAdmission(long BuildTypeId, Guid RevisionId, Guid EventId, string Version);

/// <summary>Git change observation feeds the existing admission/queue kernel; PG owns quiet periods.</summary>
internal sealed class RuntimeVcsTriggers(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration,
    RuntimeBuildService builds, TimeProvider time, ILogger<RuntimeVcsTriggers> logger)
{
    internal static string ProfileHash(Source source, Trigger trigger) =>
        IsolatedDefinitionEvaluator.Hash(JsonSerializer.Serialize(new { source, trigger }, DefinitionValidation.Json));

    internal async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await TickAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or IOException
                or ConfigurationRepositoryException or RestApiException or OperationCanceledException)
            { logger.LogWarning("VCS trigger observation is unavailable; durable pending changes are retained."); }
            await Task.Delay(TimeSpan.FromSeconds(5), time, token);
        }
    }

    internal async Task TickAsync(CancellationToken token)
    {
        CatalogSnapshot? snapshot;
        await using (var database = await databases.CreateDbContextAsync(token))
            snapshot = await new CatalogQueries(database, configuration, new CurrentAccess(database, configuration)).ActiveAsync(token);
        if (snapshot is null) return;
        await using (var database = await databases.CreateDbContextAsync(token))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await RuntimeBuildAccess.FenceQueueAsync(database, token);
            await database.Database.ExecuteSqlRawAsync("LOCK TABLE \"ActiveCatalogs\" IN SHARE MODE", token);
            var current = await new CatalogQueries(database, configuration, new CurrentAccess(database, configuration)).ActiveAsync(token);
            if (current?.Revision.Id != snapshot.Revision.Id) return;
            var active = snapshot.Definition.Projects.Where(p => p.Definition.Source is not null)
                .SelectMany(p => p.Definition.Configurations).Where(c => c.Triggers.Length != 0)
                .Select(c => snapshot.BuildTypeIds[c.Id]).ToArray();
            foreach (var withdrawn in await database.RuntimeVcsTriggerStates.Where(t => !active.Contains(t.BuildTypeId) && t.PendingId != null).ToArrayAsync(token))
                new PostgresAuditStore(database).Stage(AuditEventDraft.Create(ManagementRequestContext.System("vcs-trigger", withdrawn.PendingId.ToString()), time.GetUtcNow(),
                    "vcs-trigger.withdraw", "build-configuration", withdrawn.BuildTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    with { ResultRevision = snapshot.Revision.ControlCommit });
            await database.RuntimeVcsTriggerStates.Where(t => !active.Contains(t.BuildTypeId)).ExecuteDeleteAsync(token);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        // Sequential, time/output/disk-bounded observations own no queue/Agent fence during Git I/O.
        foreach (var project in snapshot.Definition.Projects)
        {
            if (project.Definition.Source is not { } source) continue;
            foreach (var config in project.Definition.Configurations.Where(c => c.Triggers.Length != 0))
            {
                try
                {
                    if (config.Triggers.Length != 1) continue; // No positive conformance claim for other profiles.
                    var trigger = config.Triggers[0];
                    try { RuntimeVcsChanges.Rules(trigger); RuntimeBuildSources.Profile(source); }
                    catch (InvalidDataException) { continue; }
                    var typeId = snapshot.BuildTypeIds[config.Id]; var hash = ProfileHash(source, trigger);
                    RuntimeVcsTriggerState? prior;
                    await using (var read = await databases.CreateDbContextAsync(token))
                        prior = await read.RuntimeVcsTriggerStates.AsNoTracking().SingleOrDefaultAsync(t => t.BuildTypeId == typeId, token);
                    var previous = prior?.ProfileHash == hash ? prior.ObservedVersion : null;
                    var observation = await RuntimeVcsChanges.ObserveAsync(source, previous, token);
                    RuntimeVcsAdmission? admission = null;
                    await using (var database = await databases.CreateDbContextAsync(token))
                    {
                        await using var transaction = await database.Database.BeginTransactionAsync(token);
                        await RuntimeBuildAccess.FenceQueueAsync(database, token);
                        await database.Database.ExecuteSqlRawAsync("LOCK TABLE \"ActiveCatalogs\" IN SHARE MODE", token);
                        var current = await new CatalogQueries(database, configuration, new CurrentAccess(database, configuration)).ActiveAsync(token);
                        if (current?.Revision.Id != snapshot.Revision.Id) continue;
                        var state = await database.RuntimeVcsTriggerStates.SingleOrDefaultAsync(t => t.BuildTypeId == typeId, token);
                        // Another observer committed while Git was outside the fence. Retry from that fact.
                        if (state?.ProfileHash != prior?.ProfileHash || state?.ObservedVersion != prior?.ObservedVersion) continue;
                        var now = time.GetUtcNow();
                        if (state is null)
                        {
                            state = new() { BuildTypeId = typeId }; database.RuntimeVcsTriggerStates.Add(state);
                        }
                        if (state.ProfileHash != hash)
                        {
                            state.ProfileHash = hash; state.PendingVersion = null; state.PendingId = null; state.DueAt = null;
                        }
                        state.RevisionId = snapshot.Revision.Id;
                        if (previous is not null && observation.Version != previous && RuntimeVcsChanges.Relevant(observation.Paths!, trigger))
                        {
                            state.PendingVersion = observation.Version; state.PendingId ??= Guid.NewGuid();
                            state.DueAt = now.AddSeconds(trigger.QuietSeconds);
                        }
                        else if (state.PendingId is not null && observation.Version != previous)
                        {
                            // Excluded-only commits do not extend quiet time; checkout still uses the latest head.
                            state.PendingVersion = observation.Version;
                        }
                        state.ObservedVersion = observation.Version; state.ObservedAt = now;
                        if (state.PendingVersion is { } pending)
                        {
                            // The captured used profile consumes completed successful manual coverage.
                            // Other failed/cancelled/manual variants remain unaccepted reference gaps.
                            var covered = await (from b in database.RuntimeBuilds
                                join i in database.RuntimeBuildInputs on b.Id equals i.BuildId
                                join s in database.RuntimeBuildSourceSelections on b.Id equals s.BuildId
                                where b.BuildTypeId == typeId && b.State == "FINISHED" && b.Status == "SUCCESS"
                                    && i.RevisionId == snapshot.Revision.Id && i.OverridesJson == "{}" && s.Version == pending
                                select b.Id).AnyAsync(token);
                            if (covered)
                            {
                                new PostgresAuditStore(database).Stage(AuditEventDraft.Create(ManagementRequestContext.System("vcs-trigger", state.PendingId.ToString()), now,
                                    "vcs-trigger.covered", "build-configuration", config.Id, details: new Dictionary<string, string> { ["version"] = pending })
                                    with { ResultRevision = snapshot.Revision.ControlCommit });
                                state.PendingVersion = null; state.PendingId = null; state.DueAt = null;
                            }
                            else if (state.DueAt <= now) admission = new(typeId, state.RevisionId, state.PendingId!.Value, pending);
                        }
                        if (previous != observation.Version)
                            new PostgresAuditStore(database).Stage(AuditEventDraft.Create(ManagementRequestContext.System("vcs-trigger"), now,
                                "vcs-trigger.observe", "build-configuration", config.Id, details: new Dictionary<string, string>
                                { ["version"] = observation.Version, ["pending"] = (state.PendingId is not null).ToString() })
                                with { ResultRevision = snapshot.Revision.ControlCommit });
                        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
                    }
                    if (admission is not null) await builds.SubmitVcsAsync(admission, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or IOException
                    or ConfigurationRepositoryException or RestApiException or OperationCanceledException)
                { logger.LogWarning("VCS trigger observation is unavailable; its durable pending changes are retained."); }
            }
        }
    }
}
