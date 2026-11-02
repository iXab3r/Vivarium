using System.Text.Json.Nodes;

namespace Vivarium.ApiLedger;

public static partial class Ledger
{
    // Selected obligations are not executed cases. Never mix these into TCREST or replay GREEN.
    private static Dictionary<string, Dictionary<string, string>> ValidateR0Contract(string root,
        Dictionary<string, Dictionary<string, string>> cases,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices,
        List<Dictionary<string, string>> contributions, ISet<string> inputs, Report report)
    {
        var path = Workstream + "r0-contract-scope.json";
        inputs.Add(path);
        var scope = Json(root, path);
        Require(S(scope, "kind") == "SELECTED_R0_CONTRACT_PRODUCT_UNBOUND" &&
            scope["global_variant_denominator"] is null && S(scope, "product_acceptance") == "NOT_ACCEPTED",
            "Selected R0 contract cannot claim global scope or product acceptance.");
        var slice = Find(slices, S(scope, "integration_slice"), "selected R0 integration");
        Require(scope["contract_task"]!.ToString() == slice["rest_contract_issue"] &&
            scope["acceptance_task"]!.ToString() == slice["rest_acceptance_issue"], "Wrong selected R0 task block.");
        var rows = Table(root, "r0-contract-variants.tsv", inputs, "variant_id");
        var byVariant = rows.ToDictionary(row => row["variant_id"]);
        static string[] Values(JsonNode node) => node.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        var requiredVariants = Values(scope["required_specified_variants"]!);
        var requiredReference = Values(scope["required_reference_cases"]!);
        var requiredContributions = Values(scope["contribution_ids"]!);
        Require(requiredVariants.Length > 0 && requiredVariants.Distinct().Count() == requiredVariants.Length &&
            requiredVariants.ToHashSet().SetEquals(byVariant.Keys), "Missing/orphan selected R0 variant.");
        Require(requiredReference.Length > 0 && requiredReference.Distinct().Count() == requiredReference.Length,
            "Missing/duplicate selected R0 reference cases.");
        foreach (var id in requiredReference) Find(cases, id, "selected R0 reference case");
        var own = contributions.Where(row => row["integration_slice"] == slice["slice_id"]).ToArray();
        Require(requiredContributions.Length > 0 && requiredContributions.Distinct().Count() == requiredContributions.Length &&
            requiredContributions.ToHashSet().SetEquals(own.Select(row => row["contribution_id"])),
            "Missing/orphan selected R0 contribution.");
        var excluded = scope["excluded_full_case"]!;
        var fullCase = Find(cases, S(excluded, "id"), "retained complete reference case");
        Find(slices, S(excluded, "due_owner"), "complete reference case due owner");
        Require(!requiredReference.Contains(fullCase["case_id"]), "Native preparation cannot discharge the complete PAT wire case.");
        var allRequired = requiredVariants.Concat(requiredReference).ToHashSet();
        var ownSliceVariants = rows.Where(row => Split(row["contribution_subsystems"]).Contains(slice["subsystem_id"]))
            .Select(row => row["variant_id"]);
        Require(own.SelectMany(row => Split(row["variant_case_ids"])).Concat(ownSliceVariants).ToHashSet().SetEquals(allRequired),
            "Selected R0 contribution/case roster differs from its contract scope.");
        foreach (var row in rows)
        {
            Require(!cases.ContainsKey(row["variant_id"]) && row["state"] == "SPECIFIED_PRODUCT_UNBOUND" &&
                row["product_binding"] == "UNBOUND", "Specified R0 obligation cannot masquerade as a materialized/GREEN case.");
            Require(row["contract_issue"] == slice["rest_contract_issue"] && row["acceptance_issue"] == slice["rest_acceptance_issue"],
                "Wrong selected R0 variant task block.");
            foreach (var field in new[] { "principal_and_current_authority", "seed_and_prior_state", "request_or_action",
                "expected_response", "expected_effect", "completion", "comparison_bindings" })
                Require(!string.IsNullOrWhiteSpace(row[field]), "Missing selected R0 actor/request/effect/completion binding: " + field);
            var source = row["source_path"];
            Require(File.Exists(SafePath(root, source)), "Missing selected R0 independent contract source.");
            inputs.Add(source);
            var collaborators = Split(row["contribution_subsystems"]);
            Require(collaborators.Length > 0 && collaborators.Distinct().Count() == collaborators.Length,
                "Missing/duplicate selected R0 contribution owners.");
            foreach (var owner in collaborators)
            {
                Find(owners, owner, "selected R0 variant owner");
                if (owner == slice["subsystem_id"] && !own.Any(c => c["subsystem_id"] == owner)) continue;
                Require(own.Any(c => c["subsystem_id"] == owner && Split(c["variant_case_ids"]).Contains(row["variant_id"])),
                    "Missing reverse R0 variant/contribution binding.");
            }
            Require(row["kind"] is "native-contract" or "integration-contract" or "reference-effect", "Unknown selected R0 oracle boundary.");
            if (row["kind"] != "reference-effect")
            {
                Require(row["source_case_id"] == "" && row["source_exchange_indices"] == "", "Unexpected reference subset on a native contract.");
                continue;
            }
            Require(row["variant_id"] == "R0-CREDENTIAL-REVOKED-READS-001" && row["source_case_id"] == fullCase["case_id"] &&
                row["source_exchange_indices"] == "2;3;4", "Revocation effect must retain its explicit subset/full-case distinction.");
            var corpus = Json(root, source);
            var observation = corpus["cases"]!.AsArray().Single(item => S(item!, "id") == row["source_case_id"]);
            var exchanges = observation!["exchanges"]!.AsArray();
            Require(exchanges.Count == 5 && exchanges[0]!["request"]!["method"]!.GetValue<string>() == "DELETE" &&
                exchanges[1]!["request"]!["method"]!.GetValue<string>() == "DELETE", "Complete revocation wire source changed.");
            var selection = new[] { 2, 3, 4 }.Select(index => exchanges[index]!).ToArray();
            Require(selection.All(item => S(item["request"]!, "method") == "GET") &&
                selection.Select(item => item["response"]!["status"]!.GetValue<int>()).SequenceEqual([401, 401, 200]) &&
                selection[0]["actor"]!.GetValue<string>() == "owned-scoped-pat" &&
                selection[1]["actor"]!.GetValue<string>() == "owned-scoped-pat" && selection[2]["actor"]!.GetValue<string>() == "admin",
                "Selected revocation reads lost their unchanged-actor/positive-control boundary.");
        }
        report.SpecifiedR0Variants = rows.Count;
        report.SelectedR0ReferenceCases = requiredReference.Length;
        return byVariant;
    }
}
