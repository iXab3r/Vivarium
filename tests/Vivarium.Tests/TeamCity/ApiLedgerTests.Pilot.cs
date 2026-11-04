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
}
