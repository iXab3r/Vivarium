using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.AspNetCore.DataProtection;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.Tests;

public sealed class RuntimeBuildSecretTests
{
    [Test]
    public void Recovery_is_bound_to_build_kind_and_key_and_rejects_tampering_and_unprotected_secrets()
    {
        var keys = new EphemeralDataProtectionProvider();
        var codec = new RuntimeBuildSecrets(keys);
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var environment = new Dictionary<string, string> { ["OWNED_SECRET"] = value };
        var assignment = new BuildAssignment { BuildId = "owned-workload", OutputContractMajor = 1 };
        assignment.Steps.Add(new Step { Program = "owned", Env = { { "OWNED_SECRET", value } } });
        assignment.OutputSecrets.Add(ByteString.CopyFromUtf8(value));
        var protectedEnvironment = codec.ProtectEnvironment(42, environment);
        var protectedAssignment = codec.ProtectAssignment(42, assignment);
        foreach (var ciphertext in new[] { protectedEnvironment, protectedAssignment })
            Assert.That(ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)), Is.EqualTo(-1));
        var afterRestart = new RuntimeBuildSecrets(keys);
        Assert.That(afterRestart.ReadEnvironment(42, protectedEnvironment), Is.EqualTo(environment));
        Assert.That(afterRestart.ReadAssignment(42, protectedAssignment), Is.EqualTo(assignment));
        Assert.Throws<InvalidDataException>(() => codec.ReadEnvironment(43, protectedEnvironment));
        Assert.Throws<InvalidDataException>(() => codec.ReadAssignment(43, protectedAssignment));
        Assert.Throws<InvalidDataException>(() => codec.ReadEnvironment(42, protectedAssignment));
        Assert.Throws<InvalidDataException>(() => codec.ReadAssignment(42, protectedEnvironment));
        Assert.Throws<InvalidDataException>(() => new RuntimeBuildSecrets(new EphemeralDataProtectionProvider()).ReadEnvironment(42, protectedEnvironment));
        protectedAssignment[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => codec.ReadAssignment(42, protectedAssignment));
        Assert.Throws<InvalidDataException>(() => codec.ReadAssignment(42, assignment.ToByteArray()));
        Assert.Throws<InvalidDataException>(() => codec.ReadAssignment(42, assignment.ToByteArray(), version: 0));
        assignment.OutputSecrets.Clear();
        Assert.That(codec.ReadAssignment(42, assignment.ToByteArray(), version: 0), Is.EqualTo(assignment));
    }

    [Test]
    public void Prepared_input_encryption_is_bound_to_its_identity_and_separate_from_build_recovery()
    {
        var codec = new RuntimeBuildSecrets(new EphemeralDataProtectionProvider());
        var id = Guid.NewGuid();
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var input = new Dictionary<string, Dictionary<string, string>> { ["P_Release"] = new() { ["OWNED_SECRET"] = value } };
        var encrypted = codec.ProtectRunInput(id, input);
        Assert.That(encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)), Is.EqualTo(-1));
        Assert.That(codec.ReadRunInput(id, encrypted)["P_Release"], Is.EqualTo(input["P_Release"]));
        Assert.Throws<InvalidDataException>(() => codec.ReadRunInput(Guid.NewGuid(), encrypted));
        Assert.Throws<InvalidDataException>(() => codec.ReadEnvironment(42, encrypted));
        encrypted[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => codec.ReadRunInput(id, encrypted));
    }

    [Test]
    public void Prepared_input_marker_is_explicit_and_never_changes_unrelated_ordinary_parameters()
    {
        var ordinary = new Dictionary<string, string> { ["vivarium.run-input"] = "ordinary", ["env.A"] = "value" };
        Assert.That(RuntimeBuildRunInputs.Extract(ordinary), Is.Null);
        Assert.That(ordinary.Count, Is.EqualTo(2));
        var id = Guid.NewGuid(); ordinary[RuntimeBuildRunInputs.Parameter] = "vri_" + id.ToString("N");
        Assert.That(RuntimeBuildRunInputs.Extract(ordinary), Is.EqualTo(id));
        Assert.That(ordinary.Keys, Is.EqualTo(new[] { "env.A" }));
        ordinary[RuntimeBuildRunInputs.Parameter] = "vri_invalid";
        Assert.Throws<Vivarium.Controller.Rest.Common.RestApiException>(() => RuntimeBuildRunInputs.Extract(ordinary));
    }

    [Test]
    public void Run_secret_input_rejects_environment_aliases_invalid_names_values_and_excessive_material()
    {
        Dictionary<string, string>[] invalid = [
            new() { ["A"] = "x", ["a"] = "y" }, new() { ["A\n"] = "x" }, new() { ["env.A"] = "x" },
            new() { ["A"] = "" }, new() { ["A"] = "nul\0value" }, new() { ["A"] = new('x', 4097) },
            Enumerable.Range(0, 65).ToDictionary(i => "A" + i, _ => "x"),
            Enumerable.Range(0, 5).ToDictionary(i => "A" + i, _ => new string('x', 4096))
        ];
        foreach (var environment in invalid)
            Assert.Throws<InvalidDataException>(() => RuntimeBuildSecrets.Validate(environment));
        Assert.DoesNotThrow(() => RuntimeBuildSecrets.Validate(new Dictionary<string, string> { ["A_1"] = "spaces and Unicode λ\r\n" }));
    }
}
