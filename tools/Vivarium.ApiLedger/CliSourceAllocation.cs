namespace Vivarium.ApiLedger;

public static partial class Ledger
{
    // Source responsibility and possible caller reachability, never server dispatch or coverage.
    private static void ValidateCliSourceAllocation(string root,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices,
        ISet<string> metadataInputs, Report report)
    {
        var manifestPath = Workstream + "source-union-checkpoint.json";
        metadataInputs.Add(manifestPath);
        var manifest = Json(root, manifestPath);
        var revision = S(manifest, "cli_source_revision");
        var files = manifest["cli_implementation_files"]!.AsArray()
            .ToDictionary(row => S(row!, "path"), row => S(row!, "sha256"));
        var methods = Table(root, "cli-client-methods.tsv", metadataInputs, "method_id");
        var calls = Table(root, "cli-client-calls.tsv", metadataInputs, "site_id");
        var fragments = Table(root, "cli-path-fragments.tsv", metadataInputs, "fragment_id");
        var allocatedMethods = Table(root, "cli-method-allocation.tsv", metadataInputs, "method_id");
        var allocatedFiles = Table(root, "cli-file-allocation.tsv", metadataInputs, "source");
        Require(files.Count > 0 && methods.Count > 0 && calls.Count > 0 && fragments.Count > 0,
            "Missing known CLI implementation source universe.");
        Require(allocatedFiles.Select(row => row["source"]).ToHashSet().SetEquals(files.Keys),
            "Missing/orphan CLI implementation file allocation.");
        Require(allocatedMethods.Select(row => row["method_id"]).ToHashSet()
            .SetEquals(methods.Select(row => row["method_id"])), "Missing/orphan CLI method allocation.");

        void Source(Dictionary<string, string> row)
        {
            Require(files.TryGetValue(row["source"], out var hash) && row["source_revision"] == revision &&
                row["source_sha256"] == hash, "CLI allocation source lineage differs from the pinned universe.");
        }
        foreach (var row in allocatedFiles) { Source(row); ValidateSourceResponsibility(row, owners, slices); }
        var byFile = allocatedFiles.ToDictionary(row => row["source"]);
        var byMethod = methods.ToDictionary(row => row["method"]);
        var byAllocation = allocatedMethods.ToDictionary(row => row["method_id"]);
        var entries = new HashSet<string>();
        foreach (var row in methods)
        {
            Source(row);
            Require(row["method_id"] == "CLI-METHOD:" + row["method"], "CLI method identity differs.");
            var allocation = byAllocation[row["method_id"]];
            ValidateSourceResponsibility(allocation, owners, slices);
            var file = byFile[row["source"]];
            Require(file["primary_source_subsystem"] == allocation["primary_source_subsystem"] ||
                Split(file["related_source_subsystems"]).Contains(allocation["primary_source_subsystem"]),
                "CLI method owner is not represented by its source file responsibility.");
            Require(allocation["remaining_variant_owner_basis"].Length > 0, "Missing CLI remaining-variant owner basis.");
            if (allocation["candidate_entry_slices"] == "none") continue;
            var declared = Split(allocation["candidate_entry_slices"]);
            Require(declared.Length > 0, "Missing CLI source entry assessment.");
            foreach (var entry in declared)
            {
                var slice = Find(slices, entry, "CLI candidate source entry");
                if (slice["milestone"] is "R0" or "PILOT") entries.Add(row["method"]);
            }
        }
        foreach (var row in calls)
        {
            Source(row);
            var caller = Find(byMethod, row["owner_method"], "CLI caller method");
            Find(byMethod, row["called_method"], "CLI called method");
            Require(caller["source"] == row["source"] && caller["source_sha256"] == row["source_sha256"],
                "CLI call-site owner source differs.");
        }
        foreach (var row in fragments)
        {
            Source(row);
            Require(row["fragment_id"].StartsWith("CLI-PATH:" + row["source"] + ":", StringComparison.Ordinal),
                "CLI path-fragment identity differs.");
        }
        // This deliberately overapproximates syntactic calls. It is not a runtime branch oracle.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var call in calls)
                if (entries.Contains(call["owner_method"]) && entries.Add(call["called_method"])) changed = true;
        }
        report.CliImplementationFileAllocationRows = allocatedFiles.Count;
        report.CliMethodAllocationRows = allocatedMethods.Count;
        report.CliCallSiteAllocationRows = calls.Count;
        report.CliPathFragmentAllocationRows = fragments.Count;
        report.CliPotentialEarlyMethods = entries.Count;
    }

    private static void ValidateSourceResponsibility(Dictionary<string, string> row,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices)
    {
        var owner = Find(owners, row["primary_source_subsystem"], "source owner");
        var full = Find(slices, row["full_owner_slice"], "source full-owner slice");
        Require(full["subsystem_id"] == row["primary_source_subsystem"] &&
            full["implementation_issue"] == owner["primary_issue"] &&
            row["full_implementation_issue"] == full["implementation_issue"] &&
            row["full_contract_issue"] == full["rest_contract_issue"] &&
            row["full_acceptance_issue"] == full["rest_acceptance_issue"], "Wrong source owner/task block.");
        foreach (var related in Split(row["related_source_subsystems"]))
        {
            Find(owners, related, "related source owner");
            Require(related != row["primary_source_subsystem"], "Primary source owner repeated as related.");
        }
        Require(row["rationale"].Length > 0 && row["state"] == "ALLOCATED_SOURCE_ONLY_NOT_DISPATCH_OR_ACCEPTANCE",
            "Source responsibility cannot claim dispatch, a frozen variant or acceptance.");
    }
}
