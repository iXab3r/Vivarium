using System.Text.Json.Nodes;

namespace Vivarium.ApiLedger;

public sealed class R0Report
{
    public required string Candidate { get; init; }
    public string Scope => "SELECTED_R0_CONTRACT; not global TeamCity compatibility";
    public string Status { get; set; } = "SELECTED_R0_UNVERIFIED";
    public string? ObservedHead { get; set; }
    public string? ExecutionBase { get; set; }
    public StatusInput? ExecutionReceipt { get; set; }
    public List<StatusInput> ExecutionProducers { get; } = [];
    public Dictionary<string, string> CaseKinds { get; } = [];
    public string EvidenceMode => "REUSED; this offline command executes no tests";
    public bool GlobalApiComplete => false;
    public int? GlobalVariantDenominator => null;
    public string MilestoneAcceptance => "Not assessed by this command; separate review/debt/main/tracker evidence is required";
    public string ReviewState => "Independent aggregate/Security, findings, main delivery and tracker closure are separate gates";
    public int ContractRequired { get; set; }
    public int Required { get; set; }
    public int FullReferenceCases { get; set; }
    public int BehavioralAndNativeObligations { get; set; }
    public int Green => Cases.Count(c => c.State == "GREEN");
    public int Failed => Cases.Count(c => c.State == "FAILED");
    public int Unbound => Cases.Count(c => c.State == "UNBOUND");
    public bool AllSelectedCasesGreen => Required > 0 && Green == Required && Errors.Count == 0;
    public List<string> RetainedFullWireCases { get; } = [];
    public List<CaseResult> Cases { get; } = [];
    public List<Rollup> Subsystems { get; } = [];
    public List<string> RequiredInputPaths { get; } = [];
    public List<string> Errors { get; } = [];
    public string Limit => "Validates scope, unique bindings and byte provenance; assertion/oracle correctness requires independent review. Behavioral passes never discharge retained full-wire cases.";
}

public static partial class Ledger
{
    public static R0Report ReadR0(string root, string candidate, Func<string, Identity>? inspect = null,
        string assessmentPath = Workstream + "r0-api-evidence.json")
    {
        var report = new R0Report { Candidate = candidate };
        try { ReadR0Into(Path.GetFullPath(root), candidate, inspect ?? InspectGit, report, assessmentPath); }
        catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException
            or System.Xml.XmlException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException)
        {
            report.Errors.Add(error.Message); report.Status = "INVALID_R0_ACCOUNTING"; report.Cases.Clear(); report.Subsystems.Clear();
        }
        return report;
    }

    private static void ReadR0Into(string root, string candidate, Func<string, Identity> inspect, R0Report report, string assessmentPath)
    {
        var inputs = new HashSet<string>();
        var owners = Table(root, "subsystems.tsv", inputs, "subsystem_id").ToDictionary(r => r["subsystem_id"]);
        var slices = Table(root, "subsystem-slices.tsv", inputs, "slice_id").ToDictionary(r => r["slice_id"]);
        var contributions = Table(root, "subsystem-contributions.tsv", inputs, "contribution_id");
        var reference = Table(root, "rest-first-cohort-cases.tsv", inputs, "case_id")
            .Concat(Table(root, "rest-local-mutation-cases.tsv", inputs, "case_id")).ToDictionary(r => r["case_id"]);
        var diagnostics = new Report { Candidate = candidate };
        var variants = ValidateR0Contract(root, reference, owners, slices, contributions, inputs, diagnostics);
        var scope = Json(root, Workstream + "r0-contract-scope.json");
        var requiredReference = scope["required_reference_cases"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var required = requiredReference.Concat(variants.Keys).ToHashSet();
        report.ContractRequired = required.Count; report.Required = required.Count; report.FullReferenceCases = requiredReference.Count;
        report.BehavioralAndNativeObligations = variants.Count;
        foreach (var amendment in scope["owner_scope_amendments"]?.AsArray() ?? [])
            foreach (var item in amendment!["retained_full_cases"]!.AsArray()) report.RetainedFullWireCases.Add(item!.GetValue<string>());
        var bindings = Table(root, "r0-product-bindings.tsv", inputs, "case_id");
        Require(bindings.All(b => required.Contains(b["case_id"])), "Orphan R0 product binding or retained full wire promoted into behavioral acceptance.");
        Require(bindings.Select(b => (b["result_set"], b["class_name"], b["test_name"])).Distinct().Count() == bindings.Count,
            "One result row cannot discharge multiple R0 obligations.");
        var identity = inspect(root); report.ObservedHead = identity.Head;
        var roster = (identity.RequiredInputPaths ?? []).Concat(inputs).Distinct().Order(StringComparer.Ordinal).ToArray();
        report.RequiredInputPaths.AddRange(roster);
        var assessment = Json(root, assessmentPath);
        Require(S(assessment, "candidate_id") == candidate && S(assessment, "expected_head") == identity.Head,
            "R0 candidate/HEAD differs from explicit evidence assessment.");
        // Explicit bounded renewal uses the canonical roster and bindings; defaults retain all 28.
        if (assessment["selected_case_ids"] is JsonArray selection)
        {
            var ids = selection.Select(n => n!.GetValue<string>()).ToArray();
            Require(ids.Length > 0 && ids.Distinct().Count() == ids.Length && ids.All(required.Contains),
                "Invalid selected R0 case roster.");
            required = ids.ToHashSet();
            report.Required = required.Count;
            report.FullReferenceCases = required.Count(requiredReference.Contains);
            report.BehavioralAndNativeObligations = required.Count - report.FullReferenceCases;
        }
        var receiptPath = S(assessment, "receipt");
        Require(Hash(root, receiptPath) == S(assessment, "receipt_sha256"), "R0 execution receipt hash mismatch.");
        report.ExecutionReceipt = new(receiptPath, Hash(root, receiptPath));
        var receipt = Json(root, receiptPath); var executed = receipt["candidate"]!;
        report.ExecutionBase = S(executed, "git_base");
        var source = executed["source_inputs"]!.AsArray().Select(n => n!).ToArray();
        Require(source.Select(n => S(n, "path")).Distinct().Count() == source.Length, "Duplicate R0 execution source input.");
        var sourceByPath = source.ToDictionary(n => S(n, "path"), n => S(n, "sha256"));
        Require(roster.All(sourceByPath.ContainsKey), "R0 receipt omits a required producer/harness/metadata input.");
        foreach (var input in sourceByPath.Where(p => Hash(root, p.Key) != p.Value))
        {
            var gaps = new List<string>();
            QualifyPilotInput(root, assessment, input.Key, input.Value, "source", gaps);
            Require(gaps.Count == 0, "STALE: R0 source/fixture/oracle input bytes changed. " + string.Join("; ", gaps));
        }
        var fingerprint = Sha(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', sourceByPath.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value))));
        Require(fingerprint == S(executed, "source_fingerprint_sha256"), "R0 source fingerprint differs.");
        var transfer = assessment["decision"]!;
        Require(S(transfer, "from_base") == report.ExecutionBase && S(transfer, "from_source_fingerprint") == fingerprint
            && S(transfer, "to_candidate") == candidate && S(transfer, "action") == "reuse"
            && !string.IsNullOrWhiteSpace(S(transfer, "assessor")) && !string.IsNullOrWhiteSpace(S(transfer, "rationale")),
            "Missing R0 execution-to-candidate transfer assessment.");
        foreach (var impact in new[] { "production", "shared_services", "fixture_oracle", "reference_client", "environment" })
            Require(S(transfer["impacts"]!, impact) == "unchanged", "Changed/unknown R0 evidence impact requires linked fresh execution: " + impact);
        var assemblies = executed["assemblies"]!.AsArray().Select(n => n!).ToArray();
        Require(assemblies.Length > 0 && assemblies.Select(a => S(a, "path")).Distinct().Count() == assemblies.Length,
            "Missing/duplicate retained R0 execution assemblies.");
        var retained = assessment["retained_assemblies"]?.AsArray().Select(n => n!).ToArray() ?? [];
        Require(retained.Select(a => S(a, "recorded_path")).Distinct().Count() == retained.Length,
            "Duplicate retained R0 execution assembly mapping.");
        foreach (var item in retained)
            Require(assemblies.Any(a => S(a, "path") == S(item, "recorded_path") && S(a, "sha256") == S(item, "sha256")),
                "Wrong original R0 assembly binding.");
        foreach (var assembly in assemblies)
        {
            var mapping = retained.SingleOrDefault(a => S(a, "recorded_path") == S(assembly, "path"));
            var artifact = mapping is null ? S(assembly, "path") : S(mapping, "artifact");
            Require(Hash(root, artifact) == S(assembly, "sha256"), "Retained R0 execution assembly bytes differ.");
            report.ExecutionProducers.Add(new StatusInput(artifact, S(assembly, "sha256")));
        }
        var assemblyByRole = assemblies.ToDictionary(a => S(a, "role"));
        foreach (var role in new[] { "Vivarium.Controller", "Vivarium.Agent", "Vivarium.Contracts", "Vivarium.DefinitionProof" })
            Require(assemblyByRole.TryGetValue(role, out var assembly) && Path.GetFileName(S(assembly, "path")) == role + ".dll",
                "Missing retained R0 Host/Agent/contracts/harness execution binary: " + role);
        var results = new Dictionary<string, (string Target, Result[] Rows)>();
        foreach (var item in receipt["results"]!.AsObject())
        {
            var result = item.Value!;
            Require(!string.IsNullOrWhiteSpace(S(result, "command")), "Missing R0 execution command/selection.");
            Require(assemblyByRole.ContainsKey(S(result, "assembly_role")), "Result is not bound to retained execution assembly bytes.");
            if (S(result, "target") == "fresh-host-agent") Require(S(result, "assembly_role") == "Vivarium.DefinitionProof", "Wrong real Host/Agent execution harness.");
            if (S(result, "target") == "stock-oidc-browser") Require(S(result, "assembly_role") == "Vivarium.Acceptance", "Wrong stock browser execution harness.");
            results.Add(item.Key, (S(result, "target"), Results(root, result, diagnostics)));
        }
        Require(diagnostics.Errors.Count == 0, string.Join("; ", diagnostics.Errors));
        foreach (var id in required.Order(StringComparer.Ordinal))
        {
            var binding = bindings.SingleOrDefault(b => b["case_id"] == id);
            var owner = binding is null ? requiredReference.Contains(id) ? reference[id]["primary_owner"]
                : Split(variants[id]["contribution_subsystems"])[0] : binding["primary_subsystem"];
            Find(owners, owner, "R0 primary subsystem");
            if (binding is null) { report.Cases.Add(new(id, owner, "UNBOUND", "NOT_PRODUCT_EVIDENCE")); continue; }
            Require(requiredReference.Contains(id) ? owner == reference[id]["primary_owner"]
                : Split(variants[id]["contribution_subsystems"]).Contains(owner), "R0 primary owner is outside its owning contribution.");
            var comparison = requiredReference.Contains(id) ? "full-wire" : variants[id]["kind"] == "reference-behavior" ? "behavioral" : "native";
            report.CaseKinds.Add(id, comparison);
            Require(binding["comparison"] == comparison, "R0 behavior/native evidence cannot masquerade as full-wire conformance.");
            Require(results.TryGetValue(binding["result_set"], out var selected), "Missing R0 result set.");
            Require(binding["target"] == selected.Target && selected.Target is "fresh-host-agent" or "stock-oidc-browser", "Replay/component target cannot establish R0 product GREEN.");
            Require(selected.Target == "fresh-host-agent" ? binding["class_name"] is "Vivarium.DefinitionProof.R0AcceptanceCases" or "Vivarium.DefinitionProof.RuntimeHostPhysicalTests"
                : binding["class_name"].StartsWith("Vivarium.Acceptance.", StringComparison.Ordinal), "Wrong product execution boundary/class.");
            var matching = selected.Rows.Where(r => r.Class == binding["class_name"] && r.Name == binding["test_name"]).ToArray();
            Require(matching.Length == 1, "Missing/duplicate exact R0 class/test-name result binding: " + id);
            report.Cases.Add(new(id, owner, matching[0].Outcome == "Passed" ? "GREEN" : "FAILED", "NOT_PRODUCT_EVIDENCE"));
        }
        foreach (var set in results.Values)
            Require(set.Rows.All(r => r.Outcome != "Failed"), "A required cumulative R0/supporting check failed.");
        foreach (var group in report.Cases.GroupBy(c => c.PrimaryOwner))
            report.Subsystems.Add(new(group.Key, null, group.Count(), group.Count(c => c.State != "UNBOUND"), group.Count(c => c.State == "GREEN"),
                group.Count(c => c.State == "FAILED"), group.Count(c => c.State == "UNBOUND"), 0, ScopeState: "selected-r0; full owner remains separately allocated"));
        report.Status = report.AllSelectedCasesGreen ? "SELECTED_R0_CASES_GREEN_NOT_MILESTONE_ACCEPTANCE" : "SELECTED_R0_PARTIAL";
    }
}
