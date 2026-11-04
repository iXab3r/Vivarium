using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

internal static class RuntimeResultReplayScenario
{
    internal static async Task RejectConflictAsync(IServiceProvider services, long priorBuild)
    {
        var factory = services.GetRequiredService<IDbContextFactory<VivariumDbContext>>();
        await using var database = await factory.CreateDbContextAsync();
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == priorBuild);
        var receipt = await database.RuntimeBuildResults.AsNoTracking().SingleAsync(r => r.BuildId == priorBuild);
        var before = await database.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == execution.AgentId);
        Assert.That(services.GetRequiredService<RuntimeAgentSessions>().TryGetCurrent(execution.AgentId, out var connection), Is.True);
        var conflict = BuildResult.Parser.ParseFrom(receipt.Evidence); conflict.SessionId = connection!.Handle.SessionId;
        conflict.StatusText = "Owned conflicting retained result";
        Assert.ThrowsAsync<InvalidDataException>(async () => await services.GetRequiredService<RuntimeBuildCoordinator>()
            .ReceiveAsync(connection, new() { Result = conflict }, CancellationToken.None));
        var after = await database.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == execution.AgentId);
        Assert.That(after.Quarantined, Is.True);
        Assert.That(after.HealthReason, Is.EqualTo("conflicting_build_result"));
        Assert.That(after.WorkloadFence, Is.EqualTo(before.WorkloadFence));
        var retained = await database.RuntimeBuildResults.AsNoTracking().SingleAsync(r => r.BuildId == priorBuild);
        Assert.That(retained.Evidence, Is.EqualTo(receipt.Evidence));
        Assert.That(retained.SemanticHash, Is.EqualTo(receipt.SemanticHash));
        Assert.That(await database.AuditEvents.CountAsync(a => a.Action == "build.result.conflict"
            && a.TargetId == priorBuild.ToString()), Is.EqualTo(1));
    }

    internal static async Task VerifyAsync(IServiceProvider services, long priorBuild)
    {
        var factory = services.GetRequiredService<IDbContextFactory<VivariumDbContext>>();
        await using var database = await factory.CreateDbContextAsync();
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == priorBuild);
        var receipt = await database.RuntimeBuildResults.AsNoTracking().SingleAsync(r => r.BuildId == priorBuild);
        var before = await database.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == execution.AgentId);
        var auditCount = await database.AuditEvents.CountAsync();
        Assert.That(execution.ReleasedAt, Is.Not.Null);
        Assert.That(services.GetRequiredService<RuntimeAgentSessions>().TryGetCurrent(execution.AgentId, out var connection), Is.True);
        var repeated = BuildResult.Parser.ParseFrom(receipt.Evidence); repeated.SessionId = connection!.Handle.SessionId;
        var coordinator = services.GetRequiredService<RuntimeBuildCoordinator>();
        var stale = repeated.Clone(); stale.SessionId = "owned-stale-session";
        Assert.ThrowsAsync<InvalidDataException>(async () => await coordinator.ReceiveAsync(connection, new() { Result = stale }, CancellationToken.None));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Exercise the production receipt handler under the real authenticated connection.
            // The live adapter owns ACK delivery; this is component evidence, not a wire oracle.
            await coordinator.ReceiveAsync(connection, new() { Result = repeated }, CancellationToken.None);
        }
        var after = await database.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == execution.AgentId);
        var retained = await database.RuntimeBuildResults.AsNoTracking().SingleAsync(r => r.BuildId == priorBuild);
        var retainedExecution = await database.RuntimeBuildExecutions.AsNoTracking().SingleAsync(e => e.BuildId == priorBuild);
        Assert.That(after.CurrentWorkloadId, Is.EqualTo(before.CurrentWorkloadId));
        Assert.That(after.WorkloadFence, Is.EqualTo(before.WorkloadFence));
        Assert.That(after.Quarantined, Is.False);
        Assert.That(retained.Evidence, Is.EqualTo(receipt.Evidence));
        Assert.That(retainedExecution.ReleasedAt, Is.EqualTo(execution.ReleasedAt));
        Assert.That(await database.AuditEvents.CountAsync(), Is.EqualTo(auditCount));
    }
}
