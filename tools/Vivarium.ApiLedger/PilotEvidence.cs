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
    public List<PilotCase> HeavyRawResults { get; } = [];
}
public sealed record PilotCase(string Id, string Binding, string Outcome);

public static partial class Ledger
{
    // The existing scope is the only business-case universe. Authored state/counts are not outcomes.
    public static PilotReport ReadPilot(string root, string candidate, string assessmentPath,
        Func<string, Identity>? inspect = null)
    {
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
                if (!sealedPaths.Contains(required)) report.Gaps.Add("Execution seal omits current required input: " + required);
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
                var decisions = assessment["impact_bindings"]?.AsArray().Where(d => S(d!, "path") == path).ToArray() ?? [];
                if (decisions.Length != 1 || S(decisions[0]!, "current_sha256") != Hash(root, path)
                    || S(decisions[0]!, "classification") != "no_selected_behavior_impact"
                    || string.IsNullOrWhiteSpace(S(decisions[0]!, "rationale")))
                    report.Gaps.Add("Unqualified source/producer impact: " + path);
            }
            var decision = assessment["decision"];
            if (decision is null || S(decision, "to_candidate") != candidate || S(decision, "from_candidate") != S(receipt, "candidate")
                || S(decision, "action") != "reuse" || string.IsNullOrWhiteSpace(S(decision, "assessor")))
                report.Gaps.Add("Missing explicit execution-to-candidate reuse assessment.");
            if (rows.Any(r => r.Outcome != "Passed")) report.Gaps.Add("Required raw cohort contains non-Passed results.");
            foreach (var heavy in assessment["heavy_results"]?.AsArray() ?? [])
            {
                var observed = Results(root, heavy!, diagnostics);
                foreach (var row in observed)
                    report.HeavyRawResults.Add(new(S(heavy!, "role"), row.Class + "." + row.Name, row.Outcome));
            }
            report.Gaps.AddRange(diagnostics.Errors.Except(report.Gaps));
            report.Gaps.Add("Heavy composition: raw results retained separately; exact asset/actor/readback receipt support and producer impact still require qualification.");
            report.Gaps.Add("Unknown selection denial/missing-byte/conflict/changed-HEAD reference variants remain unaccepted.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { report.Gaps.Add(e.Message); }
        return report;
    }
}
