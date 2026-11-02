using System.Text.Json.Nodes;

namespace Vivarium.ApiLedger;

public static partial class Ledger
{
    private static void ValidateSupplementalSourceAllocation(string root,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices,
        ISet<string> metadataInputs, Report report)
    {
        var inputPath = Workstream + "supplemental-source-inputs.json";
        var selectionPath = Workstream + "r0-client-selection.json";
        metadataInputs.Add(inputPath); metadataInputs.Add(selectionPath);
        var input = Json(root, inputPath); var selection = Json(root, selectionPath);
        JsonNode Frozen(JsonNode reference)
        {
            var path = S(reference, "path");
            Require(Hash(root, path) == S(reference, "sha256"), "Supplemental source reference hash differs.");
            metadataInputs.Add(path);
            return Json(root, path);
        }
        var deltaReference = input["selected_source_delta"]!;
        var selectedReference = selection["independent_verification"]!["source_difference_assessment"]!;
        Require(S(deltaReference, "path") == S(selectedReference, "path") &&
            S(deltaReference, "sha256") == S(selectedReference, "sha256"), "Supplemental source differs from selected client.");
        var producer = Frozen(input["producer_source"]!);
        var delta = Frozen(deltaReference);
        var core = Frozen(input["selected_core_blobs"]!);
        Require(S(core, "revision") == S(delta, "selectedSourceRevision"), "Selected Core source revision differs.");
        var coreFiles = core["files"]!.AsArray();
        Require(coreFiles.Select(file => S(file!, "path")).ToHashSet()
            .SetEquals(delta["coreChangedSources"]!.AsArray().Select(file => file!.GetValue<string>())),
            "Selected Core source delta is missing or orphaned.");

        var expected = new Dictionary<string, Dictionary<string, string>>();
        void Add(string kind, string repository, string revision, string path, string hash)
        {
            var id = repository + "@" + revision + ":" + path;
            expected.Add(kind + "\t" + id, new() { ["repository"] = repository, ["source_revision"] = revision,
                ["source_path"] = path, ["source_sha256"] = hash });
        }
        foreach (var file in producer["files"]!.AsArray())
            Add("producer-consumer", S(file!, "repository"), S(file!, "revision"), S(file!, "path"), S(file!, "sha256"));
        foreach (var file in coreFiles)
            Add("selected-core-change", "iXab3r/EyeAuras.CliFactory", S(core, "revision"), S(file!, "path"), S(file!, "sha256"));
        var rows = Table(root, "supplemental-source-allocation.tsv", metadataInputs, "source_kind", "source_id");
        Require(rows.Select(row => row["source_kind"] + "\t" + row["source_id"]).ToHashSet().SetEquals(expected.Keys),
            "Missing/orphan known producer/Core source allocation.");
        foreach (var row in rows)
        {
            var source = expected[row["source_kind"] + "\t" + row["source_id"]];
            Require(source.All(field => row[field.Key] == field.Value), "Supplemental source lineage differs.");
            ValidateSourceResponsibility(row, owners, slices);
            var early = Find(slices, row["earliest_integration_slice"], "supplemental source earliest integration");
            Require(row["early_implementation_issue"] == early["implementation_issue"] &&
                row["early_contract_issue"] == early["rest_contract_issue"] &&
                row["early_acceptance_issue"] == early["rest_acceptance_issue"], "Wrong supplemental source integration task block.");
        }

        var inventory = Table(root, "source-inventory.tsv", metadataInputs, "path");
        var preserved = Table(root, "source-disposition.tsv", metadataInputs, "path");
        Require(preserved.Select(row => row["path"]).ToHashSet()
            .SetEquals(inventory.Where(row => row["bucket"] == "production").Select(row => row["path"])),
            "Missing/orphan production source preservation responsibility.");
        foreach (var row in preserved)
        {
            Find(owners, row["subsystem_id"], "preservation source owner");
            Require(slices.Values.Any(slice => slice["implementation_issue"] == row["replacement_task"] ||
                Split(slice["implementation_issues"]).Contains(row["replacement_task"])),
                "Unknown preservation source task.");
            Require(row["path"].StartsWith("src/", StringComparison.Ordinal) && Hash(root, row["path"]) == row["sha256"] &&
                row["proof_state"] == "SOURCE_DISPOSITION_ONLY_NOT_REPLACEMENT_ACCEPTANCE",
                "Preserved source bytes differ or disposition claims acceptance.");
            metadataInputs.Add(row["path"]);
            var baselines = Split(row["baseline_test_files"]);
            Require(baselines.Length > 0 && baselines.All(path => path.StartsWith("tests/", StringComparison.Ordinal) && File.Exists(SafePath(root, path))),
                "Missing source preservation baseline anchor.");
            foreach (var baseline in baselines) metadataInputs.Add(baseline);
        }
        report.ProducerConsumerSourceAllocationRows = rows.Count(row => row["source_kind"] == "producer-consumer");
        report.SelectedCoreSourceAllocationRows = rows.Count(row => row["source_kind"] == "selected-core-change");
        report.ProductionSourceDispositionRows = preserved.Count;
    }
}
