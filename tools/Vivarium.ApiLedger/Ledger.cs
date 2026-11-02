using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Vivarium.ApiLedger;

public sealed record Identity(string Head, string ProductionTree, bool ProductionDirty, string[]? RequiredInputPaths = null);
public sealed record CaseResult(string CaseId, string PrimaryOwner, string State, string ReplayState);
public sealed record Rollup(string Id, int? DiscoveryRows, int KnownCases, int Executed, int Green, int Red,
    int Blocked, int Stale, bool ApiComplete = false, string ScopeState = "unfrozen");
public sealed record ObservedResult(string TestName, string Outcome);
public sealed class Report
{
    public string Status { get; set; } = "PARTIAL_BASELINE_P0_OPEN";
    public required string Candidate { get; init; }
    public string? ObservedHead { get; set; }
    public string? ExecutionBase { get; set; }
    public string? ExecutionSourceFingerprint { get; set; }
    public string EvidenceMode { get; set; } = "REUSED; no tests executed by this command";
    public string? RawArtifact { get; set; }
    public string? ReplayArtifact { get; set; }
    public string ReviewState => "NOT_REVIEWED";
    public string DebtState => "NOT_ASSESSED";
    public int SubsystemSlices { get; set; }
    public int IntegrationContributionRows { get; set; }
    public int ActorGoalStories { get; set; }
    public int SurfaceScenarioCandidates { get; set; }
    public int StoryDependencyReferences { get; set; }
    public int SchemaSourceOccurrences { get; set; }
    public int CliFixtureSourceOccurrences { get; set; }
    public int CliTestSourceFiles { get; set; }
    public int CliInlineHandlerSourceOccurrences { get; set; }
    public int CliImplementationFileAllocationRows { get; set; }
    public int CliMethodAllocationRows { get; set; }
    public int CliCallSiteAllocationRows { get; set; }
    public int CliPathFragmentAllocationRows { get; set; }
    public int CliPotentialEarlyMethods { get; set; }
    public int ProducerConsumerSourceAllocationRows { get; set; }
    public int SelectedCoreSourceAllocationRows { get; set; }
    public int ProductionSourceDispositionRows { get; set; }
    public string KnownSourceAllocationState { get; set; } = "UNVERIFIED";
    public int NativeHttpSourceOccurrences { get; set; }
    public int AgentEnvelopeSourceOccurrences { get; set; }
    public int KnownStorySourceGaps { get; set; }
    public int SourceGapDispositionRows { get; set; }
    public int UnresolvedEarlyImpactGaps { get; set; }
    public int DistinctScenarioLinkedCases { get; set; }
    public string StorySourceJoinState { get; set; } = "UNVERIFIED";
    public List<ObservedResult> RawResults { get; } = [];
    public int DiscoveryRows { get; set; }
    public int DiscoveryRowsWithAnyBoundReferenceOrReplayCase { get; set; }
    public int DiscoveryRowsWithProductReadyRawCases { get; set; }
    public int DiscoveryRowsWithoutAnyReferenceOrReplayCaseBinding => DiscoveryRows - DiscoveryRowsWithAnyBoundReferenceOrReplayCase;
    public int? NormalizedOperationVariantDenominator => null;
    public double? GlobalCompatibilityPercent => null;
    public bool GlobalApiComplete => false;
    public string DiscoveryAllocationStatus => "UNVERIFIED; source normalization, due variants and impact assessment remain open #110/#111/#112";
    public int MaterializedCases => Cases.Count;
    public int ExecutedCases => Cases.Count(c => c.State is "GREEN" or "RED" or "HARNESS_FAILURE");
    public int ProductGreenCases => Cases.Count(c => c.State == "GREEN");
    public int ProductRedCases => Cases.Count(c => c.State == "RED");
    public int ProductPrerequisiteBlockedCases => Cases.Count(c => c.State == "PRODUCT_BLOCKED");
    public int StaleCases => Cases.Count(c => c.State == "STALE");
    public int HarnessFailureCases => Cases.Count(c => c.State == "HARNESS_FAILURE");
    public List<string> Errors { get; } = [];
    public List<string> RequiredInputPaths { get; } = [];
    public List<CaseResult> Cases { get; } = [];
    public List<Rollup> Subsystems { get; } = [];
    public List<Rollup> Blocks { get; } = [];
    public List<string> Limits { get; } = ["Accounting validates identity and provenance, not assertion/oracle correctness.",
        "Known cases are a partial cohort, not frozen global or subsystem scope.",
        "Source/story joins check declared referential links, not source-byte provenance, semantic aliases, exact actors/effects, independent surface entry or acceptance.",
        "#110/#111/#112 allocation/model/freeze and final independent milestone/Security review remain open."];
}

public static partial class Ledger
{
    public const string Workstream = ".workspace/workstreams/coherent-refactor/";

    public static Report Read(string root, string candidate, Func<string, Identity>? inspect = null, string assessmentPath = Workstream + "api-evidence.json")
    {
        var report = new Report { Candidate = candidate };
        try { ReadInto(Path.GetFullPath(root), candidate, inspect ?? InspectGit, report, assessmentPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException)
        {
            report.Errors.Add(ex.Message);
            report.Status = "INVALID_ACCOUNTING";
            // A partly traversed input is never a valid partial GREEN report.
            report.Cases.Clear();
        }
        return report;
    }

    private static void ReadInto(string root, string candidate, Func<string, Identity> inspect, Report report, string assessmentPath)
    {
        var metadataInputs = new HashSet<string>();
        var ledger = Table(root, "api-ledger.tsv", metadataInputs, "api_id");
        var sources = Table(root, "teamcity-official-rest-operations.tsv", metadataInputs, "resource", "operation");
        var owners = Table(root, "subsystems.tsv", metadataInputs, "subsystem_id");
        var slices = Table(root, "subsystem-slices.tsv", metadataInputs, "slice_id");
        var blocks = Table(root, "api-blocks.tsv", metadataInputs, "block");
        var contributions = Table(root, "subsystem-contributions.tsv", metadataInputs, "contribution_id");
        var cases = Table(root, "rest-first-cohort-cases.tsv", metadataInputs, "case_id")
            .Concat(Table(root, "rest-local-mutation-cases.tsv", metadataInputs, "case_id")).ToArray();
        Unique(cases, "case_id");
        var byCase = cases.ToDictionary(c => c["case_id"]);
        var byApi = ledger.ToDictionary(c => c["api_id"]);
        var byOwner = owners.ToDictionary(c => c["subsystem_id"]);
        var bySlice = slices.ToDictionary(c => c["slice_id"]);
        var byBlock = blocks.ToDictionary(c => c["block"]);
        Require(sources.Select(s => "TCREST:" + s["resource"] + "." + s["operation"]).ToHashSet()
            .SetEquals(byApi.Keys), "Discovery source/ledger IDs differ (missing or orphan source).");
        foreach (var slice in slices)
        {
            Require(byOwner.ContainsKey(slice["subsystem_id"]) && byBlock.ContainsKey(slice["milestone"]), "Orphan slice owner/milestone.");
            Require(Split(byBlock[slice["milestone"]]["subsystem_slices"]).Contains(slice["slice_id"]), "Missing block/slice link.");
            foreach (var key in new[] { "implementation_issues", "rest_contract_issue", "rest_acceptance_issue" })
                Tasks(slice[key]);
        }
        foreach (var block in blocks)
        {
            var ownSlices = Split(block["subsystem_slices"]).Select(id => Find(bySlice, id, "block slice")).ToArray();
            Require(ownSlices.All(s => s["milestone"] == block["block"]), "Cross-milestone block slice.");
            foreach (var pair in new[] { ("contract_issues", "rest_contract_issue"), ("verification_issues", "rest_acceptance_issue"), ("implementation_issues", "implementation_issues") })
                Require(Split(block[pair.Item1]).ToHashSet().SetEquals(ownSlices.SelectMany(s => Split(s[pair.Item2]))), "Block task rollup differs from slices.");
        }
        foreach (var row in ledger)
        {
            var owner = Find(byOwner, row["subsystem_id"], "primary owner");
            Require(row["subsystem_issue"] == owner["primary_issue"], "Wrong primary owner task.");
            var ownSlices = slices.Where(s => s["subsystem_id"] == row["subsystem_id"]);
            Require(ownSlices.Any(s => s["rest_contract_issue"] == row["rest_contract_issue"] &&
                s["rest_acceptance_issue"] == row["rest_acceptance_issue"]), "Wrong primary REST task links.");
            foreach (var related in Split(row["related_subsystems"])) Find(byOwner, related, "related owner");
            Tasks(row["discovery_contract_issue"]); Tasks(row["allocation_issue"]);
            foreach (var id in Split(row["variant_case_ids"]))
                Require(Split(Find(byCase, id, "ledger case")["api_ids"]).Contains(row["api_id"]), "Wrong reverse case binding.");
        }
        foreach (var row in cases)
        {
            Require(row["primary_owner"].Length > 0, "Missing primary case owner.");
            Find(byOwner, row["primary_owner"], "primary case owner");
            foreach (var related in Split(row["related_owner"])) Find(byOwner, related, "related case owner");
            foreach (var id in Split(row["api_ids"]))
                Require(Split(Find(byApi, id, "case API")["variant_case_ids"]).Contains(row["case_id"]), "Missing reverse case binding.");
            foreach (var task in Split(row["contract_task"]))
                Require(slices.Any(s => s["rest_contract_issue"] == task &&
                    (s["subsystem_id"] == row["primary_owner"] || Split(row["related_owner"]).Contains(s["subsystem_id"]) || s["subsystem_id"] == "SS-PROTO")), "Orphan case contract task.");
            foreach (var task in Split(row["acceptance_task"]))
                Require(slices.Any(s => s["rest_acceptance_issue"] == task &&
                    (s["subsystem_id"] == row["primary_owner"] || Split(row["related_owner"]).Contains(s["subsystem_id"]) || s["subsystem_id"] == "SS-PROTO")), "Orphan case acceptance task.");
            Tasks(row["contract_task"]); Tasks(row["acceptance_task"]);
            Require(row["asserted_effect"].Length > 0 && row["product_prerequisite"].Length > 0, "Missing effect/prerequisite binding.");
        }
        foreach (var contribution in contributions)
        {
            var owner = Find(byOwner, contribution["subsystem_id"], "contribution owner");
            var slice = Find(bySlice, contribution["integration_slice"], "contribution slice");
            Require(owner["primary_issue"] == contribution["full_owner_issue"] && slice["milestone"] == contribution["milestone"] &&
                slice["rest_contract_issue"] == contribution["rest_contract_issue"] && slice["rest_acceptance_issue"] == contribution["rest_acceptance_issue"] &&
                slice["implementation_issue"] == contribution["implementation_issue"], "Wrong contribution owner/task/milestone.");
            foreach (var task in Split(contribution["contributing_issues"]))
                Require(slices.Any(s => s["milestone"] == slice["milestone"] && Split(s["implementation_issues"]).Contains(task)), "Orphan/cross-milestone contributing task.");
            foreach (var id in Split(contribution["variant_case_ids"])) Find(byCase, id, "contribution case");
        }

        ValidateStorySources(root, byApi, byCase, byOwner, bySlice, metadataInputs, report);
        ValidateCliSourceAllocation(root, byOwner, bySlice, metadataInputs, report);
        ValidateSupplementalSourceAllocation(root, byOwner, bySlice, metadataInputs, report);
        report.KnownSourceAllocationState = "KNOWN_SOURCE_RESPONSIBILITY_ALLOCATED_NOT_VARIANT_OR_ACCEPTANCE";

        var evidence = Json(root, assessmentPath);
        Require(S(evidence, "candidate_id") == candidate, "Unknown candidate: explicit evidence decision required.");
        var scope = evidence["scope"]!;
        Require(scope["required_case_ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet().SetEquals(byCase.Keys), "Missing due case or unexpected scope change.");
        Require(scope["normalized_denominator"] is null && !scope["frozen"]!.GetValue<bool>(), "This partial foundation cannot claim frozen/known global scope.");
        Require(!scope["api_complete"]!.GetValue<bool>() && scope["compatibility_percent"] is null, "False API-complete/percentage claim from unknown scope.");
        Require(blocks.All(b => b["api_complete"] == "UNVERIFIED") && slices.All(s => s["api_complete"] == "UNVERIFIED"), "API-complete requires frozen scope, current GREEN, final reviews and zero debt; no such closure is established.");
        Require(blocks.All(b => b["scope_state"] == "unfrozen"), "Partial foundation cannot freeze a milestone by editing its status.");
        Require(S(scope, "review_state") == "NOT_REVIEWED" && S(scope, "debt_state") == "NOT_ASSESSED", "No independent review/debt-closure evidence is bound by this partial foundation.");

        var receiptPath = S(evidence, "receipt");
        Require(Hash(root, receiptPath) == S(evidence, "receipt_sha256"), "Changed execution receipt.");
        var receipt = Json(root, receiptPath);
        var execution = receipt["candidate"]!;
        var decision = evidence["decision"]!;
        report.ExecutionBase = S(execution, "git_base");
        report.ExecutionSourceFingerprint = S(execution, "source_fingerprint_sha256");
        Require(S(decision, "from_base") == report.ExecutionBase && S(decision, "from_source_fingerprint") == report.ExecutionSourceFingerprint &&
            S(decision, "to_candidate") == candidate, "Mismatched from/to execution decision.");
        Require(S(decision, "assessor").Length > 0 && S(decision, "rationale").Length > 0, "Unassessed evidence decision.");
        var action = S(decision, "action");
        Require(action is "reuse" or "invalidate", "Unknown evidence decision.");
        var impacts = decision["impacts"]!.AsObject();
        Require(new[] { "production", "shared_services", "fixture_oracle", "reference_client", "environment" }.All(impacts.ContainsKey), "Missing impact assessment.");
        var valid = action == "reuse" && impacts.All(p => p.Value!.GetValue<string>() == "unchanged");
        if (!valid) report.Errors.Add("STALE: changed/unknown impact or explicit invalidation; affected rerun required.");
        var identity = inspect(root);
        report.ObservedHead = identity.Head;
        if (identity.Head != S(evidence, "expected_head") || identity.ProductionDirty || identity.ProductionTree != S(execution, "production_tree"))
        { valid = false; report.Errors.Add("STALE: candidate revision/production/shared-service tree changed."); }
        var overrides = decision["source_overrides"]!.AsArray();
        Unique(overrides.Select(x => new Dictionary<string, string> { ["path"] = S(x!, "path") }).ToArray(), "path");
        var sourceInputs = execution["source_inputs"]!.AsArray();
        Unique(sourceInputs.Select(x => new Dictionary<string, string> { ["path"] = S(x!, "path") }), "path");
        report.RequiredInputPaths.AddRange((identity.RequiredInputPaths ?? []).Concat(metadataInputs).Distinct().Order(StringComparer.Ordinal));
        if (identity.RequiredInputPaths is null || !report.RequiredInputPaths.All(path => sourceInputs.Any(x => S(x!, "path") == path)))
        { valid = false; report.Errors.Add("STALE: execution receipt omits required producer/harness or accounting-data inputs."); }
        foreach (var item in overrides)
            Require(sourceInputs.Any(x => S(x!, "path") == S(item!, "path") && S(x!, "sha256") == S(item!, "from_sha256")) &&
                S(item!, "rationale").Length > 0, "Unknown/invalid source override.");
        var fingerprint = string.Join('\n', sourceInputs.Select(x => S(x!, "path") + ":" + S(x!, "sha256")));
        Require(Sha(Encoding.UTF8.GetBytes(fingerprint)) == report.ExecutionSourceFingerprint, "Receipt source fingerprint differs.");
        foreach (var input in sourceInputs)
        {
            var changed = overrides.SingleOrDefault(x => S(x!, "path") == S(input!, "path"));
            var expected = changed is null ? S(input!, "sha256") : S(changed, "to_sha256");
            if (Hash(root, S(input!, "path")) != expected)
            { valid = false; report.Errors.Add("STALE: source/fixture/oracle hash mismatch: " + S(input!, "path")); }
        }
        // Optional retained execution binaries prove their original bytes; mutable bin/ outputs are never current authority.
        foreach (var item in evidence["retained_assemblies"]!.AsArray())
        {
            Require(execution["assemblies"]!.AsArray().Any(x => S(x!, "path") == S(item!, "recorded_path") && S(x!, "sha256") == S(item!, "sha256")), "Wrong original assembly binding.");
            if (Hash(root, S(item!, "artifact")) != S(item!, "sha256"))
            { valid = false; report.Errors.Add("STALE: retained execution assembly hash mismatch."); }
        }
        Require(S(receipt["reference"]!, "profile") == S(evidence, "reference_profile"), "Mismatched reference profile.");
        if (S(receipt["reference"]!, "capture_sha256") != Hash(root, S(receipt["reference"]!, "capture")))
        { valid = false; report.Errors.Add("STALE: reference fixture hash mismatch."); }
        var rawEvidence = receipt["results"]!["raw"]!;
        var replayEvidence = receipt["results"]!["controls"]!;
        report.RawArtifact = S(rawEvidence, "artifact");
        report.ReplayArtifact = S(replayEvidence, "artifact");
        if (replayEvidence["reused_from"] is not null)
        {
            var reuse = evidence["replay_decision"] ?? throw new InvalidDataException("Missing replay reuse decision.");
            Require(S(reuse, "from_receipt") == S(replayEvidence, "reused_from") && Hash(root, S(reuse, "from_receipt")) == S(reuse, "receipt_sha256") &&
                S(reuse, "to_candidate") == candidate && S(reuse, "assessor").Length > 0 && S(reuse, "rationale").Length > 0, "Wrong replay reuse identity.");
            var original = Json(root, S(reuse, "from_receipt"));
            Require(S(original["results"]!["controls"]!, "artifact") == report.ReplayArtifact &&
                S(original["results"]!["controls"]!, "sha256") == S(replayEvidence, "sha256"), "Wrong replay artifact transfer.");
            if (S(reuse, "action") != "reuse" || reuse["impacts"]!.AsObject().Any(p => p.Value!.GetValue<string>() != "unchanged"))
            { valid = false; report.Errors.Add("STALE: invalid/unknown replay reuse assessment."); }
        }
        var raw = Results(root, rawEvidence, report);
        var replay = Results(root, replayEvidence, report);
        report.RawResults.AddRange(raw.Select(r => new ObservedResult(r.Name, r.Outcome)));
        var controlsHealthy = replay.All(r => r.Outcome == "Passed");
        if (!controlsHealthy) report.Errors.Add("HARNESS_FAILURE: required replay/control target has failed or unexecuted rows.");
        if (report.Errors.Any(e => e.StartsWith("STALE:"))) valid = false;
        var rawNames = new HashSet<string>();
        var replayIdentities = new HashSet<string>();
        foreach (var row in cases)
        {
            var id = row["case_id"];
            var replayName = TestName(row["replay_binding"], id);
            var replayClass = "Vivarium.Tests.TeamCity." + BindingClass(row["replay_binding"]);
            Require(replayIdentities.Add(replayClass + "/" + replayName), "Duplicate replay result identity.");
            var replayResult = replay.SingleOrDefault(r => r.Name == replayName && r.Class == replayClass);
            Require(replayResult is not null, "Missing/wrong replay test binding: " + id);
            var replayState = !valid ? "STALE" : replayResult!.Outcome == "Passed" ? "REPLAY_GREEN" : "HARNESS_FAILURE";
            string state;
            if (row["product_binding"] == "product-prerequisite-blocked") state = "PRODUCT_BLOCKED";
            else
            {
                var name = TestName(row["product_binding"], id);
                Require(rawNames.Add(name), "Duplicate product result binding.");
                var productClass = "Vivarium.TeamCity.Conformance." + BindingClass(row["product_binding"]);
                Require(productClass == "Vivarium.TeamCity.Conformance.RawTeamCityTests", "Wrong product boundary class.");
                var actual = raw.SingleOrDefault(r => r.Name == name && r.Class == productClass);
                Require(actual is not null, "Missing/wrong product test binding: " + id);
                var knownFailure = rawEvidence["failures"]!.AsArray()
                    .Any(f => S(f!, "name") == name && S(f!, "message") == actual!.Message && ResponseMismatch(f!, actual.Message));
                state = !valid ? "STALE" : !controlsHealthy ? "HARNESS_FAILURE" : actual!.Outcome == "Passed" ? "GREEN" : actual.Outcome == "Failed" && knownFailure ? "RED" : "HARNESS_FAILURE";
                if (state == "HARNESS_FAILURE") report.Errors.Add("HARNESS_FAILURE: unknown/setup/cleanup or required control failure for " + id);
            }
            report.Cases.Add(new(id, row["primary_owner"], state, replayState));
        }
        Require(rawNames.SetEquals(raw.Select(r => r.Name)), "Extra/orphan raw results or blocked case masquerading as product execution.");
        report.DiscoveryRows = ledger.Count;
        report.SubsystemSlices = slices.Count;
        report.IntegrationContributionRows = contributions.Count;
        report.DiscoveryRowsWithAnyBoundReferenceOrReplayCase = cases.SelectMany(c => Split(c["api_ids"])).Distinct().Count();
        report.DiscoveryRowsWithProductReadyRawCases = cases.Where(c => c["product_binding"] != "product-prerequisite-blocked").SelectMany(c => Split(c["api_ids"])).Distinct().Count();
        foreach (var owner in owners)
            report.Subsystems.Add(RollupFor(owner["subsystem_id"], ledger.Count(r => r["subsystem_id"] == owner["subsystem_id"]), report.Cases.Where(c => c.PrimaryOwner == owner["subsystem_id"])));
        foreach (var block in blocks)
            report.Blocks.Add(RollupFor(block["block"], null, block["block"] == "P0" ? report.Cases : []));
        if (report.Errors.Count > 0) report.Status = "STALE_OR_INVALID_EVIDENCE";
    }

    private static Rollup RollupFor(string id, int? rows, IEnumerable<CaseResult> source)
    {
        var cases = source.ToArray();
        return new(id, rows, cases.Length, cases.Count(c => c.State is "GREEN" or "RED" or "HARNESS_FAILURE"), cases.Count(c => c.State == "GREEN"),
            cases.Count(c => c.State == "RED"), cases.Count(c => c.State == "PRODUCT_BLOCKED"), cases.Count(c => c.State == "STALE"));
    }
    private sealed record Result(string Name, string Class, string Outcome, string Message);
    private static bool ResponseMismatch(JsonNode failure, string message)
    {
        if (failure["classification"]?.GetValue<string>() != "http-response-mismatch" ||
            failure["stage"]?.GetValue<string>() != "response-assertion" ||
            new[] { "SetUp", "TearDown", "setup", "cleanup" }.Any(word => message.Contains(word, StringComparison.Ordinal))) return false;
        var expected = failure["expected_status"]!.GetValue<int>();
        var actual = failure["actual_status"]!.GetValue<int>();
        if (expected is < 100 or > 599 || actual is < 100 or > 599 || expected == actual) return false;
        return message.Contains($"HTTP status: expected {expected}, actual {actual}", StringComparison.Ordinal) ||
            (message.Contains("installed addProject contract: explicit project creation", StringComparison.Ordinal) &&
             message.Contains("Expected: " + (System.Net.HttpStatusCode)expected, StringComparison.Ordinal) &&
             message.Contains("But was:  " + (System.Net.HttpStatusCode)actual, StringComparison.Ordinal));
    }
    private static Result[] Results(string root, JsonNode evidence, Report report)
    {
        var path = S(evidence, "artifact");
        if (Hash(root, path) != S(evidence, "sha256")) report.Errors.Add("STALE: result artifact hash mismatch: " + path);
        var document = XDocument.Load(SafePath(root, path));
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var definitions = document.Descendants(ns + "UnitTest").ToDictionary(x => (string)x.Attribute("id")!);
        var results = document.Descendants(ns + "UnitTestResult").Select(x => new Result((string)x.Attribute("testName")!,
            (string)definitions[(string)x.Attribute("testId")!].Element(ns + "TestMethod")!.Attribute("className")!,
            (string)x.Attribute("outcome")!, (string?)x.Element(ns + "Output")?.Element(ns + "ErrorInfo")?.Element(ns + "Message") ?? "")).ToArray();
        Require(results.Length > 0 && results.Select(r => r.Name).Distinct().Count() == results.Length, "Empty/duplicate result selection.");
        var counters = document.Descendants(ns + "Counters").Single();
        Require((int)counters.Attribute("total")! == results.Length && (int)counters.Attribute("passed")! == results.Count(r => r.Outcome == "Passed") &&
            (int)counters.Attribute("failed")! == results.Count(r => r.Outcome == "Failed"), "Result counters disagree with actual rows.");
        Require((int)evidence["counters"]!["total"]! == results.Length, "Receipt count differs from raw rows.");
        Require((int)evidence["exit_code"]! == (results.Any(r => r.Outcome == "Failed") ? 1 : 0), "Receipt exit/result mismatch.");
        return results;
    }
    private static string TestName(string binding, string id)
    {
        var name = binding[(binding.IndexOf('.') + 1)..];
        return name.StartsWith("TCREST-", StringComparison.Ordinal) || name == "Source_established_project_create_readback_replays_finite_sequence_and_resets_state" ? name : name + "(" + id + ")";
    }
    private static string BindingClass(string binding)
    { Require(binding.IndexOf('.') > 0 && binding.IndexOf('.') == binding.LastIndexOf('.'), "Invalid class/method binding."); return binding[..binding.IndexOf('.')]; }
    private static List<Dictionary<string, string>> Table(string root, string name, ISet<string> inputs, params string[] keys)
    {
        inputs.Add(Workstream + name);
        var lines = File.ReadAllLines(SafePath(root, Workstream + name));
        Require(lines.Length > 0, "Empty table: " + name);
        var header = lines[0].Split('\t');
        Require(header.Distinct().Count() == header.Length, "Duplicate column: " + name);
        var rows = lines.Skip(1).Where(l => l.Length > 0).Select(line =>
        {
            var values = line.Split('\t'); Require(values.Length == header.Length, "Invalid TSV row: " + name);
            return header.Zip(values).ToDictionary(p => p.First, p => p.Second);
        }).ToList();
        Unique(rows, keys); return rows;
    }
    private static void Unique(IEnumerable<Dictionary<string, string>> rows, params string[] keys)
    {
        var ids = rows.Select(r => string.Join('\t', keys.Select(k => r[k]))).ToArray();
        Require(ids.All(id => id.Length > 0) && ids.Distinct().Count() == ids.Length, "Duplicate/missing ID: " + string.Join('/', keys));
    }
    private static Dictionary<string, string> Find(Dictionary<string, Dictionary<string, string>> items, string id, string kind)
    { Require(items.ContainsKey(id), "Orphan " + kind + ": " + id); return items[id]; }
    private static void Tasks(string value) => Require(Split(value).Length > 0 && Split(value).All(v => int.TryParse(v, out var n) && n > 0), "Missing/invalid task link.");
    private static string[] Split(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries);
    private static string S(JsonNode node, string key) => node[key]?.GetValue<string>() ?? throw new InvalidDataException("Missing string: " + key);
    private static JsonNode Json(string root, string path) => JsonNode.Parse(File.ReadAllText(SafePath(root, path))) ?? throw new InvalidDataException("Empty JSON: " + path);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string? Hash(string root, string path) => File.Exists(SafePath(root, path)) ? Sha(File.ReadAllBytes(SafePath(root, path))) : null;
    private static string SafePath(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        Require(full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Evidence path escapes repository.");
        return full;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    private static Identity InspectGit(string root)
    {
        string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--no-optional-locks"); foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Cannot inspect repository identity.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                throw new IOException("Git identity read timed out.");
            }
            if (process.ExitCode != 0) throw new IOException("Git identity read failed: " + error.GetAwaiter().GetResult());
            return output.GetAwaiter().GetResult().Trim();
        }
        string[] producerRoots = ["src", "tests", "tools", "build", "Directory.Build.props", "global.json", "Vivarium.slnx"];
        var inputPaths = Git(["ls-files", "--", .. producerRoots]) + "\n" + Git(["ls-files", "--others", "--exclude-standard", "--", .. producerRoots]);
        var required = inputPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(path => path.TrimEnd('\r'))
            .Where(path => !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        return new(Git("rev-parse", "HEAD"), Git("rev-parse", "HEAD:src"),
            Git("diff", "HEAD", "--name-only", "--", "src").Length > 0 || Git("ls-files", "--others", "--exclude-standard", "--", "src").Length > 0,
            required);
    }
}
