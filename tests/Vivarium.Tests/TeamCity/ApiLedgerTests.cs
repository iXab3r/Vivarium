using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Vivarium.ApiLedger;

namespace Vivarium.Tests.TeamCity;

[TestFixture]
[Category("ApiLedgerControl")]
public sealed class ApiLedgerTests
{
    private string root = null!;
    private JsonObject receipt = null!;
    private JsonObject evidence = null!;
    private Identity identity = new("head", "production", false, ["input.cs"]);
    private const string Ready = "CASE-1";
    private const string Blocked = "CASE-2";

    [SetUp]
    public void Create_isolated_positive_evidence()
    {
        root = Path.Combine(Path.GetTempPath(), "vivarium-ledger-" + Guid.NewGuid().ToString("N"));
        identity = new("head", "production", false, ["input.cs"]);
        Write("input.cs", "original source/oracle bytes");
        Table("subsystems.tsv", "subsystem_id\tprimary_issue", "SS-PROTO\t12");
        Table("subsystem-slices.tsv", "slice_id\tsubsystem_id\tmilestone\timplementation_issue\timplementation_issues\trest_contract_issue\trest_acceptance_issue\tapi_complete",
            "SS-PROTO@P0\tSS-PROTO\tP0\t12\t35;36\t33\t42\tUNVERIFIED");
        Table("api-blocks.tsv", "block\tsubsystem_slices\tcontract_issues\timplementation_issues\tverification_issues\tapi_complete\tscope_state",
            "P0\tSS-PROTO@P0\t33\t35;36\t42\tUNVERIFIED\tunfrozen");
        Table("subsystem-contributions.tsv", "contribution_id");
        Table("teamcity-official-rest-operations.tsv", "resource\toperation", "rootapi\tgetApiVersion");
        Table("api-ledger.tsv", "api_id\tsubsystem_id\tsubsystem_issue\trelated_subsystems\trest_contract_issue\trest_acceptance_issue\tdiscovery_contract_issue\tallocation_issue\tvariant_case_ids",
            "TCREST:rootapi.getApiVersion\tSS-PROTO\t12\t\t33\t42\t12\t36\tCASE-1;CASE-2");
        const string header = "case_id\tapi_ids\tprimary_owner\trelated_owner\tcontract_task\tacceptance_task\tasserted_effect\tproduct_prerequisite\treplay_binding\tproduct_binding";
        Table("rest-first-cohort-cases.tsv", header, "CASE-1\tTCREST:rootapi.getApiVersion\tSS-PROTO\t\t33\t42\texact bytes\thealthy production\tReplayTests.Read\tRawTeamCityTests.Read");
        Table("rest-local-mutation-cases.tsv", header, "CASE-2\tTCREST:rootapi.getApiVersion\tSS-PROTO\t\t33\t42\tunchanged readback\treal restricted actor\tReplayTests.Read\tproduct-prerequisite-blocked");
        WriteTrx("raw.trx", [("Read(CASE-1)", "Passed", "Vivarium.TeamCity.Conformance.RawTeamCityTests", "")]);
        WriteTrx("controls.trx", [("Read(CASE-1)", "Passed", "Vivarium.Tests.TeamCity.ReplayTests", ""), ("Read(CASE-2)", "Passed", "Vivarium.Tests.TeamCity.ReplayTests", "")]);
        var sourceHash = Hash("input.cs");
        var fingerprint = Sha(Encoding.UTF8.GetBytes("input.cs:" + sourceHash));
        receipt = new JsonObject
        {
            ["candidate"] = new JsonObject { ["git_base"] = "base", ["source_fingerprint_sha256"] = fingerprint, ["production_tree"] = "production",
                ["source_inputs"] = new JsonArray(new JsonObject { ["path"] = "input.cs", ["sha256"] = sourceHash }), ["assemblies"] = new JsonArray() },
            ["reference"] = new JsonObject { ["profile"] = "reference", ["capture"] = "input.cs", ["capture_sha256"] = sourceHash },
            ["results"] = new JsonObject { ["raw"] = Result("raw.trx", 1), ["controls"] = Result("controls.trx", 2) },
        };
        evidence = new JsonObject
        {
            ["candidate_id"] = "candidate", ["expected_head"] = "head", ["receipt"] = "receipt.json", ["reference_profile"] = "reference",
            ["retained_assemblies"] = new JsonArray(),
            ["scope"] = new JsonObject { ["required_case_ids"] = new JsonArray(Ready, Blocked), ["normalized_denominator"] = null, ["frozen"] = false,
                ["api_complete"] = false, ["compatibility_percent"] = null, ["review_state"] = "NOT_REVIEWED", ["debt_state"] = "NOT_ASSESSED" },
            ["decision"] = new JsonObject { ["from_base"] = "base", ["from_source_fingerprint"] = fingerprint, ["to_candidate"] = "candidate",
                ["action"] = "reuse", ["assessor"] = "test", ["rationale"] = "independently assessed unchanged relevant inputs",
                ["impacts"] = new JsonObject { ["production"] = "unchanged", ["shared_services"] = "unchanged", ["fixture_oracle"] = "unchanged", ["reference_client"] = "unchanged", ["environment"] = "unchanged" },
                ["source_overrides"] = new JsonArray(), ["linked_reruns"] = new JsonArray() },
        };
    }

    [TearDown]
    public void Remove_isolated_files() => Directory.Delete(root, recursive: true);

    [Test]
    public void Positive_raw_GREEN_is_distinct_from_replay_and_unknown_scope()
    {
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Empty);
            Assert.That(report.ProductGreenCases, Is.EqualTo(1));
            Assert.That(report.ProductPrerequisiteBlockedCases, Is.EqualTo(1));
            Assert.That(report.ExecutedCases, Is.EqualTo(1));
            Assert.That(report.GlobalApiComplete, Is.False);
            Assert.That(report.GlobalCompatibilityPercent, Is.Null);
            Assert.That(report.Subsystems.Sum(s => s.KnownCases), Is.EqualTo(2));
            Assert.That(report.Blocks.Sum(s => s.Executed), Is.EqualTo(1));
        });
    }

    [TestCase("duplicate")]
    [TestCase("orphan-api")]
    [TestCase("orphan-owner")]
    [TestCase("missing-owner")]
    [TestCase("orphan-task")]
    [TestCase("missing-due")]
    [TestCase("wrong-result")]
    [TestCase("replay-as-product")]
    [TestCase("effectless")]
    [TestCase("false-complete")]
    [TestCase("false-percent")]
    [TestCase("false-review")]
    [TestCase("false-debt")]
    [TestCase("false-block-complete")]
    [TestCase("unknown-reuse")]
    [TestCase("wrong-from")]
    [TestCase("wrong-profile")]
    [TestCase("unassessed-override")]
    [TestCase("wrong-counter")]
    [TestCase("missing-replay-reuse")]
    [TestCase("cross-milestone")]
    [TestCase("missing-column")]
    [TestCase("wrong-json-type")]
    [TestCase("wrong-replay-class")]
    [TestCase("wrong-raw-class")]
    [TestCase("duplicate-replay")]
    public void Malformed_accounting_never_counts_current_GREEN(string corruption)
    {
        switch (corruption)
        {
            case "duplicate": AppendTable("rest-first-cohort-cases.tsv"); break;
            case "orphan-api": ReplaceTable("rest-first-cohort-cases.tsv", "TCREST:rootapi.getApiVersion", "TCREST:missing"); break;
            case "orphan-owner": ReplaceTable("rest-first-cohort-cases.tsv", "SS-PROTO", "SS-MISSING"); break;
            case "missing-owner": ReplaceTable("rest-first-cohort-cases.tsv", "SS-PROTO", ""); break;
            case "orphan-task": ReplaceTable("rest-first-cohort-cases.tsv", "\t33\t", "\t999\t"); break;
            case "missing-due": evidence["scope"]!["required_case_ids"]!.AsArray().Add("CASE-OMITTED"); break;
            case "wrong-result": ReplaceTable("rest-first-cohort-cases.tsv", "RawTeamCityTests.Read", "RawTeamCityTests.Wrong"); break;
            case "replay-as-product":
                receipt["results"]!["raw"] = receipt["results"]!["controls"]!.DeepClone(); break;
            case "effectless": ReplaceTable("rest-first-cohort-cases.tsv", "exact bytes", ""); break;
            case "false-complete": evidence["scope"]!["api_complete"] = true; break;
            case "false-percent": evidence["scope"]!["compatibility_percent"] = 100; break;
            case "false-review": evidence["scope"]!["review_state"] = "PASS"; break;
            case "false-debt": evidence["scope"]!["debt_state"] = "ZERO"; break;
            case "false-block-complete": ReplaceTable("api-blocks.tsv", "UNVERIFIED", "true"); break;
            case "unknown-reuse": evidence["decision"]!["action"] = "automatic-pass"; break;
            case "wrong-from": evidence["decision"]!["from_base"] = "another-execution"; break;
            case "wrong-profile": evidence["reference_profile"] = "other-server"; break;
            case "unassessed-override": evidence["decision"]!["source_overrides"]!.AsArray().Add(new JsonObject { ["path"] = "unknown.cs", ["from_sha256"] = "a", ["to_sha256"] = "b", ["rationale"] = "" }); break;
            case "wrong-counter": receipt["results"]!["raw"]!["counters"]!["total"] = 99; break;
            case "missing-replay-reuse": receipt["results"]!["controls"]!["reused_from"] = "old.json"; break;
            case "cross-milestone": ReplaceTable("subsystem-slices.tsv", "\tP0\t", "\tR0\t"); break;
            case "missing-column": ReplaceTable("rest-first-cohort-cases.tsv", "primary_owner", "wrong_column"); break;
            case "wrong-json-type": evidence["candidate_id"] = new JsonArray(); break;
            case "wrong-replay-class": ReplaceTable("rest-first-cohort-cases.tsv", "ReplayTests.Read", "WrongReplayClass.Read"); break;
            case "wrong-raw-class": ReplaceTable("rest-first-cohort-cases.tsv", "RawTeamCityTests.Read", "WrongRawClass.Read"); break;
            case "duplicate-replay":
                const string fixedName = "Source_established_project_create_readback_replays_finite_sequence_and_resets_state";
                ReplaceTable("rest-first-cohort-cases.tsv", "ReplayTests.Read", "ReplayTests." + fixedName);
                ReplaceTable("rest-local-mutation-cases.tsv", "ReplayTests.Read", "ReplayTests." + fixedName);
                WriteTrx("controls.trx", [(fixedName, "Passed", "Vivarium.Tests.TeamCity.ReplayTests", "")]);
                receipt["results"]!["controls"] = Result("controls.trx", 1); break;
        }
        var report = Read();
        Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.ProductGreenCases, Is.Zero);
    }

    [TestCase("source")]
    [TestCase("fixture")]
    [TestCase("unknown")]
    [TestCase("production-tree")]
    [TestCase("dirty-production")]
    [TestCase("revision")]
    [TestCase("artifact")]
    [TestCase("invalidate")]
    [TestCase("source-eol")]
    [TestCase("missing-production-fingerprint")]
    public void Evidence_change_or_unknown_impact_is_stale_without_running_anything(string change)
    {
        switch (change)
        {
            case "source": Write("input.cs", "changed shared code/oracle"); break;
            case "fixture": evidence["decision"]!["impacts"]!["fixture_oracle"] = "changed"; break;
            case "unknown": evidence["decision"]!["impacts"]!["environment"] = "unknown"; break;
            case "production-tree": identity = identity with { ProductionTree = "changed" }; break;
            case "dirty-production": identity = identity with { ProductionDirty = true }; break;
            case "revision": identity = identity with { Head = "unassessed-main-transfer" }; break;
            case "artifact": Write("raw.trx", File.ReadAllText(Path.Combine(root, "raw.trx")).Replace("Passed", "NotExecuted", StringComparison.Ordinal)); break;
            case "invalidate": evidence["decision"]!["action"] = "invalidate"; break;
            case "source-eol": Write("input.cs", "original source/oracle bytes\r\n"); break;
            case "missing-production-fingerprint": identity = identity with { ProductionPaths = ["input.cs", "unfingerprinted.cs"] }; break;
        }
        var report = Read();
        Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.ProductGreenCases, Is.Zero);
    }

    [Test]
    public void Unrelated_prose_does_not_invalidate_unchanged_evidence()
    {
        Write("notes.md", "unrelated prose edit");
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
    }

    [Test]
    public void Failed_required_replay_control_cannot_support_raw_GREEN()
    {
        WriteTrx("controls.trx", [("Read(CASE-1)", "Failed", "Vivarium.Tests.TeamCity.ReplayTests", "wrong expected byte"),
            ("Read(CASE-2)", "Passed", "Vivarium.Tests.TeamCity.ReplayTests", "")]);
        receipt["results"]!["controls"] = Result("controls.trx", 2, failed: true);
        var report = Read();
        Assert.That(report.ProductGreenCases, Is.Zero);
        Assert.That(report.HarnessFailureCases, Is.EqualTo(1));
        Assert.That(report.Errors, Is.Not.Empty);
    }

    [Test]
    public void Failed_noncase_comparator_control_also_prevents_current_GREEN()
    {
        WriteTrx("controls.trx", [("Read(CASE-1)", "Passed", "Vivarium.Tests.TeamCity.ReplayTests", ""),
            ("Read(CASE-2)", "Passed", "Vivarium.Tests.TeamCity.ReplayTests", ""),
            ("Comparator_control", "Failed", "Vivarium.Tests.TeamCity.ReplayTests", "Assert.That(bytes) failed")]);
        receipt["results"]!["controls"] = Result("controls.trx", 3, failed: true);
        var report = Read();
        Assert.That(report.ProductGreenCases, Is.Zero);
        Assert.That(report.Errors, Is.Not.Empty);
    }

    [Test]
    public void A_main_transfer_needs_an_explicit_new_head_decision()
    {
        identity = identity with { Head = "verified-main" };
        var original = Read();
        Assert.That(original.ProductGreenCases, Is.Zero);
        var transferred = evidence.DeepClone();
        transferred["expected_head"] = "verified-main";
        transferred["decision"]!["rationale"] = "Resulting source tree, all source bytes and environment checked after merge.";
        Write("artifacts/assessed-main.json", transferred.ToJsonString());
        var report = Ledger.Read(root, "candidate", _ => identity, "artifacts/assessed-main.json");
        Assert.That(report.ProductGreenCases, Is.EqualTo(1));
        Assert.That(Read().ProductGreenCases, Is.Zero, "tracked execution assessment was not rewritten");
    }

    [TestCase("response", true)]
    [TestCase("setup", false)]
    [TestCase("setup-assertion", false)]
    [TestCase("cleanup-assertion", false)]
    [TestCase("unknown-assertion", false)]
    public void Failed_result_requires_exact_response_mismatch_classification(string kind, bool behavioral)
    {
        var message = kind switch
        {
            "response" => "Assert.That(status) HTTP status: expected 200, actual 404",
            "setup" => "OneTimeSetUp failed: socket unavailable",
            "setup-assertion" => "OneTimeSetUp: Assert.That(status) HTTP status: expected 200, actual 404",
            "cleanup-assertion" => "OneTimeTearDown: Assert.That(status) HTTP status: expected 200, actual 404",
            _ => "Assert.That(something) unknown assertion failure",
        };
        WriteTrx("raw.trx", [("Read(CASE-1)", "Failed", "Vivarium.TeamCity.Conformance.RawTeamCityTests", message)]);
        receipt["results"]!["raw"] = Result("raw.trx", 1, failed: true);
        receipt["results"]!["raw"]!["failures"]!.AsArray().Add(new JsonObject { ["name"] = "Read(CASE-1)", ["message"] = message,
            ["classification"] = "http-response-mismatch", ["stage"] = "response-assertion", ["expected_status"] = 200, ["actual_status"] = 404 });
        var report = Read();
        Assert.That(report.Errors.Count, Is.EqualTo(behavioral ? 0 : 1));
        Assert.That(report.ProductRedCases, Is.EqualTo(behavioral ? 1 : 0));
        Assert.That(report.HarnessFailureCases, Is.EqualTo(behavioral ? 0 : 1));
    }

    private Report Read()
    {
        Write("receipt.json", receipt.ToJsonString());
        evidence["receipt_sha256"] = Hash("receipt.json");
        Write(Ledger.Workstream + "api-evidence.json", evidence.ToJsonString());
        return Ledger.Read(root, "candidate", _ => identity);
    }
    private JsonObject Result(string path, int count, bool failed = false) => new()
    {
        ["artifact"] = path, ["sha256"] = Hash(path), ["exit_code"] = failed ? 1 : 0,
        ["counters"] = new JsonObject { ["total"] = count }, ["failures"] = new JsonArray(),
    };
    private void Table(string path, params string[] lines) => Write(Ledger.Workstream + path, string.Join('\n', lines) + "\n");
    private void ReplaceTable(string path, string from, string to)
    {
        var full = Path.Combine(root, Ledger.Workstream + path);
        File.WriteAllText(full, File.ReadAllText(full).Replace(from, to, StringComparison.Ordinal));
    }
    private void AppendTable(string path)
    {
        var full = Path.Combine(root, Ledger.Workstream + path);
        File.AppendAllText(full, File.ReadAllLines(full)[1] + "\n");
    }
    private void Write(string path, string value)
    {
        var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, value);
    }
    private string Hash(string path) => Sha(File.ReadAllBytes(Path.Combine(root, path)));
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private void WriteTrx(string path, (string Name, string Outcome, string Class, string Message)[] rows)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var document = new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "TestDefinitions", rows.Select((r, i) => new XElement(ns + "UnitTest", new XAttribute("id", i),
                new XElement(ns + "TestMethod", new XAttribute("className", r.Class))))),
            new XElement(ns + "Results", rows.Select((r, i) => new XElement(ns + "UnitTestResult", new XAttribute("testId", i),
                new XAttribute("testName", r.Name), new XAttribute("outcome", r.Outcome),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", r.Message)))))),
            new XElement(ns + "ResultSummary", new XElement(ns + "Counters", new XAttribute("total", rows.Length),
                new XAttribute("passed", rows.Count(r => r.Outcome == "Passed")), new XAttribute("failed", rows.Count(r => r.Outcome == "Failed"))))));
        Write(path, document.ToString());
    }
}
