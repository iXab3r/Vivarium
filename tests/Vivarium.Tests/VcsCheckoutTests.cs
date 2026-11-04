using Vivarium.Contracts;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents.Compatibility;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Tests;

public sealed class VcsCheckoutTests
{
    [TestCase("file:///etc/passwd")]
    [TestCase("ssh://git@example.invalid/repo")]
    [TestCase("https://user:password@example.invalid/repo")]
    [TestCase("https://example.invalid/repo?credential=value")]
    [TestCase("https://example.invalid/repo#fragment")]
    public void Source_cannot_delegate_local_files_or_implicit_credentials(string url)
        => Assert.Throws<InvalidDataException>(() => VcsCheckoutProfile.Validate(new()
            { Url = url, Branch = "refs/heads/main", Revision = new string('a', 40) }));

    [Test]
    public void Checkout_support_is_opt_in_on_both_sides_and_does_not_upgrade_a_stale_Agent()
    {
        var hello = new Hello { MinimumProtocolVersion = 1, CurrentProtocolVersion = 1 };
        Assert.That(AgentProtocolCompatibility.Negotiate(hello, enableVcsCheckout: true).SupportsVcsCheckout, Is.False);
        hello.Capabilities.Add(new CapabilitySupport { CapabilityId = VcsCheckoutProfile.CapabilityId, ContractMajor = 1 });
        Assert.That(AgentProtocolCompatibility.Negotiate(hello).SupportsVcsCheckout, Is.False);
        Assert.That(AgentProtocolCompatibility.Negotiate(hello, enableVcsCheckout: true).SupportsVcsCheckout, Is.True);
        Assert.That(BuildAssignment.Descriptor.FindFieldByNumber(10).Name, Is.EqualTo("vcs_checkout"));
        Assert.That(StepStatus.Descriptor.FindFieldByNumber(5).Name, Is.EqualTo("vcs_checkout"));
        Assert.That(BuildResult.Descriptor.FindFieldByNumber(9).Name, Is.EqualTo("vcs_checkout"));
    }

    [Test]
    public void A_successful_process_result_cannot_replace_missing_or_different_checkout_evidence()
    {
        var assignment = new BuildAssignment { VcsCheckout = new() { Revision = new string('a', 40) } };
        var result = new BuildResult { Outcome = BuildOutcome.Succeeded };
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.VcsCheckout = new() { Succeeded = true, Revision = new string('b', 40) };
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.VcsCheckout.Revision = assignment.VcsCheckout.Revision;
        Assert.DoesNotThrow(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
        result.VcsCheckout.Succeeded = false; result.VcsCheckout.Revision = ""; result.VcsCheckout.FailureCode = "git_unavailable";
        Assert.Throws<InvalidDataException>(() => RuntimeBuildCoordinator.ValidateResult(result, assignment));
    }
}
