using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Vivarium.ApiLedger;

namespace Vivarium.Tests.TeamCity;

public sealed partial class ApiLedgerTests
{
    private void PrepareAutomationR0Accounting()
    {
        PrepareSyntheticR0Accounting();
        ReplaceTable("r0-product-bindings.tsv", "\tCASE-1\tfresh-host-agent", "\tCASE_1\tfresh-host-agent");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var path = Path.Combine(root, "r0-physical.trx"); var document = XDocument.Load(path);
        document.Root!.SetAttributeValue("name", "Automation");
        var definitions = document.Descendants(ns + "UnitTest").ToDictionary(d => (string)d.Attribute("id")!);
        foreach (var result in document.Descendants(ns + "UnitTestResult"))
        {
            var definition = definitions[(string)result.Attribute("testId")!];
            var method = definition.Element(ns + "TestMethod")!;
            var name = (string)method.Attribute("className")! + "." + ((string)result.Attribute("testName")!).Replace('-', '_');
            definition.SetAttributeValue("name", name); result.SetAttributeValue("testName", name); method.Remove();
        }
        document.Save(path); receipt["results"]!["physical"]!["sha256"] = Hash("r0-physical.trx");
    }

    [Test]
    public void Selected_R0_reads_Automation_TRX_only_through_consistent_qualified_definitions()
    {
        PrepareAutomationR0Accounting(); var report = ReadR0Control();
        Assert.That(report.Errors, Is.Empty); Assert.That(report.Green, Is.EqualTo(2));
    }

    [TestCase("definition-name")]
    [TestCase("result-name")]
    [TestCase("unqualified")]
    [TestCase("missing-definition")]
    [TestCase("duplicate-definition")]
    [TestCase("other-producer")]
    public void Selected_R0_rejects_ambiguous_Automation_TRX_identities(string corruption)
    {
        PrepareAutomationR0Accounting(); Assert.That(ReadR0Control().Errors, Is.Empty);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var path = Path.Combine(root, "r0-physical.trx"); var document = XDocument.Load(path);
        var definition = document.Descendants(ns + "UnitTest").First();
        var result = document.Descendants(ns + "UnitTestResult").First();
        switch (corruption)
        {
            case "definition-name": definition.SetAttributeValue("name", (string)definition.Attribute("name")! + "Other"); break;
            case "result-name": result.SetAttributeValue("testName", (string)result.Attribute("testName")! + "Other"); break;
            case "unqualified": definition.SetAttributeValue("name", "CASE_1"); result.SetAttributeValue("testName", "CASE_1"); break;
            case "missing-definition": definition.Remove(); break;
            case "duplicate-definition": definition.AddAfterSelf(new XElement(definition)); break;
            case "other-producer": document.Root!.SetAttributeValue("name", "Unknown"); break;
        }
        document.Save(path); receipt["results"]!["physical"]!["sha256"] = Hash("r0-physical.trx");
        var report = ReadR0Control(); Assert.That(report.Errors, Is.Not.Empty); Assert.That(report.Green, Is.Zero);
    }

    private void PrepareSyntheticR0Accounting()
    {
        // Accounting controls use synthetic files/results; no product execution claim is made.
        Table("r0-product-bindings.tsv", "case_id\tprimary_subsystem\tresult_set\tclass_name\ttest_name\ttarget\tcomparison",
            "CASE-1\tSS-PROTO\tphysical\tVivarium.DefinitionProof.R0AcceptanceCases\tCASE-1\tfresh-host-agent\tfull-wire",
            "NATIVE-CASE\tSS-PROTO\tphysical\tVivarium.DefinitionProof.R0AcceptanceCases\tNative_effect\tfresh-host-agent\tnative");
        WriteTrx("r0-physical.trx", [("CASE-1", "Passed", "Vivarium.DefinitionProof.R0AcceptanceCases", ""),
            ("Native_effect", "Passed", "Vivarium.DefinitionProof.R0AcceptanceCases", "")]);
        var raw = Result("r0-physical.trx", 2); raw["command"] = "synthetic accounting control, no product execution";
        raw["target"] = "fresh-host-agent"; raw["assembly_role"] = "Vivarium.DefinitionProof";
        receipt["results"] = new JsonObject { ["physical"] = raw };
        var assemblies = new JsonArray();
        foreach (var role in new[] { "Vivarium.Controller", "Vivarium.Agent", "Vivarium.Contracts", "Vivarium.DefinitionProof" })
        {
            var path = "retained/" + role + ".dll"; Write(path, "synthetic accounting bytes only");
            assemblies.Add(new JsonObject { ["role"] = role, ["path"] = path, ["sha256"] = Hash(path) });
        }
        receipt["candidate"]!["assemblies"] = assemblies;
    }

    private R0Report ReadR0Control()
    {
        RebindMetadataInputs();
        var source = receipt["candidate"]!["source_inputs"]!.AsArray().Select(n => n!.DeepClone())
            .OrderBy(n => n["path"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
        receipt["candidate"]!["source_inputs"] = new JsonArray(source);
        var fingerprint = Sha(Encoding.UTF8.GetBytes(string.Join('\n', source.Select(n => n["path"]!.GetValue<string>() + ":" + n["sha256"]!.GetValue<string>()))));
        receipt["candidate"]!["source_fingerprint_sha256"] = fingerprint;
        evidence["decision"]!["from_source_fingerprint"] = fingerprint;
        Write("receipt.json", receipt.ToJsonString()); evidence["receipt_sha256"] = Hash("receipt.json");
        Write("r0-assessment.json", evidence.ToJsonString());
        return Ledger.ReadR0(root, "candidate", _ => identity, "r0-assessment.json");
    }

    [Test]
    public void Selected_R0_accounting_keeps_native_full_wire_and_milestone_claims_separate()
    {
        PrepareSyntheticR0Accounting(); var report = ReadR0Control();
        Assert.That(report.Errors, Is.Empty); Assert.That(report.Green, Is.EqualTo(2));
        Assert.That(report.FullReferenceCases, Is.EqualTo(1)); Assert.That(report.BehavioralAndNativeObligations, Is.EqualTo(1));
        Assert.That(report.AllSelectedCasesGreen, Is.True); Assert.That(report.GlobalApiComplete, Is.False);
        Assert.That(report.GlobalVariantDenominator, Is.Null);
        Assert.That(report.MilestoneAcceptance, Does.StartWith("Not assessed"));
    }

    [TestCase("duplicate-result")]
    [TestCase("replay-target")]
    [TestCase("wrong-class")]
    [TestCase("stale-source")]
    [TestCase("missing-assembly")]
    [TestCase("native-as-wire")]
    public void Selected_R0_rejects_false_GREEN_even_when_metadata_hashes_are_refreshed(string corruption)
    {
        PrepareSyntheticR0Accounting(); Assert.That(ReadR0Control().Errors, Is.Empty);
        switch (corruption)
        {
            case "duplicate-result": ReplaceTable("r0-product-bindings.tsv", "\tNative_effect\t", "\tCASE-1\t"); break;
            case "replay-target":
                ReplaceTable("r0-product-bindings.tsv", "fresh-host-agent", "wiremock"); receipt["results"]!["physical"]!["target"] = "wiremock"; break;
            case "wrong-class": ReplaceTable("r0-product-bindings.tsv", "Vivarium.DefinitionProof.R0AcceptanceCases", "Vivarium.Tests.TeamCity.ReplayTests"); break;
            case "stale-source": Write("input.cs", "changed actual source bytes"); break;
            case "missing-assembly": File.Delete(Path.Combine(root, "retained/Vivarium.Controller.dll")); break;
            case "native-as-wire": ReplaceTable("r0-product-bindings.tsv", "\tnative\n", "\tfull-wire\n"); break;
        }
        var report = ReadR0Control(); Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.AllSelectedCasesGreen, Is.False); Assert.That(report.Green, Is.Zero);
    }
}
