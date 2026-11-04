using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Tests;

public sealed class RuntimeBuildProtocolTests
{
    [Test]
    public void Progress_keeps_legacy_wire_tags_and_rejects_unassigned_or_ambiguous_step_receipts()
    {
        var assignment = new BuildAssignment { BuildId = "owned" }; assignment.Steps.Add(new Step { Program = "owned-program" });
        assignment.Steps.Add(new Step { Program = "second-owned-program" });
        var status = new StepStatus { BuildId = "owned", StepIndex = 1, Phase = "RUNNING",
            Completed = new StepResult { StepIndex = 1, ExitCode = 7 } };
        Assert.That(RuntimeBuildProgress.Validate(status, assignment), Is.EqualTo("COMPLETED"));
        Assert.That(StepStatus.Descriptor.FindFieldByNumber(1).Name, Is.EqualTo("build_id"));
        Assert.That(StepStatus.Descriptor.FindFieldByNumber(2).Name, Is.EqualTo("step_index"));
        Assert.That(StepStatus.Descriptor.FindFieldByNumber(3).Name, Is.EqualTo("phase"));
        using var legacy = new CodedInputStream(status.ToByteArray());
        var original = new Dictionary<uint, string>(); uint tag; var oldStepIndex = -1;
        while ((tag = legacy.ReadTag()) != 0)
        {
            if (tag == 10) original[tag] = legacy.ReadString();
            else if (tag == 16) oldStepIndex = legacy.ReadInt32();
            else if (tag == 26) original[tag] = legacy.ReadString();
            else legacy.SkipLastField();
        }
        Assert.That(original[10], Is.EqualTo("owned")); Assert.That(original[26], Is.EqualTo("RUNNING"));
        Assert.That(oldStepIndex, Is.EqualTo(1));
        var invalid = new[] { status.Clone(), status.Clone(), status.Clone(), status.Clone() };
        invalid[0].StepIndex = 2; invalid[1].Completed.StepIndex = 0;
        invalid[2].Phase = "COLLECTING"; invalid[3].Completed.Skipped = true;
        foreach (var item in invalid) Assert.Throws<InvalidDataException>(() => RuntimeBuildProgress.Validate(item, assignment));
    }

    [Test]
    public void Output_rejects_ambiguous_scope_byte_counts_and_unbounded_frames()
    {
        var frame = new BuildOutput { BuildId = "owned", SessionId = "current", StepIndex = 0,
            Stream = LogStream.Stdout, SourceSequenceEndExclusive = 1,
            SourceByteEndExclusive = 3, ProducedBytes = 3, Data = ByteString.CopyFrom([0, 255, 10]) };
        Assert.DoesNotThrow(() => RuntimeBuildOutputStore.Validate(frame));
        var subsequent = frame.Clone(); subsequent.SourceSequenceStart = 1; subsequent.SourceSequenceEndExclusive = 2;
        subsequent.SourceByteStart = 3; subsequent.SourceByteEndExclusive = 6;
        Assert.DoesNotThrow(() => RuntimeBuildOutputStore.Validate(subsequent),
            "A frame's produced_bytes counts its own bytes; ACK and terminal watermark byte counts are cumulative.");
        var variants = new[] { frame.Clone(), frame.Clone(), frame.Clone(), frame.Clone(), frame.Clone(), frame.Clone() };
        variants[0].SourceSequenceEndExclusive = 2;
        variants[1].SourceByteEndExclusive = 2;
        variants[2].Stream = (LogStream)8;
        variants[3].StepIndex = 128;
        variants[4].Data = ByteString.CopyFrom(new byte[65537]);
        variants[5].GapReason = "spool-quota";
        foreach (var invalid in variants) Assert.Throws<InvalidDataException>(() => RuntimeBuildOutputStore.Validate(invalid));
        frame.Data = ByteString.Empty; frame.GapReason = "spool-quota";
        Assert.DoesNotThrow(() => RuntimeBuildOutputStore.Validate(frame));
        frame.GapReason = "invented-gap";
        Assert.Throws<InvalidDataException>(() => RuntimeBuildOutputStore.Validate(frame));
    }

    [Test]
    public void Terminal_result_requires_each_assigned_stream_or_explicit_lost_producer_and_never_invents_empty_watermarks()
    {
        var assignment = new BuildAssignment { BuildId = "owned", OutputContractMajor = 1 };
        assignment.Steps.Add(new Step { Program = "owned-program" });
        var result = new BuildResult { BuildId = "owned", SessionId = "current", Outcome = BuildOutcome.Succeeded };
        result.Steps.Add(new StepResult { StepIndex = 0 });
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.OutputWatermarks.Add(new BuildOutputWatermark { StepIndex = 0, Stream = LogStream.Stdout });
        result.OutputWatermarks.Add(new BuildOutputWatermark { StepIndex = 0, Stream = LogStream.Stderr });
        Assert.DoesNotThrow(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.Steps[0].ExitCode = 7;
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.Steps[0].ExitCode = 0;
        result.OutputWatermarks[1].Stream = LogStream.Stdout;
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.OutputWatermarks[1].Stream = LogStream.Stderr;
        result.OutputWatermarks[0].ProducedBytes = 1;
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.OutputProducerLost = true;
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.OutputWatermarks.Clear();
        Assert.DoesNotThrow(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
    }
}
