using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildAudit
{
    internal static async Task StageAsync(VivariumDbContext database, RuntimeBuild build, DateTimeOffset receivedAt,
        string action, AuditOutcome outcome = AuditOutcome.Succeeded, string reason = "", string? workload = null,
        IReadOnlyDictionary<string, string>? details = null, CancellationToken token = default)
    {
        var revision = await (from input in database.RuntimeBuildInputs
            join source in database.CatalogRevisions on input.RevisionId equals source.Id
            where input.BuildId == build.Id select source.ControlCommit).SingleAsync(token);
        var context = new ManagementRequestContext(ManagementPrincipal.System, build.CorrelationId, workload, "build-runtime");
        new PostgresAuditStore(database).Stage(AuditEventDraft.Create(context, receivedAt, action, "build",
            build.Id.ToString(CultureInfo.InvariantCulture), outcome, reason, details) with { ResultRevision = revision });
    }
}
