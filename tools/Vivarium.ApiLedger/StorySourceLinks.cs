namespace Vivarium.ApiLedger;

public static partial class Ledger
{
    // Discovery links remain distinct from a frozen variant and from an executed actor/effect case.
    private static void ValidateStorySources(string root,
        Dictionary<string, Dictionary<string, string>> apis,
        Dictionary<string, Dictionary<string, string>> cases,
        Dictionary<string, Dictionary<string, string>> owners,
        Dictionary<string, Dictionary<string, string>> slices,
        ISet<string> metadataInputs,
        Report report)
    {
        var stories = Table(root, "stories.tsv", metadataInputs, "story_id");
        var byStory = stories.ToDictionary(row => row["story_id"]);
        var scenarios = Table(root, "story-scenarios.tsv", metadataInputs, "story_id", "scenario_id", "surface");
        var storyLinks = Table(root, "story-links.tsv", metadataInputs, "source_id");
        var installed = Table(root, "installed-http-occurrences.tsv", metadataInputs, "profile", "method", "path");
        var relations = Table(root, "source-relations.tsv", metadataInputs, "api_id", "profile", "method", "path");
        var fixtures = Table(root, "rest-fixture-inventory.tsv", metadataInputs, "id");
        var cli = Table(root, "cli-source-relations.tsv", metadataInputs, "fixture_id");
        var native = Table(root, "native-http-occurrences.tsv", metadataInputs, "source_id");
        var messages = Table(root, "agent-message-occurrences.tsv", metadataInputs, "source_id");
        var gaps = Table(root, "story-gaps.tsv", metadataInputs, "gap_id");
        var byGap = gaps.ToDictionary(row => row["gap_id"]);
        var implementationLinks = Table(root, "story-implementation-links.tsv", metadataInputs, "story_id", "slice_id");
        var dependencies = Table(root, "story-dependencies.tsv", metadataInputs, "story_id", "dependency_ref", "relation", "dependent_stage");

        void StoryScenario(Dictionary<string, string> row, string surface, string source)
        {
            Find(byStory, row["story_id"], "source story");
            var scenario = scenarios.SingleOrDefault(s => s["story_id"] == row["story_id"] && s["scenario_id"] == row["scenario_id"] && s["surface"] == surface);
            Require(scenario is not null && Split(scenario["source_ids"]).Contains(source), "Missing source/story surface scenario or reverse source binding.");
        }
        static string SchemaKey(Dictionary<string, string> row) =>
            row["profile"] + "\t" + row["method"] + "\t" + row["path"];
        static bool HashShape(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        foreach (var gap in gaps)
        {
            foreach (var story in Split(gap["story_ids"])) Find(byStory, story, "gap story");
            Require(Split(gap["story_ids"]).Length > 0 && gap["owner_task"].Length > 0 && gap["due_freeze"].Length > 0 &&
                gap["next_proof"].Length > 0 && gap["source_ref"].Length > 0, "Unowned source gap or missing due/proof reference.");
            Require(new[] { "open-decomposition", "OPEN-NOT-COVERAGE", "OPEN-SOURCE-IDENTITY",
                "DISCOVERED-PARTIAL-NOT-ACCEPTED", "DISCOVERY-GAP-NOT-NORMALIZED" }.Contains(gap["state"]),
                "Source-gap closure needs explicit evidence; this unfrozen register cannot claim it.");
        }
        Require(storyLinks.Select(row => row["source_id"]).ToHashSet().SetEquals(apis.Keys),
            "Story/discovery source bindings differ (missing or orphan source).");
        foreach (var link in storyLinks)
        {
            var api = Find(apis, link["source_id"], "story API");
            StoryScenario(link, "compatible-rest", link["source_id"]);
            Require(link["subsystem_id"] == api["subsystem_id"] && link["active_owner_issue"] == api["subsystem_issue"],
                "Wrong primary story/source owner.");
            Require(Split(link["case_id"]).ToHashSet().SetEquals(Split(api["variant_case_ids"])),
                "Story/case reverse binding differs from the owning API ledger.");
            Require(link["state"] == "candidate-intent-allocation-not-coverage", "Story source intent is not acceptance.");
        }

        var schema = installed.ToDictionary(SchemaKey);
        foreach (var occurrence in installed)
        {
            var pointer = "/paths/" + occurrence["path"].Replace("~", "~0").Replace("/", "~1") + "/" + occurrence["method"].ToLowerInvariant();
            Require(occurrence["json_pointer"] == pointer && HashShape(occurrence["schema_sha256"]),
                "Invalid schema occurrence pointer/hash.");
        }
        Require(installed.GroupBy(row => row["profile"]).All(group => group.Select(row => row["schema_sha256"]).Distinct().Count() == 1),
            "Different schema byte hashes share one pinned source profile.");
        Require(relations.Select(row => row["api_id"]).ToHashSet().SetEquals(apis.Keys) &&
            relations.Select(SchemaKey).ToHashSet().SetEquals(schema.Keys), "Missing/orphan documentation/schema source relation.");
        foreach (var relation in relations)
        {
            var occurrence = Find(schema, SchemaKey(relation), "schema occurrence");
            Require(relation["json_pointer"] == occurrence["json_pointer"] &&
                relation["declared_operation_id"] == occurrence["declared_operation_id"], "Changed schema source identity.");
            if (relation["gap_id"].Length > 0) Find(byGap, relation["gap_id"], "source relation gap");
            Require(relation["state"] == "candidate-source-relation-not-normalized-or-covered", "Source candidates are not semantic aliases or coverage.");
        }

        var byFixture = fixtures.ToDictionary(row => row["id"]);
        Require(cli.Select(row => row["fixture_id"]).ToHashSet().SetEquals(byFixture.Keys), "Missing/orphan CLI fixture source join.");
        foreach (var link in cli)
        {
            var fixture = Find(byFixture, link["fixture_id"], "CLI fixture");
            foreach (var pair in new[] { ("kind", "kind"), ("method", "method"), ("concrete_path", "path"),
                ("source", "source"), ("table", "table"), ("case_index", "case_index"), ("command_prefix", "command") })
                Require(link[pair.Item1] == fixture[pair.Item2], "CLI fixture occurrence changed in its source join.");
            StoryScenario(link, "unchanged-cli", link["fixture_id"]);
            var candidates = Split(link["candidate_api_ids"]);
            var paths = Split(link["candidate_schema_paths"]);
            var preferred = Find(apis, link["preferred_api_id"], "preferred CLI API");
            Require(candidates.Contains(link["preferred_api_id"]) && paths.Contains(link["preferred_schema_path"]) &&
                relations.Any(r => r["api_id"] == link["preferred_api_id"] && r["method"] == link["method"] && r["path"] == link["preferred_schema_path"]),
                "CLI preferred API/path is not a recorded candidate source occurrence.");
            foreach (var candidate in candidates)
            {
                Find(apis, candidate, "CLI candidate API");
                Require(relations.Any(r => r["api_id"] == candidate && r["method"] == link["method"] && paths.Contains(r["path"])),
                    "CLI candidate API/path source mismatch.");
            }
            Require(link["active_owner_issue"] == preferred["subsystem_issue"], "Wrong primary CLI source owner.");
            Find(byGap, link["gap_id"], "CLI source qualification gap");
            Require(link["state"] == "CANDIDATE_SOURCE_JOIN_NOT_VARIANT_OR_COVERAGE", "CLI source joins cannot claim an executed CLI entry.");
        }
        foreach (var row in native)
        {
            StoryScenario(row, "native-http", row["source_id"]);
            Find(owners, row["subsystem_id"], "native source owner");
        }
        foreach (var row in messages) StoryScenario(row, "agenthub", row["source_id"]);

        var allSources = storyLinks.Select(row => row["source_id"]).Concat(cli.Select(row => row["fixture_id"]))
            .Concat(native.Select(row => row["source_id"])).Concat(messages.Select(row => row["source_id"])).ToArray();
        Require(allSources.Distinct().Count() == allSources.Length, "Source identity collision across discovery kinds.");
        var sourceSet = allSources.ToHashSet();
        var scenarioCases = new HashSet<string>();
        foreach (var scenario in scenarios)
        {
            var story = Find(byStory, scenario["story_id"], "scenario story");
            Require(story["issue"] == scenario["story_issue"] && scenario["scenario_id"].StartsWith(scenario["story_id"] + "/", StringComparison.Ordinal),
                "Wrong story/scenario identity.");
            Require(Split(scenario["candidate_canonical_ac"]).Length > 0 && Split(scenario["candidate_model_invariants"]).Length > 0 &&
                Split(scenario["candidate_canonical_ac"]).All(Split(story["canonical_ac"]).Contains) &&
                Split(scenario["candidate_model_invariants"]).All(Split(story["applicable_tcm_from_ac"]).Contains), "Missing/unknown story AC/model intent link.");
            foreach (var source in Split(scenario["source_ids"])) Require(sourceSet.Contains(source), "Orphan scenario source.");
            foreach (var id in Split(scenario["retained_case_ids"]))
            {
                var testCase = Find(cases, id, "scenario case");
                Require(Split(testCase["api_ids"]).Intersect(Split(scenario["source_ids"])).Any(), "Scenario case has no declared source relation.");
                scenarioCases.Add(id);
            }
            Require(scenario["independent_oracle_and_boundary"].Length > 0 && scenario["variant_freeze"].StartsWith("UNFROZEN", StringComparison.Ordinal) &&
                scenario["state"] == "SCENARIO_INTENT_NOT_BINDING_OR_COVERAGE", "Scenario intent cannot claim frozen effects or acceptance.");
        }
        Require(scenarioCases.SetEquals(cases.Keys), "Missing required retained scenario case.");
        foreach (var link in implementationLinks)
        {
            var story = Find(byStory, link["story_id"], "implementation story");
            var slice = Find(slices, link["slice_id"], "story implementation slice");
            Require(story["issue"] == link["story_issue"] && slice["implementation_issue"] == link["implementation_issue"] &&
                slice["rest_contract_issue"] == link["contract_issue"] && slice["rest_acceptance_issue"] == link["acceptance_issue"],
                "Wrong story implementation task/slice link.");
        }
        Require(stories.All(story => implementationLinks.Any(link => link["story_id"] == story["story_id"])), "Story has no owning implementation contribution.");
        var knownTasks = slices.Values.SelectMany(slice => Split(slice["implementation_issues"])
                .Concat([slice["implementation_issue"], slice["rest_contract_issue"], slice["rest_acceptance_issue"]]))
            .Concat(stories.SelectMany(story => Split(story.GetValueOrDefault("legacy_context", "")).Select(value => value.TrimStart('#'))))
            .ToHashSet();
        foreach (var dependency in dependencies)
        {
            Find(byStory, dependency["story_id"], "dependency story");
            foreach (var target in Split(dependency["dependency_ref"]))
            {
                if (target == "none") continue;
                if (target.StartsWith('#')) Require(knownTasks.Contains(target[1..]), "Unknown technical evidence/task dependency.");
                else Find(byStory, target, "dependent story");
            }
            Require(new[] { "uses-ready-portion", "contributes", "needs-ready-evidence", "assessed-dependency-basis", "uses-reference" }.Contains(dependency["relation"]),
                "Dependency readiness is not a whole-story issue-closure blocker.");
            Require(new[] { "dependency_ref", "dependent_stage", "required_output_or_contribution", "owner_task", "readiness_or_unblock_condition", "closure_semantics" }
                .All(key => dependency[key].Length > 0), "Missing bounded dependency owner/output/readiness contract.");
        }
        Require(stories.All(story => dependencies.Any(row => row["story_id"] == story["story_id"])), "Missing story dependency assessment basis.");

        report.ActorGoalStories = stories.Count;
        report.SurfaceScenarioCandidates = scenarios.Count;
        report.StoryDependencyReferences = dependencies.Count;
        report.SchemaSourceOccurrences = installed.Count;
        report.CliFixtureSourceOccurrences = cli.Count;
        report.NativeHttpSourceOccurrences = native.Count;
        report.AgentEnvelopeSourceOccurrences = messages.Count;
        report.KnownStorySourceGaps = gaps.Count;
        report.DistinctScenarioLinkedCases = scenarioCases.Count;
        report.StorySourceJoinState = "REFERENTIAL_LINKS_VALID; FROZEN_VARIANTS_AND_EFFECTS_NOT_PROVED";
    }
}
