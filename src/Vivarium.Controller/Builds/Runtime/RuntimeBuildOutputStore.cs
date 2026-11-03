using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Bounded transport ingress; only PG-committed bytes or explicit gaps earn an ACK.</summary>
internal sealed class RuntimeBuildOutputStore(IDbContextFactory<VivariumDbContext> databases,
    RuntimeBuildBytes bytes, AgentRegistry registry, TimeProvider time,
    ILogger<RuntimeBuildOutputStore> logger)
{
    private sealed record Pending(RuntimeAgentConnection Connection, BuildOutput Frame);
    private readonly Channel<Pending>[] lanes = Enumerable.Range(0, 4).Select(_ => Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait })).ToArray();
    private const long StepHalfBytes = 32L * 1024 * 1024, BuildHalfBytes = 128L * 1024 * 1024;
    private const int HalfFrameCount = 2048;

    internal bool Enqueue(RuntimeAgentConnection connection, BuildOutput frame)
    {
        Validate(frame);
        if (!connection.Protocol.SupportsDurableBuildOutput || frame.SessionId != connection.Handle.SessionId)
            throw new InvalidDataException("Output contract/session differs.");
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(connection.Handle.AgentId));
        // Queue saturation drops this transport attempt only. The private Agent spool retries it.
        return lanes[hash[0] % lanes.Length].Writer.TryWrite(new(connection, frame.Clone()));
    }

    internal Task RunAsync(CancellationToken stoppingToken) => Task.WhenAll(lanes.Select(l => RunAsync(l, stoppingToken)));
    private async Task RunAsync(Channel<Pending> lane, CancellationToken token)
    {
        await foreach (var pending in lane.Reader.ReadAllAsync(token))
        {
            try { await IngestAsync(pending.Connection, pending.Frame, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (InvalidDataException)
            {
                // A malformed/conflicting producer does not get a success receipt.
                try { await pending.Connection.Lifetime.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
            catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException or IOException or HttpRequestException
                or TimeoutException or OperationCanceledException)
            { logger.LogWarning("Build output storage is unavailable; the producer retains unacknowledged output."); }
        }
    }

    internal async Task IngestAsync(RuntimeAgentConnection connection, BuildOutput frame, CancellationToken token)
    {
        Validate(frame);
        // Check before Storage I/O, then repeat under the durable Agent fence after it. A staged
        // orphan object confers no authority and cannot overwrite another row's immutable bytes.
        var uploadNeeded = false;
        await using (var check = await databases.CreateDbContextAsync(token))
        {
            var agent = await check.AgentRegistrations.AsNoTracking().SingleAsync(a => a.AgentId == connection.Handle.AgentId, token);
            var execution = await check.RuntimeBuildExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.WorkloadId == frame.BuildId, token);
            if (execution is null || !RuntimeBuildCoordinator.Owner(agent, execution, connection)) return;
            var prefix = await check.RuntimeOutputStreams.AsNoTracking().SingleOrDefaultAsync(s => s.BuildId == execution.BuildId
                && s.StepIndex == frame.StepIndex && s.Stream == (int)frame.Stream, token)
                ?? throw new InvalidDataException("Output scope is not assigned.");
            if (frame.SourceSequenceStart > prefix.CommittedSequence) return;
            uploadNeeded = frame.Data.Length > 0 && frame.SourceSequenceEndExclusive > prefix.CommittedSequence;
        }
        string? key = null, hash = null;
        if (frame.Data.Length > 0)
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(frame.Data.Span));
            if (uploadNeeded)
            {
                key = "build-output/" + Guid.NewGuid().ToString("N");
                await bytes.PutAsync(key, frame.Data.Memory, token);
            }
        }
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(database, connection.Handle.AgentId, token);
        var current = await database.AgentRegistrations.SingleAsync(a => a.AgentId == connection.Handle.AgentId, token);
        var owner = await database.RuntimeBuildExecutions.SingleOrDefaultAsync(e => e.WorkloadId == frame.BuildId, token);
        if (owner is null || !RuntimeBuildCoordinator.Owner(current, owner, connection)) return;
        var stream = await database.RuntimeOutputStreams.SingleOrDefaultAsync(s => s.BuildId == owner.BuildId
            && s.StepIndex == frame.StepIndex && s.Stream == (int)frame.Stream, token)
            ?? throw new InvalidDataException("Output scope is not assigned.");
        var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == owner.BuildId, token);
        if (frame.SourceSequenceStart < stream.CommittedSequence && frame.Data.Length > 0)
        {
            var earlier = await database.RuntimeOutputRanges.AsNoTracking().SingleOrDefaultAsync(r => r.BuildId == owner.BuildId
                && r.StepIndex == frame.StepIndex && r.Stream == (int)frame.Stream && r.Start == (decimal)frame.SourceSequenceStart, token);
            if (earlier?.Sha256 is not null && earlier.Sha256 != hash) throw new InvalidDataException("Output retry conflicts.");
        }
        if (output.State is "COMPLETE" or "COMPLETE_WITH_GAPS")
        {
            // Late bytes cannot replace a previously published terminal gap.
            if (stream.FinalSequence is null && frame.SourceSequenceStart <= stream.CommittedSequence
                && frame.SourceSequenceEndExclusive > stream.CommittedSequence
                && await database.RuntimeOutputRanges.AnyAsync(r => r.BuildId == owner.BuildId && r.StepIndex == frame.StepIndex
                    && r.Stream == (int)frame.Stream && r.Kind == "GAP" && r.End == null, token))
            {
                // The immutable unknown tail already declares these bytes unavailable. Recording
                // an observed acknowledged prefix does not turn that tail into a fabricated final
                // watermark, replace it with late data, or assert that the producer stopped.
                if (frame.SourceSequenceStart == stream.CommittedSequence && frame.SourceByteStart != stream.CommittedBytes)
                    throw new InvalidDataException("Late output byte prefix differs.");
                stream.CommittedSequence = frame.SourceSequenceEndExclusive; stream.CommittedBytes = frame.SourceByteEndExclusive;
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token); Acknowledge(connection, frame, stream); return;
        }
        if ((decimal)frame.SourceSequenceStart > stream.CommittedSequence) return; // Missing prefix retries first.
        if ((decimal)frame.SourceSequenceEndExclusive <= stream.CommittedSequence)
        { await transaction.CommitAsync(token); Acknowledge(connection, frame, stream); return; }
        if (frame.SourceSequenceStart == stream.CommittedSequence && frame.SourceByteStart != stream.CommittedBytes
            || stream.FinalSequence is not null && (frame.SourceSequenceEndExclusive > stream.FinalSequence || frame.SourceByteEndExclusive > stream.FinalBytes
                || frame.SourceSequenceEndExclusive == stream.FinalSequence && frame.SourceByteEndExclusive != stream.FinalBytes))
            throw new InvalidDataException("Output byte/sequence prefix differs.");
        var range = new RuntimeOutputRange
        {
            Id = Guid.NewGuid(), BuildId = owner.BuildId, StepIndex = frame.StepIndex, Stream = (int)frame.Stream,
            Start = stream.CommittedSequence, End = frame.SourceSequenceEndExclusive, ByteStart = stream.CommittedBytes,
            ByteEnd = frame.SourceByteEndExclusive, ReceivedAt = time.GetUtcNow(), Sha256 = hash, ObjectKey = key,
            Kind = frame.Data.Length == 0 ? "GAP" : "HEAD", Reason = MapGap(frame.GapReason),
        };
        if (frame.Data.Length > 0)
        {
            var head = database.RuntimeOutputRanges.Where(r => r.BuildId == owner.BuildId && r.Kind == "HEAD");
            var total = await head.SumAsync(r => (decimal?)(r.ByteEnd - r.ByteStart), token) ?? 0;
            var step = await head.Where(r => r.StepIndex == frame.StepIndex).SumAsync(r => (decimal?)(r.ByteEnd - r.ByteStart), token) ?? 0;
            if (stream.TailStarted || total + frame.Data.Length > BuildHalfBytes || step + frame.Data.Length > StepHalfBytes
                || await head.CountAsync(token) >= HalfFrameCount)
            { stream.TailStarted = true; range.Kind = "TAIL"; }
        }
        if (!stream.TailStarted) range.Position = output.NextPosition++;
        database.RuntimeOutputRanges.Add(range);
        stream.CommittedSequence = frame.SourceSequenceEndExclusive; stream.CommittedBytes = frame.SourceByteEndExclusive;
        await database.SaveChangesAsync(token);
        var evicted = await BoundTailAsync(database, output.BuildId, token);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        Acknowledge(connection, frame, stream);
        foreach (var evictedKey in evicted)
            try { await bytes.DeleteAsync(evictedKey, token); }
            catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException)
            { logger.LogWarning("Unreferenced output cleanup is pending."); }
    }

    private async Task<string[]> BoundTailAsync(VivariumDbContext database, long build, CancellationToken token)
    {
        var evicted = new List<string>();
        var tail = await database.RuntimeOutputRanges.Where(r => r.BuildId == build && r.Kind == "TAIL")
            .OrderBy(r => r.ReceiptOrder).ToListAsync(token);
        decimal Size(RuntimeOutputRange row) => row.ByteEnd!.Value - row.ByteStart;
        decimal total = tail.Sum(Size);
        var steps = tail.GroupBy(r => r.StepIndex).ToDictionary(g => g.Key, g => g.Sum(Size));
        while (tail.Count > HalfFrameCount || total > BuildHalfBytes || steps.Any(s => s.Value > StepHalfBytes))
        {
            var over = steps.Where(s => s.Value > StepHalfBytes).Select(s => (int?)s.Key).FirstOrDefault();
            var oldest = tail.First(r => over is null || r.StepIndex == over);
            total -= Size(oldest); steps[oldest.StepIndex] -= Size(oldest); tail.Remove(oldest);
            evicted.Add(oldest.ObjectKey!);
            oldest.Kind = "GAP"; oldest.Reason = "quota-exceeded"; oldest.Sha256 = null; oldest.ObjectKey = null;
        }
        await database.SaveChangesAsync(token);
        // Coalesce only unpublished adjacent gaps. Published identities/positions never change.
        var gaps = await database.RuntimeOutputRanges.Where(r => r.BuildId == build && r.Kind == "GAP" && r.Position == null)
            .OrderBy(r => r.StepIndex).ThenBy(r => r.Stream).ThenBy(r => r.Start).ToArrayAsync(token);
        RuntimeOutputRange? previous = null;
        foreach (var gap in gaps)
        {
            if (previous is not null && previous.StepIndex == gap.StepIndex && previous.Stream == gap.Stream
                && previous.End == gap.Start && previous.ByteEnd == gap.ByteStart && previous.Reason == gap.Reason)
            { previous.End = gap.End; previous.ByteEnd = gap.ByteEnd; database.RuntimeOutputRanges.Remove(gap); }
            else previous = gap;
        }
        return evicted.ToArray();
    }

    internal async Task SealAsync(long buildId, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        var agentId = await database.RuntimeBuildExecutions.AsNoTracking().Where(e => e.BuildId == buildId).Select(e => e.AgentId).SingleAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await AgentCredentials.LockAgentAsync(database, agentId, token);
        var execution = await database.RuntimeBuildExecutions.SingleAsync(e => e.BuildId == buildId, token);
        var output = await database.RuntimeBuildOutputs.SingleAsync(o => o.BuildId == buildId, token);
        if (output.State is "COMPLETE" or "COMPLETE_WITH_GAPS") return;
        var result = await database.RuntimeBuildResults.SingleOrDefaultAsync(r => r.BuildId == buildId, token);
        if (result is null) return;
        var raw = BuildResult.Parser.ParseFrom(result.Evidence);
        var streams = await database.RuntimeOutputStreams.Where(s => s.BuildId == buildId).OrderBy(s => s.StepIndex).ThenBy(s => s.Stream).ToArrayAsync(token);
        var deadline = execution.OutputDeadline <= time.GetUtcNow();
        if (!deadline && (raw.OutputProducerLost || streams.Any(s => s.FinalSequence is null
            || s.CommittedSequence != s.FinalSequence || s.CommittedBytes != s.FinalBytes))) return;
        foreach (var stream in streams)
        {
            var pending = await database.RuntimeOutputRanges.Where(r => r.BuildId == buildId && r.StepIndex == stream.StepIndex
                && r.Stream == stream.Stream && r.Position == null).OrderBy(r => r.Start).ToArrayAsync(token);
            foreach (var range in pending) range.Position = output.NextPosition++;
            if (stream.FinalSequence is null || stream.CommittedSequence < stream.FinalSequence)
            {
                database.RuntimeOutputRanges.Add(new()
                {
                    Id = Guid.NewGuid(), BuildId = buildId, StepIndex = stream.StepIndex, Stream = stream.Stream,
                    Start = stream.CommittedSequence, End = stream.FinalSequence, ByteStart = stream.CommittedBytes, ByteEnd = stream.FinalBytes,
                    Kind = "GAP", Reason = stream.FinalSequence is null ? "producer-lost" : "delivery-deadline",
                    Position = output.NextPosition++, ReceivedAt = time.GetUtcNow(),
                });
                if (stream.FinalSequence is not null) { stream.CommittedSequence = stream.FinalSequence.Value; stream.CommittedBytes = stream.FinalBytes!.Value; }
            }
        }
        await database.SaveChangesAsync(token);
        output.State = await database.RuntimeOutputRanges.AnyAsync(r => r.BuildId == buildId && r.Kind == "GAP", token)
            ? "COMPLETE_WITH_GAPS" : "COMPLETE";
        var build = await database.RuntimeBuilds.SingleAsync(b => b.Id == buildId, token);
        build.State = "FINISHED"; build.FinishedAt = time.GetUtcNow();
        build.Status = raw.Outcome switch { BuildOutcome.Succeeded => "SUCCESS", BuildOutcome.Failed => "FAILURE", _ => "UNKNOWN" };
        build.StatusText = raw.Outcome switch { BuildOutcome.Succeeded => "Success", BuildOutcome.Failed => "Failure",
            BuildOutcome.Cancelled => "Cancelled", _ => "Infrastructure failure" };
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }

    private void Acknowledge(RuntimeAgentConnection connection, BuildOutput frame, RuntimeOutputStream stream) =>
        registry.TrySend(connection.Handle, new() { BuildOutputAcknowledged = new()
        {
            BuildId = frame.BuildId, SessionId = connection.Handle.SessionId, StepIndex = frame.StepIndex, Stream = frame.Stream,
            SequenceExclusive = checked((ulong)stream.CommittedSequence), ProducedBytes = checked((ulong)stream.CommittedBytes),
        } });

    internal static void Validate(BuildOutput frame)
    {
        if (frame.BuildId.Length is < 1 or > 256 || frame.SessionId.Length is < 1 or > 256
            || frame.StepIndex is < 0 or >= 128 || (int)frame.Stream is < 0 or > 1 || frame.Data.Length > 65536
            || frame.SourceSequenceEndExclusive <= frame.SourceSequenceStart || frame.SourceByteEndExclusive <= frame.SourceByteStart
            || frame.ProducedBytes != frame.SourceByteEndExclusive - frame.SourceByteStart)
            throw new InvalidDataException("Output range is invalid.");
        if (frame.Data.Length > 0 ? frame.GapReason.Length != 0 || frame.SourceSequenceEndExclusive - frame.SourceSequenceStart != 1
                || frame.SourceByteEndExclusive - frame.SourceByteStart != (ulong)frame.Data.Length
            : MapGap(frame.GapReason) is null)
            throw new InvalidDataException("Output content/range differs.");
    }
    private static string? MapGap(string reason) => reason switch
    { "spool-quota" => "spool-overflow", "spool-unavailable" or "spool-missing" => "storage-failure", _ => null };
}
