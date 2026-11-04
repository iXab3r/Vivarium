using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeAgentByteEndpoints
{
    private static readonly SemaphoreSlim UploadSlots = new(4);
    internal static void MapRuntimeAgentBytes(this WebApplication app)
    {
        app.MapPut("/blobs/{sha256}", async (HttpContext http, string sha256, AgentCredentials credentials,
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildBytes bytes, TimeProvider time) =>
        {
            var scope = await OwnerAsync(http, credentials, databases, time, http.RequestAborted);
            if (scope is null) return Denied(http);
            if (!await UploadSlots.WaitAsync(0, http.RequestAborted))
                return RestProblems.Create(http, 429, "artifact_upload_busy", "Artifact upload is busy", "Retry this bounded upload.", retryable: true);
            try
            {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            if (!Regex.IsMatch(sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) return RestProblems.NotFound(http, "blob", sha256);
            if (http.Request.Headers["X-Vivarium-Blob-Declared-Size"].Count != 1
                || !long.TryParse(http.Request.Headers["X-Vivarium-Blob-Declared-Size"][0], NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
                || declared is < 0 or > 65536 || http.Request.ContentLength is not null && http.Request.ContentLength != declared)
                return RestProblems.Create(http, 413, "artifact_profile_limit", "The selected artifact profile limit was exceeded",
                    "The selected R0 literal-file upload accepts at most 64 KiB per file.");
            await using (var check = await databases.CreateDbContextAsync(http.RequestAborted))
                if (await check.RuntimeArtifactUploads.CountAsync(u => u.BuildId == scope.BuildId, http.RequestAborted) >= 128
                    && !await check.RuntimeArtifactUploads.AnyAsync(u => u.BuildId == scope.BuildId && u.Sha256 == sha256, http.RequestAborted))
                    return RestProblems.Create(http, 413, "artifact_profile_limit", "The selected artifact profile limit was exceeded",
                        "The selected R0 literal-file upload accepts at most 128 content objects.");
            // Verify the entire bounded entity before any object write. Untrusted bytes never
            // overwrite an existing content address, including another Build's retained object.
            var content = new byte[checked((int)declared)]; var offset = 0;
            while (offset < content.Length)
            {
                var read = await http.Request.Body.ReadAsync(content.AsMemory(offset), deadline.Token);
                if (read == 0) break; offset += read;
            }
            if (offset != content.Length || await http.Request.Body.ReadAsync(new byte[1], deadline.Token) != 0
                || Convert.ToHexStringLower(SHA256.HashData(content)) != sha256)
                return RestProblems.Create(http, 400, "artifact_integrity_mismatch", "Artifact integrity differs", "The declared size and SHA256 must match the exact body.");
            var key = $"build-artifacts/{scope.BuildId}/{sha256}";
            // Reserve bounded ownership before PUT. A terminal/racing request may never allocate
            // an unjournalled private object, and failed/rejected uploads remain cleanup work.
            await using (var reserve = await databases.CreateDbContextAsync(deadline.Token))
            await using (var reservation = await reserve.Database.BeginTransactionAsync(deadline.Token))
            {
                await AgentCredentials.LockAgentAsync(reserve, scope.AgentId, deadline.Token);
                if ((await OwnerAsync(http, credentials, databases, time, deadline.Token))?.BuildId != scope.BuildId) return Denied(http);
                var known = await reserve.RuntimeArtifactUploads.SingleOrDefaultAsync(u => u.BuildId == scope.BuildId && u.Sha256 == sha256, deadline.Token);
                if (known is not null) return Results.NoContent();
                if (await reserve.RuntimeBuildResults.AnyAsync(r => r.BuildId == scope.BuildId, deadline.Token)) return Closed(http);
                var pending = await reserve.RuntimeOutputDeletions.AnyAsync(d => d.ObjectKey == key, deadline.Token);
                if (!pending)
                {
                    var stagedCount = await reserve.RuntimeOutputDeletions.CountAsync(d => d.BuildId == scope.BuildId && d.ObjectKey.StartsWith("build-artifacts/"), deadline.Token);
                    if (stagedCount + await reserve.RuntimeArtifactUploads.CountAsync(u => u.BuildId == scope.BuildId, deadline.Token) >= 128) return Closed(http);
                    await reserve.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(83371856)", deadline.Token);
                    if (await reserve.RuntimeOutputDeletions.CountAsync(deadline.Token) >= 1024) return Busy(http);
                    reserve.RuntimeOutputDeletions.Add(new() { BuildId = scope.BuildId, ObjectKey = key, CreatedAt = time.GetUtcNow() });
                    await reserve.SaveChangesAsync(deadline.Token);
                }
                await reservation.CommitAsync(deadline.Token);
            }
            await using var database = await databases.CreateDbContextAsync(deadline.Token);
            await using var transaction = await database.Database.BeginTransactionAsync(deadline.Token);
            var staged = await database.RuntimeOutputDeletions.FromSqlInterpolated($"""
                SELECT * FROM "RuntimeOutputDeletions" WHERE "ObjectKey" = {key} FOR UPDATE
                """).SingleOrDefaultAsync(deadline.Token);
            if (staged is null)
            {
                if ((await OwnerAsync(http, credentials, databases, time, deadline.Token))?.BuildId != scope.BuildId) return Denied(http);
                return await database.RuntimeArtifactUploads.AnyAsync(u => u.BuildId == scope.BuildId && u.Sha256 == sha256, deadline.Token)
                    ? Results.NoContent() : Busy(http);
            }
            // Cleanup skips the object row; Agent/queue coordination remains free during I/O.
            await bytes.PutAsync(key, content, deadline.Token);
            await AgentCredentials.LockAgentAsync(database, scope.AgentId, deadline.Token);
            var current = await OwnerAsync(http, credentials, databases, time, deadline.Token);
            if (current?.BuildId != scope.BuildId) return Denied(http);
            var existing = await database.RuntimeArtifactUploads.SingleOrDefaultAsync(u => u.BuildId == scope.BuildId && u.Sha256 == sha256, deadline.Token);
            if (existing is null)
            {
                if (await database.RuntimeBuildResults.AnyAsync(r => r.BuildId == scope.BuildId, deadline.Token)
                    || await database.RuntimeArtifactUploads.CountAsync(u => u.BuildId == scope.BuildId, deadline.Token) >= 128)
                    return Closed(http);
                database.RuntimeArtifactUploads.Add(new() { BuildId = scope.BuildId, Sha256 = sha256, ObjectKey = key, Size = declared, CreatedAt = time.GetUtcNow() });
            }
            database.RuntimeOutputDeletions.Remove(staged);
            await database.SaveChangesAsync(deadline.Token);
            await transaction.CommitAsync(deadline.Token); return Results.NoContent();
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return RestProblems.Create(http, 408, "artifact_upload_timeout", "Artifact upload timed out", "Retry the upload within its transfer bound.", retryable: true); }
            finally { UploadSlots.Release(); }
        });
        app.MapGet("/blobs/{sha256}", async (HttpContext http, string sha256, AgentCredentials credentials,
            IDbContextFactory<VivariumDbContext> databases, RuntimeBuildBytes bytes, TimeProvider time) =>
        {
            var scope = await OwnerAsync(http, credentials, databases, time, http.RequestAborted);
            if (scope is null) return Denied(http);
            if (!Regex.IsMatch(sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) return RestProblems.NotFound(http, "blob", sha256);
            await using var database = await databases.CreateDbContextAsync(http.RequestAborted);
            var payload = await database.RuntimeBuildPayloads.AsNoTracking()
                .SingleOrDefaultAsync(p => p.BuildId == scope.BuildId && p.Sha256 == sha256, http.RequestAborted);
            if (payload is null) return RestProblems.NotFound(http, "blob", sha256);
            var content = await bytes.ReadAsync(payload.ObjectKey, payload.Size, payload.Sha256, http.RequestAborted);
            // Storage I/O runs outside SQL. A replacement session cannot retain the old read grant
            // merely because it was current before that I/O started.
            var current = await OwnerAsync(http, credentials, databases, time, http.RequestAborted);
            return current?.BuildId == scope.BuildId ? Results.Bytes(content, "application/octet-stream") : Denied(http);
        });
    }

    private static IResult Closed(HttpContext http) => RestProblems.Create(http, 409, "artifact_owner_closed",
        "The artifact owner is closed", "No new content may be added to this manifest.");
    private static IResult Busy(HttpContext http) => RestProblems.Create(http, 429, "artifact_upload_busy",
        "Artifact upload is busy", "Retry this bounded upload.", retryable: true);

    private static IResult Denied(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return RestProblems.Create(http, 401, "agent_ownership_required", "Agent ownership is required",
            "Use the current owning Agent credential and assignment/session headers.");
    }

    private static async Task<RuntimeBuildExecution?> OwnerAsync(HttpContext http, AgentCredentials credentials,
        IDbContextFactory<VivariumDbContext> databases, TimeProvider time, CancellationToken token)
    {
        if (http.Request.Headers.Authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization[0], out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authorization.Parameter)
            || http.Request.Headers["X-Vivarium-Build-Id"].Count != 1 || http.Request.Headers["X-Vivarium-Session-Id"].Count != 1)
            return null;
        var credential = await credentials.ResolveAsync(authorization.Parameter, token);
        if (credential is null) return null;
        var workload = http.Request.Headers["X-Vivarium-Build-Id"][0]; var session = http.Request.Headers["X-Vivarium-Session-Id"][0];
        if (string.IsNullOrWhiteSpace(workload) || workload.Length > 256 || string.IsNullOrWhiteSpace(session) || session.Length > 256) return null;
        await using var database = await databases.CreateDbContextAsync(token);
        var current = await database.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == credential.AgentId, token);
        var execution = await database.RuntimeBuildExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.WorkloadId == workload, token);
        return execution is not null && current.DeletedAt is null && current.AuthTokenHash == credential.AuthTokenHash
            && current.CredentialGeneration == credential.CredentialGeneration && current.SessionAuthenticated
            && current.SessionCredentialGeneration == credential.CredentialGeneration
            && current.CurrentSessionId == session && current.CurrentWorkloadId == workload
            && execution.AgentId == current.AgentId && execution.CredentialGeneration == current.CredentialGeneration
            && execution.CurrentSessionId == session && execution.CurrentConnectionGeneration == current.ConnectionGeneration
            && execution.ReleasedAt is null && (execution.ReconnectDeadline is null || execution.ReconnectDeadline > time.GetUtcNow()) ? execution : null;
    }
}
