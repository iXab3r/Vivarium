using System.Data;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Artifact metadata and private bytes share current Build authority across both HTTP contracts.</summary>
internal sealed class RuntimeBuildArtifacts(IDbContextFactory<VivariumDbContext> databases, RuntimeBuildService builds, RuntimeBuildBytes bytes)
{
    internal async Task<RuntimeBuildArtifact[]> ListAsync(ManagementRequestContext actor, long id, CancellationToken token, bool discloseDenied = false)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        if (await builds.VisibleAsync(database, actor, id, token, discloseDenied) is null) throw RuntimeBuildService.Missing();
        // Manifest admission has a hard 128-file bound; pagination belongs to each public contract.
        return await database.RuntimeBuildArtifacts.AsNoTracking().Where(a => a.BuildId == id).OrderBy(a => a.Ordinal).Take(128).ToArrayAsync(token);
    }

    internal async Task<byte[]> ReadAsync(ManagementRequestContext actor, long id, int? ordinal, string? path, CancellationToken token,
        bool discloseDenied = false)
    {
        RuntimeArtifactUpload upload;
        await using (var database = await databases.CreateDbContextAsync(token))
        {
            await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            if (await builds.VisibleAsync(database, actor, id, token, discloseDenied) is null) throw RuntimeBuildService.Missing();
            var artifact = await database.RuntimeBuildArtifacts.AsNoTracking().SingleOrDefaultAsync(a => a.BuildId == id
                && (ordinal != null ? a.Ordinal == ordinal : a.Path == path), token);
            if (artifact is null) throw new RestApiException(404, "resource_not_found", "Artifact not found", "The artifact is not found");
            upload = await database.RuntimeArtifactUploads.AsNoTracking().SingleAsync(u => u.BuildId == id && u.Sha256 == artifact.Sha256, token);
        }
        var content = await bytes.ReadAsync(upload.ObjectKey, upload.Size, upload.Sha256, token);
        await using var current = await databases.CreateDbContextAsync(token);
        if (await builds.VisibleAsync(current, actor, id, token, discloseDenied) is null) throw RuntimeBuildService.Missing();
        return content;
    }
}
