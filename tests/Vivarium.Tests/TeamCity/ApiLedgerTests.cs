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
        CreateStorySourceLinks();
        CreateCliSourceAllocation();
        CreateSupplementalSourceAllocation();
        RebindMetadataInputs();
    }

    private void CreateStorySourceLinks()
    {
        Table("stories.tsv", "story_id\tissue\tcanonical_ac\tapplicable_tcm_from_ac",
            "US-001\t86\tAC-INS-01\tTCM-08");
        Table("story-scenarios.tsv", "story_id\tstory_issue\tscenario_id\tsurface\tsource_ids\tretained_case_ids\tcandidate_canonical_ac\tcandidate_model_invariants\tindependent_oracle_and_boundary\tvariant_freeze\tstate",
            "US-001\t86\tUS-001/server\tcompatible-rest\tTCREST:rootapi.getApiVersion\tCASE-1;CASE-2\tAC-INS-01\tTCM-08\tIndependent captured bytes; synthetic accounting input\tUNFROZEN\tSCENARIO_INTENT_NOT_BINDING_OR_COVERAGE",
            "US-001\t86\tUS-001/server\tunchanged-cli\tCLI-FIX-0001\t\tAC-INS-01\tTCM-08\tPinned client source, no executed CLI effect\tUNFROZEN\tSCENARIO_INTENT_NOT_BINDING_OR_COVERAGE");
        Table("story-links.tsv", "source_id\tstory_id\tscenario_id\tsubsystem_id\tactive_owner_issue\tcase_id\tstate",
            "TCREST:rootapi.getApiVersion\tUS-001\tUS-001/server\tSS-PROTO\t12\tCASE-1;CASE-2\tcandidate-intent-allocation-not-coverage");
        Table("installed-http-occurrences.tsv", "profile\tmethod\tpath\tjson_pointer\tdeclared_operation_id\tschema_sha256",
            $"reference\tGET\t/app/rest/apiVersion\t/paths/~1app~1rest~1apiVersion/get\tgetApiVersion\t{Hash("input.cs")}");
        Table("source-relations.tsv", "api_id\tprofile\tmethod\tpath\tjson_pointer\tdeclared_operation_id\tgap_id\tstate",
            "TCREST:rootapi.getApiVersion\treference\tGET\t/app/rest/apiVersion\t/paths/~1app~1rest~1apiVersion/get\tgetApiVersion\t\tcandidate-source-relation-not-normalized-or-covered");
        Table("rest-fixture-inventory.tsv", "id\tkind\tmethod\tpath\tsource\ttable\tcase_index\tcommand",
            "CLI-FIX-0001\trequest\tGET\t/app/rest/apiVersion\tcli.ts\tcases\t0\tserver version");
        Table("cli-source-relations.tsv", "fixture_id\tkind\tmethod\tconcrete_path\tsource\ttable\tcase_index\tcommand_prefix\tcandidate_schema_paths\tcandidate_api_ids\tpreferred_api_id\tpreferred_schema_path\tstory_id\tscenario_id\tactive_owner_issue\tgap_id\tstate",
            "CLI-FIX-0001\trequest\tGET\t/app/rest/apiVersion\tcli.ts\tcases\t0\tserver version\t/app/rest/apiVersion\tTCREST:rootapi.getApiVersion\tTCREST:rootapi.getApiVersion\t/app/rest/apiVersion\tUS-001\tUS-001/server\t12\tGAP-CLI\tCANDIDATE_SOURCE_JOIN_NOT_VARIANT_OR_COVERAGE");
        Table("native-http-occurrences.tsv", "source_id\tstory_id\tscenario_id\tsubsystem_id");
        var testSourceHash = new string('a', 64);
        var testSourceRevision = new string('b', 40);
        const string testSource = "integrations/teamcity/tests/example.test.ts";
        Table("cli-test-sources.tsv", "source_id\tsource\tsource_revision\tsource_sha256\tbytes\tstate\tinline_handlers",
            $"CLI-TEST-SOURCE:{testSource}\t{testSource}\t{testSourceRevision}\t{testSourceHash}\t100\tPINNED_TEST_SOURCE_NOT_SERVER_ORACLE\t1");
        Table("cli-inline-handlers.tsv", "source_id\tsource\tsource_revision\tsource_sha256\tline\tcolumn\tmethod_expression\tpath_expression\tenclosing_test\tdeclaration_sha256\tprimary_subsystem\tgap_id\tstate",
            $"CLI-INLINE:{testSource}:1:1\t{testSource}\t{testSourceRevision}\t{testSourceHash}\t1\t1\tget\t/app/rest/apiVersion\texample\t{testSourceHash}\tSS-PROTO\tGAP-CLI\tSYNTHETIC_HANDLER_SYNTAX_NOT_REQUEST_VARIANT_OR_SERVER_EXPECTATION");
        Table("agent-message-occurrences.tsv", "source_id\tstory_id\tscenario_id");
        Table("story-gaps.tsv", "gap_id\tstory_ids\towner_task\tdue_freeze\tnext_proof\tsource_ref\tstate",
            "GAP-CLI\tUS-001\t36\tP0 impact assessment remains due\tActual source-backed effect\tcli.ts\tOPEN-NOT-COVERAGE");
        Table("gap-dispositions.tsv", "gap_id\tprimary_subsystem\trelated_subsystems\tsource_refs\tauthority\tearliest_integration_slice\tearly_implementation_issue\tearly_contract_issue\tearly_acceptance_issue\tfull_owner_slice\tfull_implementation_issue\tfull_contract_issue\tfull_acceptance_issue\tearly_impact\tearly_required_subset\tremaining_dimensions\tdue_freeze\tassessor\trationale\tevidence_refs\tstate",
            "GAP-CLI\tSS-PROTO\t\tcli.ts\tNo business state authority\tSS-PROTO@P0\t12\t33\t42\tSS-PROTO@P0\t12\t33\t42\tUNKNOWN\tSelected early requests\tRemaining source branches\tP0\tSynthetic accounting fixture\tUnknown impact remains early\tcli.ts\tOPEN_EARLY_IMPACT");
        Table("story-implementation-links.tsv", "story_id\tstory_issue\tslice_id\timplementation_issue\tcontract_issue\tacceptance_issue",
            "US-001\t86\tSS-PROTO@P0\t12\t33\t42");
        Table("story-dependencies.tsv", "story_id\tdependency_ref\trelation\tdependent_stage\trequired_output_or_contribution\towner_task\treadiness_or_unblock_condition\tclosure_semantics",
            "US-001\tnone\tassessed-dependency-basis\tP0\tOwned ready inputs\t36\tActual declared output, not task closure\tNo whole-story closure blocker");
    }

    [TearDown]
    public void Remove_isolated_files() => Directory.Delete(root, recursive: true);

    private void CreateCliSourceAllocation()
    {
        var revision = new string('b', 40); var hash = new string('a', 64);
        const string source = "integrations/teamcity/src/client.ts";
        Write(Ledger.Workstream + "source-union-checkpoint.json", new JsonObject
        {
            ["cli_source_revision"] = revision,
            ["cli_implementation_files"] = new JsonArray(new JsonObject { ["path"] = source, ["sha256"] = hash }),
        }.ToJsonString());
        Table("cli-client-methods.tsv", "method_id\tsource\tsource_revision\tsource_sha256\tmethod\tdeclaration_sha256",
            $"CLI-METHOD:currentUser\t{source}\t{revision}\t{hash}\tcurrentUser\t{hash}");
        Table("cli-client-calls.tsv", "site_id\tsource\tsource_revision\tsource_sha256\towner_method\tcalled_method",
            $"CLI-CALL:1:1\t{source}\t{revision}\t{hash}\tcurrentUser\tcurrentUser");
        Table("cli-path-fragments.tsv", "fragment_id\tsource\tsource_revision\tsource_sha256",
            $"CLI-PATH:{source}:1:1\t{source}\t{revision}\t{hash}");
        Table("cli-method-allocation.tsv", "method_id\tprimary_source_subsystem\trelated_source_subsystems\tfull_owner_slice\tfull_implementation_issue\tfull_contract_issue\tfull_acceptance_issue\tcandidate_entry_slices\trationale\tremaining_variant_owner_basis\tstate",
            "CLI-METHOD:currentUser\tSS-PROTO\t\tSS-PROTO@P0\t12\t33\t42\tSS-PROTO@P0\tSynthetic source responsibility\tapi-ledger.tsv\tALLOCATED_SOURCE_ONLY_NOT_DISPATCH_OR_ACCEPTANCE");
        Table("cli-file-allocation.tsv", "source\tsource_revision\tsource_sha256\tprimary_source_subsystem\trelated_source_subsystems\tfull_owner_slice\tfull_implementation_issue\tfull_contract_issue\tfull_acceptance_issue\trationale\tstate",
            $"{source}\t{revision}\t{hash}\tSS-PROTO\t\tSS-PROTO@P0\t12\t33\t42\tSynthetic source responsibility\tALLOCATED_SOURCE_ONLY_NOT_DISPATCH_OR_ACCEPTANCE");
    }

    [TestCase("missing-method")]
    [TestCase("unknown-owner")]
    [TestCase("wrong-task")]
    [TestCase("unknown-entry")]
    [TestCase("orphan-call")]
    [TestCase("wrong-source-hash")]
    [TestCase("missing-file")]
    [TestCase("false-acceptance")]
    public void Known_CLI_sources_cannot_lose_ownership_or_become_dispatch_acceptance(string corruption)
    {
        switch (corruption)
        {
            case "missing-method": ReplaceTable("cli-method-allocation.tsv", "CLI-METHOD:currentUser", "CLI-METHOD:missing"); break;
            case "unknown-owner": ReplaceTable("cli-method-allocation.tsv", "\tSS-PROTO\t", "\tSS-MISSING\t"); break;
            case "wrong-task": ReplaceTable("cli-method-allocation.tsv", "\t33\t", "\t999\t"); break;
            case "unknown-entry": ReplaceTable("cli-method-allocation.tsv", "\tSS-PROTO@P0\tSynthetic", "\tSS-MISSING@R0\tSynthetic"); break;
            case "orphan-call": ReplaceTable("cli-client-calls.tsv", "\tcurrentUser\tcurrentUser", "\tcurrentUser\tmissing"); break;
            case "wrong-source-hash": ReplaceTable("cli-path-fragments.tsv", new string('a', 64), new string('c', 64)); break;
            case "missing-file": ReplaceTable("cli-file-allocation.tsv", "src/client.ts", "src/missing.ts"); break;
            case "false-acceptance": ReplaceTable("cli-method-allocation.tsv", "ALLOCATED_SOURCE_ONLY_NOT_DISPATCH_OR_ACCEPTANCE", "GREEN_SERVER_DISPATCH"); break;
        }
        RebindMetadataInputs(); // A correct current hash must not hide an invalid allocation.
        var report = Read();
        Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.ProductGreenCases, Is.Zero);
    }

    private static readonly string[] SupplementalFixtureInputs =
        ["producer-source.json", "selected-cli-source.json", "selected-core-blobs.json", "src/Fixture.cs", "tests/FixtureTests.cs"];

    private void CreateSupplementalSourceAllocation()
    {
        var revision = new string('b', 40); var hash = new string('a', 64);
        Write("producer-source.json", new JsonObject { ["files"] = new JsonArray(new JsonObject
            { ["repository"] = "sample", ["revision"] = revision, ["path"] = "build.cs", ["sha256"] = hash }) }.ToJsonString());
        Write("selected-cli-source.json", new JsonObject { ["selectedSourceRevision"] = revision,
            ["coreChangedSources"] = new JsonArray("packages/core/src/example.ts") }.ToJsonString());
        Write("selected-core-blobs.json", new JsonObject { ["revision"] = revision, ["files"] = new JsonArray(new JsonObject
            { ["path"] = "packages/core/src/example.ts", ["sha256"] = hash }) }.ToJsonString());
        JsonObject Reference(string path) => new() { ["path"] = path, ["sha256"] = Hash(path) };
        Write(Ledger.Workstream + "supplemental-source-inputs.json", new JsonObject
        {
            ["producer_source"] = Reference("producer-source.json"),
            ["selected_source_delta"] = Reference("selected-cli-source.json"),
            ["selected_core_blobs"] = Reference("selected-core-blobs.json"),
        }.ToJsonString());
        Write(Ledger.Workstream + "r0-client-selection.json", new JsonObject { ["independent_verification"] = new JsonObject
            { ["source_difference_assessment"] = Reference("selected-cli-source.json") } }.ToJsonString());
        const string header = "source_kind\tsource_id\trepository\tsource_revision\tsource_path\tsource_sha256\tprimary_source_subsystem\trelated_source_subsystems\tfull_owner_slice\tfull_implementation_issue\tfull_contract_issue\tfull_acceptance_issue\tearliest_integration_slice\tearly_implementation_issue\tearly_contract_issue\tearly_acceptance_issue\trationale\tstate";
        var suffix = "\tSS-PROTO\t\tSS-PROTO@P0\t12\t33\t42\tSS-PROTO@P0\t12\t33\t42\tSynthetic source allocation\tALLOCATED_SOURCE_ONLY_NOT_DISPATCH_OR_ACCEPTANCE";
        Table("supplemental-source-allocation.tsv", header,
            $"producer-consumer\tsample@{revision}:build.cs\tsample\t{revision}\tbuild.cs\t{hash}" + suffix,
            $"selected-core-change\tiXab3r/EyeAuras.CliFactory@{revision}:packages/core/src/example.ts\tiXab3r/EyeAuras.CliFactory\t{revision}\tpackages/core/src/example.ts\t{hash}" + suffix);
        Write("src/Fixture.cs", "Synthetic preserved source"); Write("tests/FixtureTests.cs", "Synthetic baseline anchor; not execution proof");
        Table("source-inventory.tsv", "path\tbucket", "src/Fixture.cs\tproduction");
        Table("source-disposition.tsv", "path\tsha256\tsubsystem_id\treplacement_task\tbaseline_test_files\tproof_state",
            $"src/Fixture.cs\t{Hash("src/Fixture.cs")}\tSS-PROTO\t12\ttests/FixtureTests.cs\tSOURCE_DISPOSITION_ONLY_NOT_REPLACEMENT_ACCEPTANCE");
    }

    [TestCase("missing-producer")]
    [TestCase("missing-core")]
    [TestCase("changed-reference")]
    [TestCase("missing-preservation")]
    [TestCase("missing-baseline")]
    [TestCase("false-acceptance")]
    public void Known_external_and_preserved_sources_cannot_be_dropped_or_counted_as_acceptance(string corruption)
    {
        switch (corruption)
        {
            case "missing-producer": ReplaceTable("supplemental-source-allocation.tsv", "sample@", "missing@"); break;
            case "missing-core": ReplaceTable("supplemental-source-allocation.tsv", "packages/core/src/example.ts", "packages/core/src/missing.ts"); break;
            case "changed-reference": ReplaceTable("supplemental-source-allocation.tsv", new string('a', 64), new string('c', 64)); break;
            case "missing-preservation": ReplaceTable("source-disposition.tsv", "src/Fixture.cs", "src/missing.cs"); break;
            case "missing-baseline": File.Delete(Path.Combine(root, "tests/FixtureTests.cs")); break;
            case "false-acceptance": ReplaceTable("supplemental-source-allocation.tsv", "ALLOCATED_SOURCE_ONLY_NOT_DISPATCH_OR_ACCEPTANCE", "GREEN"); break;
        }
        RebindMetadataInputs();
        var report = Read();
        Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.ProductGreenCases, Is.Zero);
    }

    [TestCase("missing-disposition")]
    [TestCase("wrong-owner")]
    [TestCase("wrong-task")]
    [TestCase("lost-source")]
    [TestCase("false-closure")]
    [TestCase("unknown-impact-deferred")]
    public void Source_gap_accountability_cannot_be_lost_or_promoted_to_acceptance(string corruption)
    {
        switch (corruption)
        {
            case "missing-disposition":
                var path = Path.Combine(root, Ledger.Workstream + "gap-dispositions.tsv");
                File.WriteAllText(path, File.ReadLines(path).First() + "\n"); break;
            case "wrong-owner": ReplaceTable("gap-dispositions.tsv", "GAP-CLI\tSS-PROTO", "GAP-CLI\tSS-MISSING"); break;
            case "wrong-task": ReplaceTable("gap-dispositions.tsv", "\t12\t33\t42\t", "\t12\t999\t42\t"); break;
            case "lost-source": ReplaceTable("gap-dispositions.tsv", "\tcli.ts\tNo business", "\tother.ts\tNo business"); break;
            case "false-closure": ReplaceTable("gap-dispositions.tsv", "OPEN_EARLY_IMPACT", "CLOSED"); break;
            case "unknown-impact-deferred":
                var slices = Path.Combine(root, Ledger.Workstream + "subsystem-slices.tsv");
                File.AppendAllText(slices, "SS-PROTO@R5\tSS-PROTO\tR5\t12\t12\t33\t42\tUNVERIFIED\n");
                var blocks = Path.Combine(root, Ledger.Workstream + "api-blocks.tsv");
                File.AppendAllText(blocks, "R5\tSS-PROTO@R5\t33\t12\t42\tUNVERIFIED\tunfrozen\n");
                ReplaceTable("gap-dispositions.tsv", "No business state authority\tSS-PROTO@P0", "No business state authority\tSS-PROTO@R5"); break;
        }
        RebindMetadataInputs();
        var report = Read();
        Assert.That(report.Status, Is.EqualTo("INVALID_ACCOUNTING"));
        Assert.That(report.ProductGreenCases, Is.Zero);
        if (corruption == "unknown-impact-deferred") Assert.That(report.Errors, Has.Some.Contains("Unknown early impact cannot be deferred"));
    }

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

    [TestCase("omitted-handler")]
    [TestCase("wrong-source-hash")]
    [TestCase("orphan-source")]
    [TestCase("unowned-gap")]
    [TestCase("false-server-oracle")]
    [TestCase("duplicate-source")]
    [TestCase("invalid-revision")]
    public void Inline_client_source_omission_or_promotion_cannot_preserve_GREEN(string corruption)
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
        switch (corruption)
        {
            case "omitted-handler":
                var path = Path.Combine(root, Ledger.Workstream + "cli-inline-handlers.tsv");
                File.WriteAllText(path, File.ReadLines(path).First() + "\n"); break;
            case "wrong-source-hash": ReplaceTable("cli-inline-handlers.tsv", new string('a', 64), new string('c', 64)); break;
            case "orphan-source": ReplaceTable("cli-inline-handlers.tsv", "example.test.ts", "missing.test.ts"); break;
            case "unowned-gap": ReplaceTable("cli-inline-handlers.tsv", "GAP-CLI", "MISSING-GAP"); break;
            case "false-server-oracle": ReplaceTable("cli-inline-handlers.tsv", "SYNTHETIC_HANDLER_SYNTAX_NOT_REQUEST_VARIANT_OR_SERVER_EXPECTATION", "SERVER_EXPECTATION_GREEN"); break;
            case "duplicate-source":
                var sources = Path.Combine(root, Ledger.Workstream + "cli-test-sources.tsv");
                File.AppendAllText(sources, File.ReadLines(sources).Skip(1).First().Replace("CLI-TEST-SOURCE:", "INVALID:", StringComparison.Ordinal) + "\n"); break;
            case "invalid-revision": ReplaceTable("cli-test-sources.tsv", new string('b', 40), new string('z', 40)); break;
        }
        RebindMetadataInputs();
        var report = Read();
        Assert.That(report.Status, Is.EqualTo("INVALID_ACCOUNTING"));
        Assert.That(report.ProductGreenCases, Is.Zero);
    }

    [Test]
    public void Missing_story_discovery_link_cannot_preserve_current_GREEN()
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1), "begin with a valid bounded accounting fixture");
        Table("story-links.tsv", "source_id\tstory_id\tscenario_id\tsubsystem_id\tactive_owner_issue\tcase_id\tstate");
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Not.Empty, "the entire discovery source cannot disappear from the story graph");
            Assert.That(report.ProductGreenCases, Is.Zero);
        });
    }

    [TestCase("duplicate-source")]
    [TestCase("orphan-story")]
    [TestCase("missing-scenario")]
    [TestCase("wrong-source-owner")]
    [TestCase("lost-api-case-link")]
    [TestCase("lost-scenario-case")]
    [TestCase("orphan-scenario-source")]
    [TestCase("missing-ac")]
    [TestCase("wrong-model")]
    [TestCase("missing-oracle-description")]
    [TestCase("false-frozen-scenario")]
    [TestCase("wrong-acceptance-task")]
    [TestCase("implementation-as-acceptance")]
    [TestCase("wrong-cli-preferred-path")]
    [TestCase("changed-cli-occurrence")]
    [TestCase("unowned-gap")]
    [TestCase("unproved-gap-closure")]
    [TestCase("invalid-schema-pointer")]
    [TestCase("invalid-schema-hash")]
    [TestCase("orphan-dependency-story")]
    [TestCase("unknown-dependency-task")]
    [TestCase("issue-closure-edge")]
    public void Broken_story_source_join_never_preserves_current_GREEN(string corruption)
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
        switch (corruption)
        {
            case "duplicate-source":
                var sourcePath = Path.Combine(root, Ledger.Workstream, "story-links.tsv");
                File.AppendAllText(sourcePath, File.ReadAllLines(sourcePath)[1] + "\n");
                break;
            case "orphan-story": ChangeStoryCell("story-links.tsv", "story_id", "US-404"); break;
            case "missing-scenario": ChangeStoryCell("story-links.tsv", "scenario_id", "US-001/absent"); break;
            case "wrong-source-owner": ChangeStoryCell("story-links.tsv", "active_owner_issue", "999"); break;
            case "lost-api-case-link": ChangeStoryCell("story-links.tsv", "case_id", Ready); break;
            case "lost-scenario-case": ChangeStoryCell("story-scenarios.tsv", "retained_case_ids", Ready); break;
            case "orphan-scenario-source": ChangeStoryCell("story-scenarios.tsv", "source_ids", "TCREST:absent.read"); break;
            case "missing-ac": ChangeStoryCell("story-scenarios.tsv", "candidate_canonical_ac", ""); break;
            case "wrong-model": ChangeStoryCell("story-scenarios.tsv", "candidate_model_invariants", "TCM-999"); break;
            case "missing-oracle-description": ChangeStoryCell("story-scenarios.tsv", "independent_oracle_and_boundary", ""); break;
            case "false-frozen-scenario": ChangeStoryCell("story-scenarios.tsv", "variant_freeze", "FROZEN-GREEN"); break;
            case "wrong-acceptance-task": ChangeStoryCell("story-implementation-links.tsv", "acceptance_issue", "35"); break;
            case "implementation-as-acceptance": ChangeStoryCell("rest-first-cohort-cases.tsv", "acceptance_task", "35"); break;
            case "wrong-cli-preferred-path": ChangeStoryCell("cli-source-relations.tsv", "preferred_schema_path", "/app/rest/absent"); break;
            case "changed-cli-occurrence": ChangeStoryCell("cli-source-relations.tsv", "command_prefix", "invented command"); break;
            case "unowned-gap": ChangeStoryCell("story-gaps.tsv", "owner_task", ""); break;
            case "unproved-gap-closure": ChangeStoryCell("story-gaps.tsv", "state", "CLOSED-GREEN"); break;
            case "invalid-schema-pointer": ChangeStoryCell("installed-http-occurrences.tsv", "json_pointer", "/paths/guessed"); break;
            case "invalid-schema-hash": ChangeStoryCell("installed-http-occurrences.tsv", "schema_sha256", "missing"); break;
            case "orphan-dependency-story": ChangeStoryCell("story-dependencies.tsv", "dependency_ref", "US-404"); break;
            case "unknown-dependency-task": ChangeStoryCell("story-dependencies.tsv", "dependency_ref", "#999"); break;
            case "issue-closure-edge": ChangeStoryCell("story-dependencies.tsv", "relation", "blocks-until-whole-story-closed"); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Not.Empty);
            Assert.That(report.ProductGreenCases, Is.Zero);
            Assert.That(report.GlobalApiComplete, Is.False);
        });
    }

    [Test]
    public void Related_scenarios_do_not_double_count_cases_or_accept_an_independent_entry()
    {
        File.AppendAllText(Path.Combine(root, Ledger.Workstream, "story-scenarios.tsv"),
            "US-001\t86\tUS-001/related\tcompatible-rest\tTCREST:rootapi.getApiVersion\tCASE-1\tAC-INS-01\tTCM-08\tRelated reference support, not another execution\tUNFROZEN\tSCENARIO_INTENT_NOT_BINDING_OR_COVERAGE\n");
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Empty);
            Assert.That(report.ActorGoalStories, Is.EqualTo(1));
            Assert.That(report.SurfaceScenarioCandidates, Is.EqualTo(3));
            Assert.That(report.DistinctScenarioLinkedCases, Is.EqualTo(2));
            Assert.That(report.MaterializedCases, Is.EqualTo(2));
            Assert.That(report.ProductGreenCases, Is.EqualTo(1));
            Assert.That(report.GlobalApiComplete, Is.False);
            Assert.That(report.GlobalCompatibilityPercent, Is.Null);
            Assert.That(report.StorySourceJoinState, Does.Contain("NOT_PROVED"));
        });
    }

    [Test]
    public void Shared_operation_name_keeps_distinct_source_occurrences_without_a_semantic_merge()
    {
        File.AppendAllText(Path.Combine(root, Ledger.Workstream, "installed-http-occurrences.tsv"),
            $"reference\tGET\t/app/rest/anotherVersion\t/paths/~1app~1rest~1anotherVersion/get\tgetApiVersion\t{Hash("input.cs")}\n");
        File.AppendAllText(Path.Combine(root, Ledger.Workstream, "source-relations.tsv"),
            "TCREST:rootapi.getApiVersion\treference\tGET\t/app/rest/anotherVersion\t/paths/~1app~1rest~1anotherVersion/get\tgetApiVersion\tGAP-CLI\tcandidate-source-relation-not-normalized-or-covered\n");
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Empty);
            Assert.That(report.DiscoveryRows, Is.EqualTo(1));
            Assert.That(report.SchemaSourceOccurrences, Is.EqualTo(2));
            Assert.That(report.MaterializedCases, Is.EqualTo(2));
            Assert.That(report.NormalizedOperationVariantDenominator, Is.Null);
        });
    }

    [Test]
    public void Missing_CLI_reverse_scenario_source_is_not_a_complete_join()
    {
        Assert.That(Read().Errors, Is.Empty);
        ChangeStoryCell("story-scenarios.tsv", "source_ids", "", row: 2);
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Not.Empty);
            Assert.That(report.ProductGreenCases, Is.Zero);
        });
    }

    [Test]
    public void Different_schema_bytes_cannot_share_one_pinned_source_profile()
    {
        File.AppendAllText(Path.Combine(root, Ledger.Workstream, "installed-http-occurrences.tsv"),
            $"reference\tGET\t/app/rest/anotherVersion\t/paths/~1app~1rest~1anotherVersion/get\tgetApiVersion\t{new string('0', 64)}\n");
        File.AppendAllText(Path.Combine(root, Ledger.Workstream, "source-relations.tsv"),
            "TCREST:rootapi.getApiVersion\treference\tGET\t/app/rest/anotherVersion\t/paths/~1app~1rest~1anotherVersion/get\tgetApiVersion\tGAP-CLI\tcandidate-source-relation-not-normalized-or-covered\n");
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Not.Empty);
            Assert.That(report.ProductGreenCases, Is.Zero);
        });
    }

    [Test]
    public void Changed_valid_story_input_requires_an_explicit_evidence_assessment()
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
        ChangeStoryCell("story-scenarios.tsv", "independent_oracle_and_boundary", "A changed declared evidence boundary");
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Has.Some.StartsWith("STALE:"));
            Assert.That(report.ProductGreenCases, Is.Zero);
            Assert.That(report.RequiredInputPaths, Does.Contain(Ledger.Workstream + "story-scenarios.tsv"));
        });
    }

    [Test]
    public void Added_producer_source_cannot_hide_behind_an_old_input_manifest()
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
        Write("new-helper.cs", "new producer source");
        identity = identity with { RequiredInputPaths = ["input.cs", "new-helper.cs"] };
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Has.Some.StartsWith("STALE:"));
            Assert.That(report.ProductGreenCases, Is.Zero);
            Assert.That(report.RequiredInputPaths, Does.Contain("new-helper.cs"));
        });
    }

    [Test]
    public void A_self_consistent_receipt_cannot_omit_a_consumed_metadata_input()
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
        var inputs = receipt["candidate"]!["source_inputs"]!.AsArray();
        inputs.Remove(inputs.Single(item => item!["path"]!.GetValue<string>() == Ledger.Workstream + "story-links.tsv"));
        var fingerprint = Sha(Encoding.UTF8.GetBytes(string.Join('\n', inputs.Select(input =>
            input!["path"]!.GetValue<string>() + ":" + input["sha256"]!.GetValue<string>()))));
        receipt["candidate"]!["source_fingerprint_sha256"] = fingerprint;
        evidence["decision"]!["from_source_fingerprint"] = fingerprint;
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Has.Some.StartsWith("STALE:"));
            Assert.That(report.ProductGreenCases, Is.Zero);
        });
    }

    [Test]
    public void Missing_dependency_target_is_not_an_assessed_readiness_basis()
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1));
        ChangeStoryCell("story-dependencies.tsv", "dependency_ref", "");
        RebindMetadataInputs();
        var report = Read();
        Assert.Multiple(() =>
        {
            Assert.That(report.Errors, Is.Not.Empty);
            Assert.That(report.ProductGreenCases, Is.Zero);
        });
    }

    private void ChangeStoryCell(string name, string column, string value, int row = 1)
    {
        var path = Path.Combine(root, Ledger.Workstream, name);
        var lines = File.ReadAllLines(path);
        var index = Array.IndexOf(lines[0].Split('\t'), column);
        Assert.That(index, Is.GreaterThanOrEqualTo(0));
        var cells = lines[row].Split('\t');
        cells[index] = value;
        lines[row] = string.Join('\t', cells);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    [Test]
    public void Missing_oracle_cannot_match_a_declared_missing_hash_sentinel()
    {
        Assert.That(Read().ProductGreenCases, Is.EqualTo(1), "start from valid GREEN evidence");
        var inputs = receipt["candidate"]!["source_inputs"]!.AsArray();
        inputs.Add(new JsonObject { ["path"] = "missing-oracle.json", ["sha256"] = "MISSING" });
        var fingerprint = Sha(Encoding.UTF8.GetBytes(string.Join('\n', inputs.Select(input =>
            input!["path"]!.GetValue<string>() + ":" + input["sha256"]!.GetValue<string>()))));
        receipt["candidate"]!["source_fingerprint_sha256"] = fingerprint;
        evidence["decision"]!["from_source_fingerprint"] = fingerprint;
        receipt["reference"]!["capture"] = "missing-oracle.json";
        receipt["reference"]!["capture_sha256"] = "MISSING";

        var report = Read();
        Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.ProductGreenCases, Is.Zero);
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
            case "missing-production-fingerprint": identity = identity with { RequiredInputPaths = ["input.cs", "unfingerprinted.cs"] }; break;
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

    private void RebindMetadataInputs()
    {
        var inputs = receipt["candidate"]!["source_inputs"]!.AsArray();
        foreach (var input in inputs.Where(input => input!["path"]!.GetValue<string>().StartsWith(Ledger.Workstream, StringComparison.Ordinal) ||
            SupplementalFixtureInputs.Contains(input["path"]!.GetValue<string>())).ToArray())
            inputs.Remove(input);
        foreach (var path in Directory.GetFiles(Path.Combine(root, Ledger.Workstream), "*.tsv")
            .Concat(new[] { "source-union-checkpoint.json", "supplemental-source-inputs.json", "r0-client-selection.json" }
                .Select(path => Path.Combine(root, Ledger.Workstream + path)))
            .Concat(SupplementalFixtureInputs.Select(path => Path.Combine(root, path)).Where(File.Exists)).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            inputs.Add(new JsonObject { ["path"] = relative, ["sha256"] = Hash(relative) });
        }
        var fingerprint = Sha(Encoding.UTF8.GetBytes(string.Join('\n', inputs.Select(input =>
            input!["path"]!.GetValue<string>() + ":" + input["sha256"]!.GetValue<string>()))));
        receipt["candidate"]!["source_fingerprint_sha256"] = fingerprint;
        evidence["decision"]!["from_source_fingerprint"] = fingerprint;
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
