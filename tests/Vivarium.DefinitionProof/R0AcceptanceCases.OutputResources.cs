using System.Net;
using System.Security.Cryptography;
using EyeAuras.Storage;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Output_ingress_bounds_repeated_gaps_and_recovers_failed_quota_deletion_after_a_cold_restart()
    {
        var fault = new R0OutputDeleteFault();
        var wire = new R0AgentWireFaults { DropOutputData = true, DropOutputAcknowledgements = true };
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        {
            services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentWireFaults>());
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildBytes(fault.Wrap(provider.GetRequiredService<IObjectStorage>()))));
        });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var gate = Path.Combine(host.Root, "resource-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        await WaitAsync(() => Task.FromResult(wire.Observed.Length >= 2));
        Assert.That(host.Services.GetRequiredService<RuntimeAgentSessions>().TryGetCurrent(host.AgentId!, out var connection), Is.True);
        var databases = host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>();
        string workload;
        // Controlled accepted-prefix metadata is a cheap boundary precondition. This is an
        // ingestion/cleanup proof, not a claim that these synthetic prefix bytes came from the process.
        await using (var database = await databases.CreateDbContextAsync())
        {
            workload = await database.RuntimeBuildExecutions.Where(e => e.BuildId == id).Select(e => e.WorkloadId).SingleAsync();
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RuntimeOutputRanges" ("Id","BuildId","StepIndex","Stream","Start","End","ByteStart","ByteEnd","Kind","Reason","Position","ReceivedAt")
                SELECT gen_random_uuid(),{id},0,0,n,n+1,n,n+1,'GAP','storage-failure',n+1,now() FROM generate_series(0,2047) n;
                UPDATE "RuntimeBuildOutputs" SET "NextPosition"=2049 WHERE "BuildId"={id};
                UPDATE "RuntimeOutputStreams" SET "CommittedSequence"=2048,"CommittedBytes"=2048 WHERE "BuildId"={id} AND "Stream"=0;
                """);
        }
        var store = host.Services.GetRequiredService<RuntimeBuildOutputStore>();
        for (ulong sequence = 2048; sequence < 2304; sequence++)
            await store.IngestAsync(connection!, new BuildOutput { BuildId = workload, SessionId = connection!.Handle.SessionId,
                StepIndex = 0, Stream = LogStream.Stdout, SourceSequenceStart = sequence, SourceSequenceEndExclusive = sequence + 1,
                SourceByteStart = sequence, SourceByteEndExclusive = sequence + 1, ProducedBytes = 1,
                GapReason = sequence % 2 == 0 ? "spool-unavailable" : "spool-quota" }, CancellationToken.None);
        var key = "build-output/" + Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexStringLower(SHA256.HashData("x"u8));
        await host.Services.GetRequiredService<RuntimeBuildBytes>().PutAsync(key, "x"u8.ToArray(), CancellationToken.None);
        await using (var database = await databases.CreateDbContextAsync())
        {
            Assert.That(await database.RuntimeOutputRanges.CountAsync(r => r.BuildId == id && r.Position != null), Is.EqualTo(2048));
            var gap = await database.RuntimeOutputRanges.SingleAsync(r => r.BuildId == id && r.Position == null);
            Assert.That((gap.Start, gap.End, gap.Reason), Is.EqualTo((2048m, (decimal?)2304, "quota-exceeded")));
            // Only the oldest selected tail object is needed; other unread tail metadata is
            // synthetic precondition state. The next real PUT/ingestion must durably evict it.
            database.RuntimeOutputRanges.Add(new()
            {
                Id = Guid.NewGuid(), BuildId = id, StepIndex = 0, Stream = 1, Start = 0, End = 1,
                ByteStart = 0, ByteEnd = 1, Kind = "TAIL", Sha256 = hash, ObjectKey = key, ReceivedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync(); // The selected object must really be the oldest receipt.
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RuntimeOutputRanges" ("Id","BuildId","StepIndex","Stream","Start","End","ByteStart","ByteEnd","Kind","Sha256","ObjectKey","ReceivedAt")
                SELECT gen_random_uuid(),{id},0,1,n,n+1,n,n+1,'TAIL',{hash},'build-output/' || md5(n::text || {key}),now()
                FROM generate_series(1,2047) n ORDER BY n;
                """);
            await database.RuntimeOutputStreams.Where(s => s.BuildId == id && s.Stream == 1).ExecuteUpdateAsync(u =>
                u.SetProperty(s => s.CommittedSequence, 2048m).SetProperty(s => s.CommittedBytes, 2048m).SetProperty(s => s.TailStarted, true));
        }
        var frame = new BuildOutput { BuildId = workload, SessionId = connection!.Handle.SessionId, StepIndex = 0,
            Stream = LogStream.Stderr, SourceSequenceStart = 2048, SourceSequenceEndExclusive = 2049,
            SourceByteStart = 2048, SourceByteEndExclusive = 2049, ProducedBytes = 1, Data = ByteString.CopyFromUtf8("n") };
        await store.IngestAsync(connection, frame, CancellationToken.None);
        await store.IngestAsync(connection, frame, CancellationToken.None);
        var conflict = frame.Clone(); conflict.Data = ByteString.CopyFromUtf8("z");
        Assert.ThrowsAsync<InvalidDataException>(() => store.IngestAsync(connection, conflict, CancellationToken.None));
        Assert.ThrowsAsync<IOException>(() => host.Services.GetRequiredService<RuntimeOutputRetention>().TickAsync(CancellationToken.None));
        await using (var database = await databases.CreateDbContextAsync())
        {
            Assert.That(await database.RuntimeOutputDeletions.AnyAsync(d => d.ObjectKey == key), Is.True);
            Assert.That(await database.RuntimeOutputRanges.CountAsync(r => r.BuildId == id && r.Kind == "TAIL"), Is.EqualTo(2048));
            var evicted = await database.RuntimeOutputRanges.SingleAsync(r => r.BuildId == id && r.Stream == 1 && r.Start == 0);
            Assert.That(evicted.Kind, Is.EqualTo("GAP")); Assert.That(evicted.ObjectKey, Is.Null);
            var pending = Enumerable.Range(0, 1023).Select(_ => new RuntimeOutputDeletion
                { BuildId = id, ObjectKey = "build-output/" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow }).ToArray();
            database.RuntimeOutputDeletions.AddRange(pending); await database.SaveChangesAsync();
            var blocked = frame.Clone(); blocked.SourceSequenceStart++; blocked.SourceSequenceEndExclusive++;
            blocked.SourceByteStart++; blocked.SourceByteEndExclusive++;
            await store.IngestAsync(connection, blocked, CancellationToken.None);
            Assert.That(await database.RuntimeOutputDeletions.CountAsync(), Is.EqualTo(1024));
            Assert.That(await database.RuntimeOutputRanges.AnyAsync(r => r.BuildId == id && r.Stream == 1 && r.Start == 2049), Is.False,
                "Deletion backpressure must not allocate another object or acknowledge new bytes.");
            var syntheticKeys = pending.Select(p => p.ObjectKey).ToArray();
            await database.RuntimeOutputDeletions.Where(d => syntheticKeys.Contains(d.ObjectKey)).ExecuteDeleteAsync();
        }
        await host.RestartAsync();
        Assert.That(await host.Services.GetRequiredService<RuntimeBuildBytes>().ReadAsync(key, 1, hash, CancellationToken.None), Is.EqualTo("x"u8.ToArray()));
        fault.Reject = false;
        await using (var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
            await database.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.owned_output_delete_ack_fault() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Owned deletion acknowledgement fault' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER "Owned_OutputDeleteAckFault" BEFORE DELETE ON "RuntimeOutputDeletions"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.owned_output_delete_ack_fault();
                """);
        var commit = Assert.ThrowsAsync<DbUpdateException>(() => host.Services.GetRequiredService<RuntimeOutputRetention>().TickAsync(CancellationToken.None));
        Assert.That(commit!.InnerException, Is.TypeOf<Npgsql.PostgresException>());
        Assert.That(((Npgsql.PostgresException)commit.InnerException!).SqlState, Is.EqualTo("23514"));
        var missing = Assert.ThrowsAsync<HttpRequestException>(() => host.Services.GetRequiredService<RuntimeBuildBytes>().ReadAsync(key, 1, hash, CancellationToken.None));
        Assert.That(missing!.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await host.RestartAsync();
        await using (var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
        {
            Assert.That(await database.RuntimeOutputDeletions.AnyAsync(d => d.ObjectKey == key), Is.True);
            await database.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER "Owned_OutputDeleteAckFault" ON "RuntimeOutputDeletions";
                DROP FUNCTION public.owned_output_delete_ack_fault();
                """);
        }
        await host.Services.GetRequiredService<RuntimeOutputRetention>().TickAsync(CancellationToken.None);
        await using (var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
            Assert.That(await database.RuntimeOutputDeletions.AnyAsync(d => d.ObjectKey == key), Is.False);
    }
}
