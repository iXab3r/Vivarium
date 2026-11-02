namespace Vivarium.ApiLedger;

public static partial class Ledger
{
    // Accountability for known gaps; no disposition is evidence that behavior is implemented.
    private static void ValidateGapDispositions(string root,
        List<Dictionary<string, string>> gaps,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices,
        ISet<string> metadataInputs, Report report)
    {
        var rows = Table(root, "gap-dispositions.tsv", metadataInputs, "gap_id");
        Require(rows.Select(row => row["gap_id"]).ToHashSet().SetEquals(gaps.Select(gap => gap["gap_id"])),
            "Missing/orphan source-gap disposition.");
        foreach (var row in rows)
        {
            var gap = gaps.Single(gap => gap["gap_id"] == row["gap_id"]);
            var owner = Find(owners, row["primary_subsystem"], "gap primary owner");
            foreach (var related in Split(row["related_subsystems"]))
            {
                Find(owners, related, "gap related owner");
                Require(related != row["primary_subsystem"], "Gap primary owner repeated as related owner.");
            }
            var early = Find(slices, row["earliest_integration_slice"], "gap earliest integration");
            var full = Find(slices, row["full_owner_slice"], "gap full-owner slice");
            Require(full["subsystem_id"] == row["primary_subsystem"] && full["implementation_issue"] == owner["primary_issue"],
                "Wrong source-gap full owner.");
            foreach (var (prefix, slice) in new[] { ("early", early), ("full", full) })
                Require(row[prefix + "_implementation_issue"] == slice["implementation_issue"] &&
                    row[prefix + "_contract_issue"] == slice["rest_contract_issue"] &&
                    row[prefix + "_acceptance_issue"] == slice["rest_acceptance_issue"], "Wrong source-gap task block.");
            Require(row["source_refs"] == gap["source_ref"], "Source-gap disposition lost its original source reference.");
            foreach (var field in new[] { "authority", "early_required_subset", "remaining_dimensions", "due_freeze", "assessor", "rationale", "evidence_refs" })
                Require(row[field].Length > 0, "Missing source-gap impact/rationale/evidence: " + field);
            Require(row["early_impact"] is "YES" or "UNKNOWN" or "NO_WITH_EVIDENCE", "Unknown source-gap impact classification.");
            if (row["early_impact"] == "UNKNOWN")
            {
                Require(early["milestone"] == "P0" && row["state"] == "OPEN_EARLY_IMPACT",
                    "Unknown early impact cannot be deferred or accepted.");
                report.UnresolvedEarlyImpactGaps++;
            }
            else Require(row["state"] == "ALLOCATED_NOT_ACCEPTED", "Source-gap allocation is not acceptance or closure.");
        }
        report.SourceGapDispositionRows = rows.Count;
    }
}
