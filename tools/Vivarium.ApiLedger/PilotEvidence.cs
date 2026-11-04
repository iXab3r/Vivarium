using System.Text.Json.Nodes;

namespace Vivarium.ApiLedger;

public sealed class PilotReport
{
    public required string Candidate { get; init; }
    public string Status => "NOT_ACCEPTED";
    public string Boundary => "Selected Chiral/PostgreSQL PILOT business evidence; not literal REST conformance";
    public string LegacyBaseline => "Historical SQLite ControllerHost: five stale, twenty prerequisite blocked; not renewed by PILOT";
    public bool GlobalApiComplete => false;
    public int? GlobalVariantDenominator => null;
    public string ReviewDebtMain => "Fresh independent aggregate/Security review, zero debt and main delivery remain due";
    public int DeclaredWireVariants { get; set; }
    public List<PilotCase> Cases { get; } = [];
    public List<string> Gaps { get; } = [];
    public List<string> InputDrift { get; } = [];
    public List<string> ProducerDrift { get; } = [];
    public int RawPassed => Cases.Count(c => c.Outcome == "Passed");
    public int CurrentGreen => Gaps.Count == 0 ? RawPassed : 0;
    public string HeavyComposition { get; set; } = "UNQUALIFIED";
    public List<string> HeavyGaps { get; } = [];
    public string ReferenceGates => "#117/#118: bounded viewer denial, required filename absence, conflict and advanced HEAD have reference profiles; physical deleted bytes and broader profiles remain unaccepted; seven declarations are not full literal wire GREEN";
    public List<PilotCase> HeavyRawResults { get; } = [];
}
public sealed record PilotCase(string Id, string Binding, string Outcome);

public static partial class Ledger
{
    // The existing scope is the only business-case universe. Authored state/counts are not outcomes.
    public static PilotReport ReadPilot(string root, string candidate, string assessmentPath,
        Func<string, Identity>? inspect = null)
    {
        root = Path.GetFullPath(root);
        var report = new PilotReport { Candidate = candidate };
        try
        {
            var scope = Json(root, Workstream + "pilot-contract-scope.json");
            var cases = scope["cases"]!.AsArray();
            Require(cases.Select(c => S(c!, "id")).Distinct().Count() == cases.Count, "Duplicate PILOT case ID.");
            report.DeclaredWireVariants = scope["declared_variant_ids"]!.AsArray().Count;
            var identity = (inspect ?? InspectGit)(root);
            var assessment = Json(root, assessmentPath);
            Require(S(assessment, "candidate_id") == candidate && identity.Head == candidate,
                "Wrong PILOT candidate/HEAD.");
            JsonNode Bound(string key)
            {
                var binding = assessment[key]!;
                Require(Hash(root, S(binding, "path")) == S(binding, "sha256"), "PILOT receipt hash mismatch: " + key);
                return Json(root, S(binding, "path"));
            }
            var receipt = Bound("receipt");
            var seal = Bound("seal");
            var sealedPaths = seal["inputs"]!.AsArray().Select(e => S(e!, "path")).ToHashSet();
            foreach (var required in identity.RequiredInputPaths ?? [])
                if (!sealedPaths.Contains(required)) QualifyPilotInput(root, assessment, required, null, "addition", report.Gaps);
            var bindings = Bound("bindings").AsArray();
            var discovery = assessment["discovery"]!;
            Require(Hash(root, S(discovery, "path")) == S(discovery, "sha256"), "Discovery hash mismatch.");
            var discovered = File.ReadAllLines(SafePath(root, S(discovery, "path"))).Select(l => l.Trim()).ToHashSet();
            var diagnostics = new Report { Candidate = candidate };
            var rows = Results(root, new JsonObject {
                ["artifact"] = S(assessment, "trx"), ["sha256"] = S(receipt, "trx_sha256"),
                ["counters"] = new JsonObject { ["total"] = bindings.Count + 1 }, ["exit_code"] = 0
            }, diagnostics);
            report.Gaps.AddRange(diagnostics.Errors);
            foreach (var c in cases)
            {
                var id = S(c!, "id"); var binding = S(c!, "product_binding");
                var matches = bindings.Where(b => S(b!, "case_id") == id && S(b!, "binding") == binding).ToArray();
                Require(matches.Length == 1, "Missing/duplicate normative PILOT binding: " + id);
                var b = matches[0]!;
                var className = binding[..binding.LastIndexOf('.')];
                Require(discovered.Contains(S(b, "display_name")), "Binding absent from actual discovery: " + id);
                var found = rows.Where(r => r.Class == className && r.Name == S(b, "display_name")).ToArray();
                Require(found.Length == 1, "Missing exact raw PILOT class/test identity: " + id);
                report.Cases.Add(new(id, binding, found[0].Outcome));
            }
            Require(bindings.Count == cases.Count, "Orphan PILOT bindings.");
            var sdk = S(receipt["sdk"]!, "binding");
            var sdkRows = rows.Where(r => r.Class + "." + r.Name == sdk).ToArray();
            Require(sdkRows.Length == 1, "Missing exact SDK raw result.");
            report.Cases.Add(new("required-sdk", sdk, sdkRows[0].Outcome));
            foreach (var kind in new[] { "inputs", "producers" })
            {
                var entries = seal[kind]!.AsArray();
                Require(entries.Count > 0 && entries.Select(e => S(e!, "path")).Distinct().Count() == entries.Count,
                    "Missing/duplicate PILOT seal entries.");
                foreach (var entry in entries)
                    if (Hash(root, S(entry!, "path")) != S(entry!, "sha256"))
                        (kind == "inputs" ? report.InputDrift : report.ProducerDrift).Add(S(entry!, "path"));
            }
            // A changed source/binary requires an input-specific impact decision, never a blanket summary.
            foreach (var path in report.InputDrift.Concat(report.ProducerDrift))
            {
                var old = seal[report.InputDrift.Contains(path) ? "inputs" : "producers"]!.AsArray().Single(e => S(e!, "path") == path)!;
                QualifyPilotInput(root, assessment, path, S(old, "sha256"),
                    report.InputDrift.Contains(path) ? "source" : "producer", report.Gaps);
            }

            var decision = assessment["decision"];
            if (decision is null || S(decision, "to_candidate") != candidate || S(decision, "from_candidate") != S(receipt, "candidate")
                || S(decision, "action") != "reuse" || string.IsNullOrWhiteSpace(S(decision, "assessor")))
                report.Gaps.Add("Missing explicit execution-to-candidate reuse assessment.");
            if (rows.Any(r => r.Outcome != "Passed")) report.Gaps.Add("Required raw cohort contains non-Passed results.");
            var heavyDiagnostics = new Report { Candidate = candidate };
            foreach (var heavy in assessment["heavy_results"]?.AsArray() ?? [])
            {
                var observed = Results(root, heavy!, heavyDiagnostics);
                foreach (var row in observed)
                {
                    var role = S(heavy!, "role");
                    var expected = role switch {
                        "ordinary-heavy-observer" => "Published_CLI_builds_packages_and_publishes_actual_T3_with_partial_draft_recovery_and_exact_bytes",
                        "read-only-continuation" => "Retained_successful_CLI_chain_has_exact_remote_bytes_and_ordinary_readbacks",
                        _ => "" };
                    if (row.Class != "Vivarium.DefinitionProof.ActualT3AcceptanceCases" || row.Name != expected)
                        report.HeavyGaps.Add("Wrong raw heavy test binding: " + role);
                    if (role == "ordinary-heavy-observer" && !row.Message.Contains("could not be translated", StringComparison.Ordinal))
                        report.HeavyGaps.Add("Heavy failure is not the retained observer translation failure.");
                    report.HeavyRawResults.Add(new(role, row.Class + "." + row.Name, row.Outcome));
                }
            }
            report.HeavyGaps.AddRange(heavyDiagnostics.Errors);
            try { ReadPilotHeavy(root, assessment, report); }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
            { report.HeavyGaps.Add(e.Message); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { report.Gaps.Add(e.Message); }
        return report;
    }
    public static void QualifyPilotInput(string root, JsonNode assessment, string path, string? previous, string kind, List<string> gaps)
    {
        var decisions = assessment["impact_bindings"]?.AsArray().Where(d => S(d!, "path") == path).ToArray() ?? [];
        if (decisions.Length != 1) { gaps.Add("Missing/duplicate impact assessment: " + path); return; }
        var d = decisions[0]!;
        if (S(d, "previous_sha256") != (previous ?? "ADDED") || S(d, "current_sha256") != Hash(root, path)
            || S(d, "kind") != kind || !(S(d, "classification") == "no_selected_behavior_impact"
                || kind == "producer" && S(d, "producer_cause") == "runtime" && S(d, "classification") == "targeted_runtime_qualification")
            || new[] { "rationale", "assessor", "affected_binding", "evidence_category", "observed_diff", "independent_review" }
                .Any(k => string.IsNullOrWhiteSpace(S(d, k)))
            || d["evidence"] is not JsonArray evidence || evidence.Count == 0)
        { gaps.Add("Invalid input-specific impact assessment: " + path); return; }
        foreach (var e in evidence)
            if (Hash(root, S(e!, "path")) != S(e!, "sha256")) gaps.Add("Impact evidence mismatch: " + path);
        // Rationale is an attributed human judgment, not a mechanical behavioral oracle.
        if (kind == "producer" && S(d, "producer_cause") is not ("metadata" or "test_only"))
        {
            if (d["runtime_qualification"] is null) gaps.Add("Unresolved runtime producer impact: " + path);
            else QualifyPilotRuntime(root, d, gaps);
        }
    }

    // This is an attributed branch-impact assessment, not a new coverage oracle. Preserve the
    // original cohort and bind targeted execution separately; never rewrite its raw outcomes.
    public static void QualifyPilotRuntime(string root, JsonNode decision, List<string> gaps)
    {
        var q = decision["runtime_qualification"]!;
        var binding = q["assessment"]!;
        Require(Hash(root, S(binding, "path")) == S(binding, "sha256"), "Runtime qualification hash mismatch.");
        var assessment = Json(root, S(binding, "path"));
        Require(S(assessment, "producer_sha256") == S(decision, "current_sha256"), "Runtime producer mismatch.");
        foreach (var input in assessment["inputs"]!.AsArray())
            Require(Hash(root, S(input!, "path")) == S(input!, "sha256"), "Runtime input mismatch.");
        Require(assessment["inputs"]!.AsArray().Count > 0, "Missing runtime inputs.");
        var scope = Json(root, Workstream + "pilot-contract-scope.json")["cases"]!.AsArray();
        var expected = scope.Select(c => S(c!, "id")).Append("required-sdk").ToHashSet();
        var cases = assessment["cases"]!.AsArray();
        Require(cases.Count == expected.Count && cases.Select(c => S(c!, "id")).ToHashSet().SetEquals(expected),
            "Missing/duplicate runtime case qualification.");
        var proofs = assessment["proofs"]!.AsArray();
        Require(proofs.Count > 0 && proofs.Select(p => S(p!, "id")).Distinct().Count() == proofs.Count, "Missing/duplicate runtime proofs.");
        foreach (var proof in proofs)
        {
            var diagnostics = new Report { Candidate = "targeted-runtime" };
            var rows = Results(root, proof!, diagnostics);
            gaps.AddRange(diagnostics.Errors);
            var selected = proof!["bindings"]!.AsArray().Select(b => b!.GetValue<string>()).ToArray();
            Require(selected.Length > 0 && selected.Distinct().Count() == selected.Length, "Missing/duplicate targeted bindings.");
            foreach (var test in selected)
                Require(rows.Count(r => r.Class + "." + r.Name == test && r.Outcome == "Passed") == 1, "Missing targeted runtime PASS: " + test);
        }
        foreach (var c in cases)
        {
            Require(S(c!, "action") is "renew" or "qualified_reuse" && !string.IsNullOrWhiteSpace(S(c!, "rationale")), "Invalid runtime case decision.");
            var normative = scope.SingleOrDefault(s => S(s!, "id") == S(c!, "id"));
            if (normative is not null)
                Require(S(c!, "binding") == S(normative, "product_binding"), "Runtime normative binding mismatch.");
            var references = c!["proof_ids"]!.AsArray();
            Require(references.Count > 0 && references.All(r => proofs.Any(p => S(p!, "id") == r!.GetValue<string>())), "Missing runtime case proof.");
            if (S(c!, "action") == "renew")
                Require(proofs.Where(p => references.Any(r => r!.GetValue<string>() == S(p!, "id")))
                    .Any(p => p!["bindings"]!.AsArray().Any(b => b!.GetValue<string>() == S(c!, "binding"))), "Renewed case absent from targeted proof.");
        }
    }

    public static void ReadPilotHeavy(string root, JsonNode assessment, PilotReport report)
    {
        JsonNode Bound(string key)
        {
            var b = assessment["heavy_composition"]![key]!;
            Require(Hash(root, S(b, "path")) == S(b, "sha256"), "Heavy hash mismatch: " + key);
            return Json(root, S(b, "path"));
        }
        var cli = Bound("cli"); var receipt = Bound("receipt"); var execution = Bound("execution");
        var definition = Bound("definition"); var manifest = Bound("manifest");
        var version = S(receipt, "version"); var revision = S(receipt, "revision");
        Require(S(cli, "outcome") == "succeeded" && cli["build"]!["id"]!.GetValue<int>() == 3
            && receipt["rootBuildId"]!.GetValue<int>() == 3 && S(cli["build"]!, "number") == version,
            "Heavy CLI/root identity mismatch.");
        Require(revision == S(definition, "packageSource") && revision == S(manifest, "revision")
            && version == S(manifest, "version"), "Heavy source mismatch.");
        foreach (var key in new[] { "definitionGit", "scriptSha256", "helperSha256", "publisherSha256" })
            Require(S(definition, key) == S(assessment["heavy_composition"]!["identity"]!, key), "Heavy definition mismatch: " + key);
        var builds = execution["builds"]!.AsArray();
        Require(builds.Count == 3 && builds.Select(b => b!["Id"]!.GetValue<int>()).SequenceEqual(new[] { 1, 2, 3 })
            && builds.All(b => S(b!, "State") == "FINISHED" && S(b!, "Status") == "SUCCESS" && S(b!, "Number") == version),
            "Heavy build graph mismatch.");
        var release = receipt["release"]!;
        Require(release["id"]!.GetValue<long>() == 434492192 && !release["draft"]!.GetValue<bool>()
            && S(release, "tag_name") == "v" + version, "Heavy release identity mismatch.");
        var assets = receipt["assets"]!.AsArray();
        Require(assets.Count == 10, "Heavy asset count mismatch.");
        Require(release["assets"]!.AsArray().Count == 10 && assets.Select(a => S(a!, "Path")).Distinct().Count() == 10,
            "Heavy remote asset inventory mismatch.");
        Require(execution["source"]!.AsArray().Count == 3 && execution["source"]!.AsArray().All(s => S(s!, "Version") == revision),
            "Heavy selected source mismatch.");
        var executions = execution["executions"]!.AsArray();
        Require(executions.Count == 3 && executions.All(e => S(e!, "AgentId") == S(receipt,
            e!["BuildId"]!.GetValue<int>() == 2 ? "windowsId" : "linuxId")), "Heavy assigned Agent mismatch.");
        foreach (var asset in release["assets"]!.AsArray())
        {
            var name = S(asset!, "name");
            Require(Path.GetFileName(name) == name && !name.Contains('\\') && !name.Contains('/'), "Heavy asset name is not a leaf.");
            var file = S(assessment["heavy_composition"]!, "directory") + "/published/" + name;
            var hash = Hash(root, file);
            Require(S(asset!, "digest") == "sha256:" + hash && new FileInfo(SafePath(root, file)).Length == asset!["size"]!.GetValue<long>(),
                "Heavy asset bytes mismatch: " + name);
            var retained = assets.Single(a => S(a!, "Path") == "release/" + name)!;
            Require(S(retained, "Sha256") == hash && retained["Length"]!.GetValue<long>() == asset!["size"]!.GetValue<long>(),
                "Heavy receipt bytes mismatch: " + name);
            if (manifest["files"]![name] is JsonNode digest) Require(digest.GetValue<string>() == hash, "Heavy manifest mismatch: " + name);
        }
        Require(report.HeavyRawResults.Any(r => r.Id == "ordinary-heavy-observer" && r.Outcome == "Failed")
            && report.HeavyRawResults.Any(r => r.Id == "read-only-continuation" && r.Outcome == "Passed"), "Heavy raw observer/continuation identities missing.");
        report.HeavyComposition = "CLI_SUCCESS_BYTES_VERIFIED; observer FAIL and read-only continuation PASS retained separately; actor/cookie/current-key observations require independent receipt/source review";
    }

}
