using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Builds.Runtime;

namespace Vivarium.DefinitionProof;

[NonParallelizable, Category("US013BuildNumbers")]
public sealed class BuildNumberAcceptanceCases
{
    private R0AcceptanceInstallation installation = null!;
    [OneTimeSetUp] public async Task Prepare() { installation = new(); await installation.StartAsync(); }
    [OneTimeTearDown] public async Task Cleanup() => await installation.DisposeAsync();

    [TestCase(0), TestCase(1), TestCase(2)]
    public async Task Actual_stdout_number_messages_retain_the_reference_value_and_independent_result(int variant)
        => await RunAsync(variant);

    [Test] public async Task Terminal_metadata_recovers_a_lost_live_number_frame() => await RunAsync(0, dropLive: true);
    [Test] public async Task Registered_run_secrets_are_masked_in_number_metadata_and_output() => await RunAsync(0, secretNumber: true);

    private async Task RunAsync(int variant, bool dropLive = false, bool secretNumber = false)
    {
        var wire = new R0AgentMessageGate { DropBuildNumbers = dropLive };
        wire.Acceptance.Release(); wire.Step.Release(); wire.Collection.Release(); wire.Completion.Release(); wire.Result.Release();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
        { services.AddSingleton(wire); services.AddGrpc(options => options.Interceptors.Add<R0AgentMessageGate>()); });
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        var registered = secretNumber ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) : null;
        var value = secretNumber ? "$US013_SECRET" : variant == 0 ? "1.2.3-nightly.20261104.%build.counter%" : "number with |'quote||and|[bracket|]";
        var expectedNumber = secretNumber ? "[REDACTED]" : variant == 0 ? "1.2.3-nightly.20261104.1" : "number with 'quote|and[bracket]";
        var script = "#!/bin/sh\nset -eu\necho \"##teamcity[buildNumber '" + value + "']\"\nsleep 2\nexit " + (variant == 2 ? "7" : "0") + "\n";
        var settings = """
            using Vivarium.Definitions;
            public static class Managed {
              public static Settings Declare() => new("P", null, [], [
                new("P_Number", "Number", [], [new("number", "Owned number producer", "simpleRunner", [new("script.content", SCRIPT, null)])],
                  [new("system.os.family", "linux")], [], [], 2, [])
              ]);
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"), settings.Replace("SCRIPT", JsonSerializer.Serialize(script), StringComparison.Ordinal));
        await R0AcceptanceCases.CommitCaseAsync(host, "fixture: selected inline Build number and terminal result");
        Assert.That((await R0AcceptanceCases.ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        using var actor = await R0AcceptanceCases.RestrictedActorAsync(host, administrator, csrf);
        actor.BuildClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        await using var linux = new OwnedDockerAgent(host.Root); await host.StartLinuxAgentAsync(linux, administrator, csrf);
        using var submission = secretNumber
            ? await actor.BuildClient.PostAsJsonAsync("/api/v1/builds", new { configurationId = "P_Number",
                secretEnvironment = new Dictionary<string, string> { ["US013_SECRET"] = registered! } })
            : await actor.BuildClient.PostAsJsonAsync("/app/rest/buildQueue", new { buildType = new { id = "P_Number" } });
        Assert.That(submission.StatusCode, Is.EqualTo(secretNumber ? HttpStatusCode.Accepted : HttpStatusCode.OK));
        using var admitted = JsonDocument.Parse(await submission.Content.ReadAsStringAsync());
        var id = admitted.RootElement.GetProperty("id").GetInt64();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        JsonElement terminal;
        var observedLive = false;
        while (true)
        {
            using var read = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/app/rest/builds/id:" + id, deadline.Token));
            terminal = read.RootElement.Clone();
            if (terminal.GetProperty("state").GetString() == "finished") break;
            if (terminal.GetProperty("state").GetString() == "running" && terminal.GetProperty("number").GetString() == expectedNumber) observedLive = true;
            await Task.Delay(100, deadline.Token);
        }
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Vivarium.slnx"))) repository = repository.Parent;
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository!.FullName,
            "tests/Vivarium.Tests/TeamCity/Reference/build-number-observations.json")));
        Assert.That(reference.RootElement.GetProperty("complete").GetBoolean(), Is.True);
        var expected = reference.RootElement.GetProperty("cases")[variant].GetProperty("terminalObservation");
        foreach (var field in new[] { "state", "status", "statusText" })
            Assert.That(terminal.GetProperty(field).GetRawText(), Is.EqualTo(expected.GetProperty(field).GetRawText()), field);
        Assert.That(terminal.GetProperty("number").GetString(), Is.EqualTo(secretNumber ? expectedNumber : expected.GetProperty("number").GetString()));
        if (!dropLive) Assert.That(observedLive, Is.True, "The number must be observable during the actual running process.");
        Assert.That(wire.DroppedBuildNumbers, Is.EqualTo(dropLive ? 1 : 0));
        Assert.That(wire.ResultMessage!.Result.NumberChanges.Single().Value, Is.EqualTo(expectedNumber));
        await host.RestartAsync();
        using var native = JsonDocument.Parse(await actor.BuildClient.GetStringAsync("/api/v1/builds/" + id));
        Assert.That(native.RootElement.GetProperty("number").GetString(), Is.EqualTo(expectedNumber));
        await using var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync();
        Assert.That((await database.RuntimeBuilds.SingleAsync()).Counter, Is.EqualTo(1));
        Assert.That((await database.RuntimeBuildCounters.SingleAsync()).NextValue, Is.EqualTo(2));
        Assert.That((await database.RuntimeBuildNumberObservations.SingleAsync()).Value, Is.EqualTo(expectedNumber));
        var owner = await database.RuntimeBuildExecutions.SingleAsync();
        var assignment = host.Services.GetRequiredService<RuntimeBuildSecrets>().ReadAssignment(id, owner.Assignment, owner.AssignmentProtectionVersion);
        var build = await database.RuntimeBuilds.SingleAsync();
        var change = wire.ResultMessage.Result.NumberChanges.Single();
        Assert.That(await RuntimeBuildNumbers.ApplyAsync(database, build, assignment, [change], complete: true, closed: true,
            DateTimeOffset.UtcNow, CancellationToken.None), Is.True, "Exact terminal replay is harmless.");
        Assert.That(await RuntimeBuildNumbers.ApplyAsync(database, build, assignment, [], complete: true, closed: true,
            DateTimeOffset.UtcNow, CancellationToken.None), Is.False, "A complete result cannot retract live metadata.");
        Assert.That(await RuntimeBuildNumbers.ApplyAsync(database, build, assignment, [], complete: false, closed: true,
            DateTimeOffset.UtcNow, CancellationToken.None), Is.True, "Infrastructure fallback preserves already committed observations.");
        var conflicting = change.Clone(); conflicting.Value = "contradictory-number";
        Assert.That(await RuntimeBuildNumbers.ApplyAsync(database, build, assignment, [conflicting], complete: false, closed: true,
            DateTimeOffset.UtcNow, CancellationToken.None), Is.False);
        Assert.That(build.Number, Is.EqualTo(expectedNumber));
        if (registered is not null)
        {
            var raw = change.Clone(); raw.Value = registered;
            Assert.That(await RuntimeBuildNumbers.ApplyAsync(database, build, assignment, [raw], complete: false, closed: false,
                DateTimeOffset.UtcNow, CancellationToken.None), Is.False, "The Host independently rejects registered raw values.");
            Assert.That(JsonSerializer.Serialize(await database.RuntimeBuildNumberObservations.ToArrayAsync()), Does.Not.Contain(registered));
            Assert.That((await database.RuntimeBuildResults.SingleAsync()).Evidence.AsSpan().IndexOf(Encoding.UTF8.GetBytes(registered)), Is.EqualTo(-1));
            using var output = JsonDocument.Parse(await actor.BuildClient.GetStringAsync($"/api/v1/builds/{id}/output"));
            foreach (var item in output.RootElement.GetProperty("items").EnumerateArray())
                Assert.That(Encoding.UTF8.GetString(await actor.BuildClient.GetByteArrayAsync(item.GetProperty("content").GetProperty("href").GetString())), Does.Not.Contain(registered));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs")), Does.Not.Contain(registered));
        }
    }
}
