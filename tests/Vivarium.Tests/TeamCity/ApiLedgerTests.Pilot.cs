using System.Text.Json.Nodes;
using Vivarium.ApiLedger;

namespace Vivarium.Tests.TeamCity;

public sealed partial class ApiLedgerTests
{
    [TestCase("binding")]
    [TestCase("tamper")]
    [TestCase("candidate")]
    [TestCase("producer")]
    public void Pilot_assessment_preserves_raw_outcome_and_refuses_unqualified_green(string mutation)
    {
        WriteTrx("pilot.trx", [("Read", "Passed", "Proof.Cases", ""), ("Sdk", "Passed", "Proof.Cases", "")]);
        Write(Ledger.Workstream + "pilot-contract-scope.json", """
            {"cases":[{"id":"PILOT-1","product_binding":"Proof.Cases.Read"}],"declared_variant_ids":["WIRE-DECLARED"]}
            """);
        Write("bindings.json", """[{"case_id":"PILOT-1","binding":"Proof.Cases.Read","display_name":"Read"}]""");
        Write("discovery.log", "Read\nSdk\n");
        Write("seal.json", """{"inputs":[{"path":"input.cs","sha256":"unknown"}],"producers":[{"path":"producer.dll","sha256":"unknown"}]}""");
        Write("pilot-receipt.json", new JsonObject { ["candidate"] = "base", ["trx_sha256"] = Hash("pilot.trx"),
            ["sdk"] = new JsonObject { ["binding"] = "Proof.Cases.Sdk" } }.ToJsonString());
        JsonObject Bound(string path) => new() { ["path"] = path, ["sha256"] = Hash(path) };
        var assessment = new JsonObject { ["candidate_id"] = "head", ["receipt"] = Bound("pilot-receipt.json"),
            ["seal"] = Bound("seal.json"), ["bindings"] = Bound("bindings.json"), ["discovery"] = Bound("discovery.log"), ["trx"] = "pilot.trx" };
        if (mutation == "candidate") assessment["candidate_id"] = "wrong";
        if (mutation == "tamper") Write("pilot.trx", File.ReadAllText(Path.Combine(root, "pilot.trx")).Replace("Passed", "Failed"));
        if (mutation == "binding") { Write("bindings.json", "[]"); assessment["bindings"] = Bound("bindings.json"); }
        Write("pilot-assessment.json", assessment.ToJsonString());
        var report = Ledger.ReadPilot(root, "head", "pilot-assessment.json", _ => identity);
        Assert.That(report.CurrentGreen, Is.Zero);
        Assert.That(report.Gaps, Is.Not.Empty);
        Assert.That(report.GlobalApiComplete, Is.False);
        Assert.That(report.DeclaredWireVariants, Is.EqualTo(1));
        if (mutation == "producer") { Assert.That(report.RawPassed, Is.EqualTo(2)); Assert.That(report.ProducerDrift, Does.Contain("producer.dll")); }
    }

    [TestCase("valid-addition", true)]
    [TestCase("valid-source", true)]
    [TestCase("wrong-old", false)]
    [TestCase("wrong-current", false)]
    [TestCase("unsupported-producer", false)]
    [TestCase("missing-evidence", false)]
    public void Pilot_input_transfer_binds_both_bytes_and_specific_evidence(string mutation, bool valid)
    {
        Write("input.cs", "changed source"); Write("diff.txt", "Observed source diff and affected test binding");
        var addition = mutation == "valid-addition";
        var d = new JsonObject {
            ["path"] = "input.cs", ["previous_sha256"] = addition ? "ADDED" : "old",
            ["current_sha256"] = Hash("input.cs"), ["kind"] = mutation == "unsupported-producer" ? "producer" : addition ? "addition" : "source",
            ["classification"] = "no_selected_behavior_impact", ["rationale"] = "Changes only the separate observer fixture",
            ["assessor"] = "test assessor", ["affected_binding"] = "Proof.Observer", ["evidence_category"] = "observer",
            ["observed_diff"] = "Extract observer method", ["independent_review"] = "Required",
            ["producer_cause"] = "runtime",
            ["evidence"] = new JsonArray(new JsonObject { ["path"] = "diff.txt", ["sha256"] = Hash("diff.txt") }) };
        if (mutation == "wrong-old") d["previous_sha256"] = "wrong";
        if (mutation == "wrong-current") d["current_sha256"] = "wrong";
        if (mutation == "missing-evidence") d["evidence"] = new JsonArray();
        var gaps = new List<string>();
        Ledger.QualifyPilotInput(root, new JsonObject { ["impact_bindings"] = new JsonArray(d) }, "input.cs",
            addition ? null : "old", mutation == "unsupported-producer" ? "producer" : addition ? "addition" : "source", gaps);
        Assert.That(gaps.Count == 0, Is.EqualTo(valid), string.Join(";", gaps));
    }

    [TestCase("valid")]
    [TestCase("tamper")]
    [TestCase("missing")]
    [TestCase("source")]
    [TestCase("root")]
    [TestCase("agent")]
    public void Pilot_heavy_composition_checks_actual_files_and_retains_failed_observer(string mutation)
    {
        JsonObject Bound(string path, JsonNode value) { Write(path, value.ToJsonString()); return new() { ["path"] = path, ["sha256"] = Hash(path) }; }
        var identity = new JsonObject { ["packageSource"] = "source", ["definitionGit"] = "definition",
            ["scriptSha256"] = "script", ["helperSha256"] = "helper", ["publisherSha256"] = "publisher" };
        var files = new JsonObject(); var remote = new JsonArray(); var assets = new JsonArray();
        for (var n = 0; n < 10; n++)
        {
            var name = n + ".zip"; Write("heavy/published/" + name, "payload " + n);
            var hash = Hash("heavy/published/" + name); files[name] = hash;
            remote.Add(new JsonObject { ["name"] = name, ["digest"] = "sha256:" + hash, ["size"] = 9 });
            assets.Add(new JsonObject { ["Path"] = "release/" + name, ["Sha256"] = hash, ["Length"] = 9 });
        }
        var builds = new JsonArray(); var sources = new JsonArray(); var executions = new JsonArray();
        for (var n = 1; n <= 3; n++)
        {
            builds.Add(new JsonObject { ["Id"] = n, ["State"] = "FINISHED", ["Status"] = "SUCCESS", ["Number"] = "version" });
            sources.Add(new JsonObject { ["Version"] = mutation == "source" ? "other" : "source" });
            executions.Add(new JsonObject { ["BuildId"] = n, ["AgentId"] = mutation == "agent" ? "foreign" : n == 2 ? "windows" : "linux" });
        }
        var composition = new JsonObject {
            ["directory"] = "heavy", ["identity"] = identity.DeepClone(),
            ["definition"] = Bound("definition.json", identity),
            ["cli"] = Bound("cli.json", new JsonObject { ["outcome"] = "succeeded", ["build"] = new JsonObject { ["id"] = mutation == "root" ? 8 : 3, ["number"] = "version" } }),
            ["manifest"] = Bound("manifest.json", new JsonObject { ["revision"] = "source", ["version"] = "version", ["files"] = files }),
            ["execution"] = Bound("execution.json", new JsonObject { ["builds"] = builds, ["source"] = sources, ["executions"] = executions }),
            ["receipt"] = Bound("receipt.json", new JsonObject { ["rootBuildId"] = 3, ["revision"] = "source", ["version"] = "version", ["linuxId"] = "linux", ["windowsId"] = "windows",
                ["assets"] = assets, ["release"] = new JsonObject { ["id"] = 434492192, ["draft"] = false, ["tag_name"] = "vversion", ["assets"] = remote } }) };
        if (mutation == "tamper") Write("heavy/published/0.zip", "corrupted");
        if (mutation == "missing") File.Delete(Path.Combine(root, "heavy/published/0.zip"));
        var report = new PilotReport { Candidate = "head" };
        report.HeavyRawResults.Add(new("ordinary-heavy-observer", "Observer", "Failed"));
        report.HeavyRawResults.Add(new("read-only-continuation", "Readback", "Passed"));
        var assessment = new JsonObject { ["heavy_composition"] = composition };
        if (mutation == "valid") Assert.DoesNotThrow(() => Ledger.ReadPilotHeavy(root, assessment, report));
        else Assert.Throws<InvalidDataException>(() => Ledger.ReadPilotHeavy(root, assessment, report));
        Assert.That(report.HeavyRawResults.Select(r => r.Outcome), Is.EqualTo(new[] { "Failed", "Passed" }));
    }
}
