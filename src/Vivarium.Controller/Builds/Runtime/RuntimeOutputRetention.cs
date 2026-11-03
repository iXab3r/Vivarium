using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Explicit Host retention policy; durable availability precedes retryable private-byte deletion.</summary>
internal sealed class RuntimeOutputRetention(IDbContextFactory<VivariumDbContext> databases, RuntimeBuildBytes bytes,
    IConfiguration configuration, TimeProvider time, ILogger<RuntimeOutputRetention> logger)
{
    internal int? Days => configuration.GetValue<int?>("Vivarium:OutputRetentionDays");

    internal async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await TickAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or IOException
                or HttpRequestException or TimeoutException or OperationCanceledException)
            { logger.LogWarning("Output retention is unavailable; durable availability/deletion intent is retained."); }
            await Task.Delay(TimeSpan.FromMinutes(1), time, token);
        }
    }

    internal async Task TickAsync(CancellationToken token)
    {
        if (Days is { } days)
        {
            if (days is < 1 or > 3650) throw new InvalidDataException("The output retention period is invalid.");
            await ExpireAsync(time.GetUtcNow().AddDays(-days), token);
        }
        // Finish an already committed deletion even if an operator subsequently disables expiry.
        // This worker is independent of queue/heartbeat/cancellation coordination.
        for (var count = 0; count < 64; count++)
        {
            await using var database = await databases.CreateDbContextAsync(token);
            var pending = await (from expiry in database.RuntimeOutputExpiries
                join range in database.RuntimeOutputRanges on expiry.RangeId equals range.Id
                where expiry.DeletedAt == null orderby expiry.ExpiredAt, expiry.RangeId
                select new { expiry.RangeId, range.ObjectKey }).FirstOrDefaultAsync(token);
            if (pending is null) break;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await bytes.DeleteAsync(pending.ObjectKey!, deadline.Token).WaitAsync(TimeSpan.FromSeconds(15), token);
            // A duplicate/idempotent delete after a crash is safe. Expiry identity never changes.
            await database.RuntimeOutputExpiries.Where(e => e.RangeId == pending.RangeId && e.DeletedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.DeletedAt, time.GetUtcNow()), token);
        }
    }

    private async Task ExpireAsync(DateTimeOffset cutoff, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RuntimeBuildAccess.FenceQueueAsync(database, token);
        // One completed Build per tick bounds the transaction. No external I/O holds the fence.
        var build = await (from value in database.RuntimeBuilds
            join output in database.RuntimeBuildOutputs on value.Id equals output.BuildId
            where value.State == "FINISHED" && value.FinishedAt <= cutoff && output.RetentionEpoch == 0
            orderby value.Id select value).FirstOrDefaultAsync(token);
        if (build is null) { await transaction.CommitAsync(token); return; }
        var now = time.GetUtcNow();
        var count = await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RuntimeOutputExpiries" ("RangeId", "ExpiredAt")
            SELECT "Id", {now} FROM "RuntimeOutputRanges"
            WHERE "BuildId" = {build.Id} AND "Position" IS NOT NULL AND "Kind" IN ('HEAD','TAIL')
            """, token);
        var state = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == build.Id, token);
        state.RetentionEpoch++;
        await RuntimeBuildAudit.StageAsync(database, build, now, "build.output.expired",
            details: new Dictionary<string, string> { ["rangeCount"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) }, token: token);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }
}
