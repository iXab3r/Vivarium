using System.Text;
using Google.Protobuf;
using Vivarium.Agent;
using Vivarium.Contracts.V1;

namespace Vivarium.Tests;

public sealed class BuildOutputSpoolTests
{
    private string directory = null!;
    private readonly string build = Guid.NewGuid().ToString("N");

    [SetUp]
    public void CreateOwnedDirectory()
    {
        directory = Path.Combine(Path.GetTempPath(), "vivarium-output-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void DeleteOwnedDirectory() => Directory.Delete(directory, recursive: true);

    [Test]
    public void Masking_spans_reads_and_precedes_disk_and_transport_with_exact_binary_and_empty_streams()
    {
        var spool = BuildOutputSpool.Create(directory, build, 2, [ByteString.CopyFromUtf8("SECRET")]);
        spool.Append(0, LogStream.Stdout, "prefix-SE"u8);
        spool.Append(0, LogStream.Stdout, "CRET-tail"u8);
        spool.Append(0, LogStream.Stderr, new byte[] { 0, 255, 13, 10 });
        var watermarks = spool.Seal();
        var frames = spool.Snapshot("session-a");
        Assert.That(Bytes(frames, 0, LogStream.Stdout), Is.EqualTo("prefix-[REDACTED]-tail"u8.ToArray()));
        Assert.That(Bytes(frames, 0, LogStream.Stderr), Is.EqualTo(new byte[] { 0, 255, 13, 10 }));
        Assert.That(watermarks, Has.Count.EqualTo(4));
        Assert.That(watermarks.Where(w => w.StepIndex == 1).All(w => w.FinalSequenceExclusive == 0 && w.ProducedBytes == 0), Is.True);
        Assert.That(frames.All(f => f.SessionId == "session-a" && f.BuildId == build
            && f.SourceSequenceEndExclusive - f.SourceSequenceStart == 1
            && f.SourceByteEndExclusive - f.SourceByteStart == (ulong)f.Data.Length), Is.True);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(path)), Does.Not.Contain("SECRET"));
        var cold = BuildOutputSpool.Open(directory, build);
        Assert.That(Bytes(cold.Snapshot("session-b"), 0, LogStream.Stdout), Is.EqualTo(Bytes(frames, 0, LogStream.Stdout)));
        Assert.That(cold.Seal(), Is.EqualTo(watermarks));
        Assert.Throws<InvalidOperationException>(() => cold.Append(0, LogStream.Stdout, "late"u8));
    }

    [Test]
    public void Quota_keeps_a_bounded_head_and_rolling_tail_and_coalesces_the_exact_missing_byte_range()
    {
        var spool = BuildOutputSpool.Create(directory, build, 1, [], capacityBytes: 16, maximumChunks: 4);
        foreach (var value in new[] { "head", "keep", "old1", "old2", "tail", "last" })
            spool.Append(0, LogStream.Stdout, Encoding.UTF8.GetBytes(value));
        var final = spool.Seal().Single(w => w.Stream == LogStream.Stdout);
        var frames = BuildOutputSpool.Open(directory, build).Snapshot("session-a");
        Assert.That(frames.Where(f => f.Data.Length > 0).Sum(f => f.Data.Length), Is.EqualTo(16));
        Assert.That(Bytes(frames, 0, LogStream.Stdout), Is.EqualTo("headkeeptaillast"u8.ToArray()));
        var gap = frames.Single(f => f.GapReason.Length > 0);
        Assert.That((gap.SourceSequenceStart, gap.SourceSequenceEndExclusive, gap.ProducedBytes, gap.GapReason),
            Is.EqualTo((2UL, 4UL, 8UL, "spool-quota")));
        Assert.That((final.FinalSequenceExclusive, final.ProducedBytes), Is.EqualTo((6UL, 24UL)));
        Assert.That(Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories).Count(), Is.EqualTo(4));
    }

    [Test]
    public void Result_ack_does_not_release_unacknowledged_output_and_independent_stream_ack_survives_restart()
    {
        var spool = BuildOutputSpool.Create(directory, build, 1, []);
        spool.Append(0, LogStream.Stdout, "abcdef"u8);
        spool.Append(0, LogStream.Stderr, "error"u8);
        spool.Seal(); spool.MarkResultAccepted();
        spool.Acknowledge(0, LogStream.Stdout, 1, 6);
        Assert.That(spool.CanRelease, Is.False);
        var cold = BuildOutputSpool.Open(directory, build);
        Assert.That(Bytes(cold.Snapshot("current"), 0, LogStream.Stdout), Is.Empty);
        cold.Acknowledge(0, LogStream.Stdout, 0, 0); // stale cumulative ACK cannot rewind
        Assert.Throws<InvalidDataException>(() => cold.Acknowledge(0, LogStream.Stdout, 2, 7));
        Assert.That(cold.CanRelease, Is.False);
        cold.Acknowledge(0, LogStream.Stdout, 1, 6);
        cold.Acknowledge(0, LogStream.Stderr, 1, 5);
        Assert.That(BuildOutputSpool.Open(directory, build).CanRelease, Is.True);
        cold.DeleteCompleted();
        Assert.That(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public void Lost_unacknowledged_local_content_is_an_explicit_gap_and_does_not_fabricate_empty_output()
    {
        var spool = BuildOutputSpool.Create(directory, build, 1, []);
        spool.Append(0, LogStream.Stdout, "known-produced"u8);
        spool.Seal();
        File.Delete(Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories).Single());
        var cold = BuildOutputSpool.Open(directory, build);
        var gap = cold.Snapshot("current").Single();
        Assert.That((gap.GapReason, gap.SourceSequenceStart, gap.SourceSequenceEndExclusive, gap.ProducedBytes),
            Is.EqualTo(("spool-unavailable", 0UL, 1UL, 14UL)));
        Assert.That(cold.Seal().Single(w => w.Stream == LogStream.Stdout).FinalSequenceExclusive, Is.EqualTo(1));
        Assert.That(cold.CanRelease, Is.False);
    }

    private static byte[] Bytes(IEnumerable<BuildOutput> frames, int step, LogStream stream) => frames
        .Where(f => f.StepIndex == step && f.Stream == stream).OrderBy(f => f.SourceSequenceStart)
        .SelectMany(f => f.Data.ToByteArray()).ToArray();

    [Test]
    public void Tiny_chunks_cannot_exceed_metadata_quota_and_ack_can_split_a_coalesced_gap()
    {
        var spool = BuildOutputSpool.Create(directory, build, 1, [], capacityBytes: 64, maximumChunks: 4);
        foreach (var value in "0123456789") spool.Append(0, LogStream.Stdout, [(byte)value]);
        spool.Seal(); spool.Acknowledge(0, LogStream.Stdout, 5, 5);
        var frames = BuildOutputSpool.Open(directory, build).Snapshot("current");
        var gap = frames.Single(f => f.GapReason == "spool-quota");
        Assert.That((gap.SourceSequenceStart, gap.SourceSequenceEndExclusive, gap.ProducedBytes), Is.EqualTo((5UL, 8UL, 3UL)));
        Assert.That(Bytes(frames, 0, LogStream.Stdout), Is.EqualTo("89"u8.ToArray()));
        Assert.That(Directory.EnumerateFiles(directory, "*.bin", SearchOption.AllDirectories).Count(), Is.LessThanOrEqualTo(4));
    }

    [Test]
    public void A_lost_producer_cannot_invent_final_watermarks_and_completed_cleanup_recovers_without_a_pending_result()
    {
        var spool = BuildOutputSpool.Create(directory, build, 1, []);
        spool.Append(0, LogStream.Stdout, "prefix"u8);
        var cold = BuildOutputSpool.Restore(directory, build)!;
        Assert.Throws<InvalidOperationException>(() => cold.Seal());
        cold.MarkProducerLost(); cold.MarkResultAccepted();
        Assert.That(cold.CanRelease, Is.False);
        cold.Acknowledge(0, LogStream.Stdout, 1, 6);
        Assert.That(cold.CanRelease, Is.True);
        Assert.Throws<InvalidOperationException>(() => cold.Seal());
        // Models a crash after the pending-result file was retired but before spool cleanup.
        Assert.That(BuildOutputSpool.Restore(directory, pendingBuildId: null), Is.Null);
        Assert.That(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), Is.Empty);
    }
}
