namespace Vivarium.ApiLedger;

public static partial class Ledger
{
    private static void ValidatePilotDeclarations(string root,
        Dictionary<string, Dictionary<string, string>> apis,
        Dictionary<string, Dictionary<string, string>> cases,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices, ISet<string> inputs, Report report)
    {
        foreach (var api in apis.Values)
        {
            var ids = Split(api.GetValueOrDefault("declared_variant_ids", ""));
            Require(ids.Distinct().Count() == ids.Length, "Duplicate API declaration binding.");
        }
        var declared = apis.Values.SelectMany(a => Split(a.GetValueOrDefault("declared_variant_ids", ""))).ToHashSet();
        var path = Workstream + "pilot-contract-variants.tsv";
        if (!File.Exists(SafePath(root, path)))
        {
            Require(declared.Count == 0, "Missing PILOT declaration inventory.");
            return;
        }
        var rows = Table(root, "pilot-contract-variants.tsv", inputs, "variant_id");
        Require(declared.SetEquals(rows.Select(r => r["variant_id"])), "Missing/orphan PILOT API declaration.");
        var scopePath = Workstream + "pilot-contract-scope.json";
        inputs.Add(scopePath);
        var scope = Json(root, scopePath);
        var required = scope["declared_variant_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Require(required.Distinct().Count() == required.Length && declared.SetEquals(required), "Missing/duplicate PILOT scope declaration.");
        foreach (var row in rows)
        {
            Require(!cases.ContainsKey(row["variant_id"]) && row["state"] == "DECLARED_REFERENCE_GAPS_NOT_WIRE_GREEN",
                "PILOT declaration cannot masquerade as a materialized/GREEN wire case.");
            var slice = Find(slices, row["integration_slice"], "PILOT declaration slice");
            Find(owners, row["primary_owner"], "PILOT declaration owner");
            Require(slice["milestone"] == "PILOT" && slice["subsystem_id"] == row["primary_owner"] &&
                row["contract_issue"] == slice["rest_contract_issue"] && row["acceptance_issue"] == slice["rest_acceptance_issue"] &&
                row["contract_issue"] == scope["contract_issue"]!.ToString() && row["acceptance_issue"] == scope["acceptance_issue"]!.ToString(),
                "Wrong PILOT declaration owner/task/milestone.");
            var collaborators = Split(row["contribution_subsystems"]);
            Require(collaborators.Contains(row["primary_owner"]) && collaborators.Distinct().Count() == collaborators.Length,
                "Missing/duplicate PILOT declaration owner.");
            foreach (var owner in collaborators) Find(owners, owner, "PILOT related owner");
            var ids = Split(row["api_ids"]);
            Require(ids.Length > 0 && ids.Distinct().Count() == ids.Length, "Missing/duplicate PILOT declaration API.");
            foreach (var id in ids)
            {
                var api = Find(apis, id, "PILOT declaration API");
                Require(collaborators.Contains(api["subsystem_id"]) && Split(api.GetValueOrDefault("declared_variant_ids", "")).Contains(row["variant_id"]),
                    "Wrong reverse PILOT declaration API binding/owner.");
            }
            foreach (var api in apis.Values.Where(a => Split(a.GetValueOrDefault("declared_variant_ids", "")).Contains(row["variant_id"])))
                Require(ids.Contains(api["api_id"]), "Wrong forward PILOT declaration API binding.");
            foreach (var field in new[] { "source_path", "principal_and_current_authority", "seed_and_prior_state", "request_or_action",
                "expected_response", "expected_effect", "completion", "comparison_bindings", "product_binding" })
                Require(!string.IsNullOrWhiteSpace(row[field]), "Missing PILOT declaration field: " + field);
            Require(File.Exists(SafePath(root, row["source_path"])), "Missing PILOT declaration source.");
            inputs.Add(row["source_path"]);
            if (row.TryGetValue("selected_product_evidence", out var selected) && selected.Length > 0)
            {
                Require(File.Exists(SafePath(root, selected)), "Missing selected PILOT product evidence.");
                inputs.Add(selected);
            }
        }
        report.DeclaredPilotVariants = rows.Count;
    }
}
