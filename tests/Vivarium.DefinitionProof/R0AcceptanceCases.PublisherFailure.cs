using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Builds.Runtime;
using Vivarium.Controller.Persistence;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Malformed_publication_stops_the_real_producer_and_retains_unknown_output_tails_instead_of_false_completeness()
    {
        var clock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true, configureServices: services =>
            services.Replace(ServiceDescriptor.Singleton(provider => new RuntimeBuildOutputStore(
                provider.GetRequiredService<IDbContextFactory<VivariumDbContext>>(), provider.GetRequiredService<RuntimeBuildBytes>(),
                provider.GetRequiredService<AgentRegistry>(), clock, provider.GetRequiredService<ILogger<RuntimeBuildOutputStore>>()))));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        var path = Path.Combine(host.Definitions, ".vivarium/unbound/P/Settings.g.cs"); var replaced = 0;
        var settings = System.Text.RegularExpressions.Regex.Replace(await File.ReadAllTextAsync(path),
            "powershell -NoProfile -InputFormat Text -OutputFormat Text -EncodedCommand ([A-Za-z0-9+/=]+)", match =>
            {
                var script = Encoding.Unicode.GetString(Convert.FromBase64String(match.Groups[1].Value));
                if (!script.Contains("owned-log-tail", StringComparison.Ordinal)) return match.Value;
                replaced++;
                script = script.Replace("[Console]::Out.Write(\"owned-log-tail`n\")", """
                    [Console]::Out.WriteLine("##teamcity[publishArtifacts '../escape']")
                    while ($true) { [Console]::Out.Write(('x' * 8192)) }
                    """, StringComparison.Ordinal);
                return "powershell -NoProfile -InputFormat Text -OutputFormat Text -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            });
        Assert.That(replaced, Is.EqualTo(1)); await File.WriteAllTextAsync(path, settings);
        await CommitCaseAsync(host, "fixture: malformed publisher with a live output flood");
        Assert.That((await ReconcileCaseAsync(administrator, csrf)).GetProperty("state").GetString(), Is.EqualTo("applied"));
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await RestrictedActorAsync(host, administrator, csrf);
        var gate = Path.Combine(host.Root, "publisher-failure-gate"); Directory.CreateDirectory(gate);
        var id = await SubmitAsync(actor.BuildClient, "P_Log", new() { ["env.CASE_DIR"] = gate });
        var prefix = await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("stream").GetString()).Distinct().Count() == 2);
        await WaitAsync(async () => File.Exists(Path.Combine(gate, "entered"))
            && (await File.ReadAllTextAsync(Path.Combine(gate, "entered"))).EndsWith('\n'));
        using var process = Process.GetProcessById(int.Parse((await File.ReadAllLinesAsync(Path.Combine(gate, "entered")))[0]));
        await File.WriteAllTextAsync(Path.Combine(gate, "release"), "release");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitOutputAsync(actor.BuildClient, id, page => page.GetProperty("output").GetProperty("state").GetString() == "sealing");
        await using (var database = await host.Services.GetRequiredService<IDbContextFactory<VivariumDbContext>>().CreateDbContextAsync())
        {
            var evidence = BuildResult.Parser.ParseFrom((await database.RuntimeBuildResults.SingleAsync(r => r.BuildId == id)).Evidence);
            Assert.That(evidence.Outcome, Is.EqualTo(BuildOutcome.InfrastructureFailed));
            Assert.That(evidence.OutputProducerLost, Is.True); Assert.That(evidence.OutputWatermarks, Is.Empty);
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.That((await WaitBuildStateAsync(actor.BuildClient, id, "finished")).GetProperty("status").GetString(), Is.EqualTo("UNKNOWN"));
        var output = await OutputAsync(actor.BuildClient, id);
        Assert.That(output.GetProperty("output").GetProperty("state").GetString(), Is.EqualTo("complete-with-gaps"));
        var items = output.GetProperty("items").EnumerateArray().ToArray();
        var prefixCount = prefix.GetProperty("items").GetArrayLength();
        Assert.That(items.Take(prefixCount).Select(i => i.GetRawText()), Is.EqualTo(prefix.GetProperty("items").EnumerateArray().Select(i => i.GetRawText())));
        foreach (var gap in items.Skip(prefixCount))
        {
            Assert.That(gap.GetProperty("kind").GetString(), Is.EqualTo("gap"));
            Assert.That(gap.GetProperty("reason").GetString(), Is.EqualTo("producer-lost"));
            Assert.That(gap.GetProperty("sourceSequenceEndExclusive").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        Assert.That(items.Length, Is.EqualTo(prefixCount + 2));
    }
}
