using System.Text;
using Google.Protobuf;
using Vivarium.Agent;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents.Compatibility;

namespace Vivarium.Tests;

public sealed class BuildNumberPublisherTests
{
    [Test]
    public void Split_UTF8_and_escaped_registered_values_are_masked_before_metadata_and_final_flush()
    {
        const string secret = "owned-'secret|value";
        var publisher = new BuildNumberPublisher("owned", [ByteString.CopyFromUtf8(secret)]);
        var bytes = Encoding.UTF8.GetBytes("unrelated output\r\n##teamcity[buildNumber 'λ-owned-|'secret||value']\r\n");
        var changes = new List<BuildNumberChanged>();
        foreach (var value in bytes) changes.AddRange(publisher.Append([value]));
        Assert.That(changes.Single().Value, Is.EqualTo("λ-[REDACTED]"));
        Assert.That(changes.Single().Sequence, Is.EqualTo(1));
        Assert.That(publisher.FinishStep(), Is.Null);
        Assert.That(publisher.Append("##teamcity[buildNumber 'quote|'||and|[bracket|]']"u8), Is.Empty);
        Assert.That(publisher.FinishStep()!.Value, Is.EqualTo("quote'|and[bracket]"));
        Assert.That(publisher.Changes.Select(c => c.Sequence), Is.EqualTo(new uint[] { 1, 2 }));
        Assert.That(publisher.Changes.All(c => !c.Value.Contains(secret, StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Metadata_delivery_is_an_explicit_additive_capability_and_bounds_are_not_silent_truncation()
    {
        var hello = new Hello { MinimumProtocolVersion = 1, CurrentProtocolVersion = 1 };
        hello.Capabilities.Add(new CapabilitySupport { CapabilityId = BuildNumberProfile.CapabilityId, ContractMajor = 1 });
        Assert.That(AgentProtocolCompatibility.Negotiate(hello).SupportsBuildNumbers, Is.False);
        Assert.That(AgentProtocolCompatibility.Negotiate(hello, enableBuildNumbers: true).SupportsBuildNumbers, Is.True);
        var change = new BuildNumberChanged { BuildId = "owned", Sequence = 1, Value = "1.2.3" };
        Assert.DoesNotThrow(() => BuildNumberProfile.Validate(change, "owned"));
        Assert.Throws<InvalidDataException>(() => BuildNumberProfile.Validate(change, "other"));
        change.Sequence = 129; Assert.Throws<InvalidDataException>(() => BuildNumberProfile.Validate(change, "owned"));
        change.Sequence = 1; change.Value = new('λ', 2049);
        Assert.Throws<InvalidDataException>(() => BuildNumberProfile.Validate(change, "owned"));
    }
}
