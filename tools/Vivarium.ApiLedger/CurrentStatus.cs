using System.Text;
using System.Text.Json.Nodes;

namespace Vivarium.ApiLedger;

public sealed class CurrentStatus
{
    public string AccountingState { get; set; } = "VALID";
    public string EvidenceState => SelectedR0?.AllSelectedCasesGreen == true || SelectedPilot?.CurrentGreen > 0
        ? "QUALIFIED_SELECTED_EVIDENCE" : "UNQUALIFIED_CURRENT_HEAD";
    public R0Report? SelectedR0 { get; set; }
    public PilotReport? SelectedPilot { get; set; }
    public List<StatusInput> SelectedAssessments { get; } = [];
    public required string Head { get; init; }
    public bool ProductionDirty { get; init; }
    public bool GlobalApiComplete => false;
    public int? GlobalVariantDenominator => null;
    public double? CompatibilityPercent => null;
    public List<CurrentSubsystem> Subsystems { get; } = [];
    public List<StatusInput> Inputs { get; } = [];
    public List<StatusInput> CurrentHostSourceObservations { get; } = [];
    public Dictionary<string, string> ImplementedSurfaceObservations { get; } = new()
    {
        ["SS-CFG;SS-EXEC / US-013"] = "CatalogEndpoints: project/configuration reads and reconcile/change reads; C#/Git steps are selected execution evidence, not complete REST step editing. Owners #122/#137.",
        ["SS-RUN;SS-LOG;SS-BLOB / US-013"] = "RuntimeBuild*Endpoints: submit/read/cancel/history, output and artifacts; TeamCityBuild/ArtifactEndpoints provide selected /app/rest adapters. Exact progress/cancel wire gaps #166; full owners #134/#146/#152.",
        ["SS-AGT / US-013"] = "RuntimeAgentEndpoints and TeamCityAgentEndpoints: selected enrollment/authorization/Agent reads. Complete requirements, compatibility and pools remain unknown under #128/#129/#130.",
        ["SS-IAM / US-013"] = "AccessEndpoints and token endpoints: native user/role/grant/current-access operations. Complete compatible identity family remains unknown under #119/#120/#121.",
        ["OTHER"] = "All remaining owners retain canonical scope and tasks below; implementation/variant completeness unknown. These observations require the named source files; they confer no GREEN."
    };
    public string SourceObservationQualification => "Source bytes only, implementation and route ownership require review; historical native registry is not a current Host route census. US-013 selected delivery is retained in the hashed delivery record; current binding currency is reported separately in selected assessments.";
    public List<string> Errors { get; } = [];
    public required JsonNode HistoricalSnapshot { get; init; }
    public required JsonNode LegacyAssessment { get; init; }
    public List<string> Gaps { get; } = [
        "Current source observations are not executed requests or a complete route/variant census; unmatched routes remain UNKNOWN (#161/#162/#163).",
        "No current-head input-specific transfer is supplied. Preserve selected R0/PILOT delivery; do not relabel it fresh evidence.",
        "R0 selected 28 obligations include four full reference workflows and native/behavioral obligations; not 28 full wire cases (#114/#115).",
        "PILOT selected 34 business/SDK rows are not wire cases; seven declared variants remain unmaterialized (#117/#118).",
        "Exact progress/cancel wire behavior remains unaccepted (#166); broader source normalization/freeze remains #110/#111/#112.",
        "Private farm 0.1.30/source f56 smoke is not new wire parity. No deployed binary is inspected by this command."
    ];
}
public sealed record StatusInput(string Path, string? Sha256);
public sealed record CurrentSubsystem(string Id, string Name, string Owner, string Scope,
    int DiscoveryHeadings, string[] MaterializedCases, string[] DeclaredVariants,
    string[] R0Bindings, StatusInput[] SourceObservations, string[] HistoricalOutcomes,
    string[] Tasks, string EvidenceState = "UNKNOWN_CURRENT_COMPATIBILITY",
    string Next = "Qualify input-specific evidence transfer or renew the smallest affected reference-backed binding; freeze remaining variants with the owning contract task.");

public static partial class Ledger
{
    // Projection of existing registers, never an alternative compatibility ledger.
    public static CurrentStatus ReadCurrent(string root, Func<string, Identity>? inspect = null,
        string? r0Assessment = null, string? pilotAssessment = null)
    {
        root = Path.GetFullPath(root);
        var identity = (inspect ?? InspectGit)(root);
        var snapshot = Json(root, Workstream + "api-status.json");
        var assessment = Json(root, Workstream + "api-evidence.json");
        var report = new CurrentStatus { Head = identity.Head, ProductionDirty = identity.ProductionDirty,
            HistoricalSnapshot = new JsonObject {
                ["candidate"] = snapshot["candidate"]?.DeepClone(),
                ["observed_head"] = snapshot["observed_head"]?.DeepClone(),
                ["boundary"] = "HISTORICAL_SQLITE_NOT_CURRENT_HOST",
                ["cases"] = snapshot["cases"]?.DeepClone()
            }, LegacyAssessment = new JsonObject {
                ["candidate_id"] = assessment["candidate_id"]?.DeepClone(),
                ["expected_head"] = assessment["expected_head"]?.DeepClone(),
                ["receipt"] = assessment["receipt"]?.DeepClone(),
                ["receipt_sha256"] = assessment["receipt_sha256"]?.DeepClone(),
                ["classification"] = "Named assessment identity, not arbitrary Git SHA; current head still requires explicit transfer"
            } };
        var inputs = new HashSet<string>();
        // Bounded current adapter observation, not a route parser or a parity denominator.
        foreach (var directory in new[] { "Catalog", "TeamCity", "Agents", "Identity", "Tokens", "Builds/Runtime" })
        {
            var path = SafePath(root, "src/Vivarium.Controller/" + directory);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*Endpoints.cs", SearchOption.TopDirectoryOnly).Order())
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                report.CurrentHostSourceObservations.Add(new(relative, Hash(root, relative)));
            }
        }
        var owners = Table(root, "subsystems.tsv", inputs, "subsystem_id");
        var apis = Table(root, "api-ledger.tsv", inputs, "api_id");
        var cases = Table(root, "rest-first-cohort-cases.tsv", inputs, "case_id")
            .Concat(Table(root, "rest-local-mutation-cases.tsv", inputs, "case_id")).ToArray();
        Unique(cases, "case_id");
        var bindings = Table(root, "r0-product-bindings.tsv", inputs, "case_id");
        var declarations = Table(root, "pilot-contract-variants.tsv", inputs, "variant_id");
        var native = Table(root, "native-http-occurrences.tsv", inputs, "source_id");
        var dispositions = Table(root, "source-disposition.tsv", inputs, "path");
        var ownerIds = owners.Select(o => o["subsystem_id"]).ToHashSet();
        foreach (var row in apis) Require(ownerIds.Contains(row["subsystem_id"]), "Orphan current API owner.");
        foreach (var row in cases) Require(ownerIds.Contains(row["primary_owner"]), "Orphan current case owner.");
        foreach (var row in bindings) Require(ownerIds.Contains(row["primary_subsystem"]), "Orphan current binding owner.");
        var caseIds = cases.Select(c => c["case_id"]).ToHashSet();
        var historical = snapshot["cases"]!.AsArray();
        Require(historical.Count == caseIds.Count && historical.Select(c => S(c!, "case_id")).ToHashSet().SetEquals(caseIds),
            "Historical snapshot/case roster differs; preserve outcomes explicitly.");
        foreach (var row in historical)
        {
            Require(cases.Any(c => c["case_id"] == S(row!, "case_id") && c["primary_owner"] == S(row!, "primary_owner")), "Historical case owner differs.");
            Require(S(row!, "state") is "GREEN" or "RED" or "STALE" or "PRODUCT_BLOCKED" or "HARNESS_FAILURE", "Unknown historical outcome.");
        }
        foreach (var row in apis)
            foreach (var id in Split(row["variant_case_ids"]))
                Require(caseIds.Contains(id) && Split(cases.Single(c => c["case_id"] == id)["api_ids"]).Contains(row["api_id"]), "Invalid current API/case join.");
        foreach (var row in cases)
            foreach (var id in Split(row["api_ids"]))
                Require(apis.Any(a => a["api_id"] == id && Split(a["variant_case_ids"]).Contains(row["case_id"])), "Invalid reverse current case/API join.");
        foreach (var owner in owners)
        {
            var id = owner["subsystem_id"];
            var ownApis = apis.Where(a => a["subsystem_id"] == id).ToArray();
            var sources = native.Where(n => n["subsystem_id"] == id).Select(n => n["source"])
                .Concat(dispositions.Where(d => d["subsystem_id"] == id).Select(d => d["path"]))
                .Distinct().Order().Select(p => new StatusInput(p, Hash(root, p))).ToArray();
            report.Subsystems.Add(new(id, owner["name"], "https://github.com/iXab3r/Vivarium/issues/" + owner["primary_issue"],
                owner["rest_scope"], ownApis.Length,
                cases.Where(c => c["primary_owner"] == id).Select(c => c["case_id"]).ToArray(),
                ownApis.SelectMany(a => Split(a["declared_variant_ids"])).Distinct().ToArray(),
                bindings.Where(b => b["primary_subsystem"] == id).Select(b => b["case_id"]).ToArray(), sources,
                snapshot["cases"]!.AsArray().Where(c => S(c!, "primary_owner") == id)
                    .Select(c => S(c!, "case_id") + ":" + S(c!, "state")).ToArray(),
                ownApis.SelectMany(a => new[] { a["rest_contract_issue"], a["rest_acceptance_issue"] })
                    .Distinct().Select(t => "https://github.com/iXab3r/Vivarium/issues/" + t).ToArray()));
        }
        Require(apis.SelectMany(a => Split(a["declared_variant_ids"])).ToHashSet()
            .SetEquals(declarations.Select(d => d["variant_id"])), "Current declaration joins differ.");
        foreach (var path in inputs.Concat(new[] { Workstream + "api-status.json", Workstream + "api-evidence.json",
            Workstream + "pilot-final-delivery-20261105.md", Workstream + "r0-contract-scope.json", Workstream + "pilot-contract-scope.json" }).Order())
            report.Inputs.Add(new(path, Hash(root, path)));
        Require(report.Inputs.All(i => i.Sha256 != null), "Missing current status input.");
        if (r0Assessment is not null)
        {
            var selected = Json(root, r0Assessment);
            report.SelectedAssessments.Add(new(r0Assessment, Hash(root, r0Assessment)));
            report.SelectedR0 = ReadR0(root, S(selected, "candidate_id"), _ => identity, r0Assessment);
            report.Errors.AddRange(report.SelectedR0.Errors);
        }
        if (pilotAssessment is not null)
        {
            report.SelectedAssessments.Add(new(pilotAssessment, Hash(root, pilotAssessment)));
            report.SelectedPilot = ReadPilot(root, identity.Head, pilotAssessment, _ => identity);
            report.Errors.AddRange(report.SelectedPilot.Gaps);
        }
        if (report.SelectedAssessments.Count > 0)
            report.Gaps.RemoveAll(g => g.StartsWith("No current-head input-specific"));
        return report;
    }

    public static string RenderCurrent(CurrentStatus report)
    {
        var text = new StringBuilder($"REST status at {report.Head}\nAccounting: {report.AccountingState}; evidence: {report.EvidenceState}; global denominator/percentage: UNKNOWN; API complete: false\n");
        text.AppendLine("Historical outcomes below retain original RED/GREEN/blocked states; none are promoted to current GREEN.");
        text.AppendLine(report.SourceObservationQualification);
        foreach (var surface in report.ImplementedSurfaceObservations) text.AppendLine(surface.Key + ": " + surface.Value);
        foreach (var source in report.CurrentHostSourceObservations) text.AppendLine($"Current adapter source: {source.Path} sha256:{source.Sha256}");
        foreach (var s in report.Subsystems)
        {
            text.AppendLine($"{s.Id} {s.Name} ({s.Owner})\n  {s.Scope}\n  Discovery headings {s.DiscoveryHeadings}; materialized cases {s.MaterializedCases.Length}; declared variants {s.DeclaredVariants.Length}; selected R0 bindings {s.R0Bindings.Length}; source observations {s.SourceObservations.Length}\n  {s.EvidenceState}; {s.Next}");
            if (s.HistoricalOutcomes.Length > 0) text.AppendLine("  Historical: " + string.Join(", ", s.HistoricalOutcomes));
            text.AppendLine("  Work: " + string.Join(", ", s.Tasks));
        }
        if (report.SelectedR0 is { } r0)
        {
            text.AppendLine($"Selected R0: {r0.Green}/{r0.Required} current GREEN; {r0.FullReferenceCases} full wire, {r0.BehavioralAndNativeObligations} behavioral/native; full contract {r0.ContractRequired}; remaining obligations are not renewed.");
            foreach (var c in r0.Cases) text.AppendLine($"  {c.CaseId} {c.PrimaryOwner} {c.State} {r0.CaseKinds.GetValueOrDefault(c.CaseId, "unbound")} R0 canonical binding");
        }
        if (report.SelectedPilot is { } pilot) text.AppendLine($"Selected PILOT business/SDK: {pilot.CurrentGreen} current GREEN, {pilot.RawPassed} raw Passed; not literal wire conformance; heavy currency separate: {pilot.HeavyComposition}");
        foreach (var error in report.Errors) text.AppendLine("UNQUALIFIED: " + error);
        foreach (var gap in report.Gaps) text.AppendLine("UNKNOWN/UNQUALIFIED: " + gap);
        return text.ToString();
    }
}
