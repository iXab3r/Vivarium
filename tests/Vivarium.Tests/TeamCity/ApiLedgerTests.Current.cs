using System.Text.Json.Nodes;
using Vivarium.ApiLedger;

namespace Vivarium.Tests.TeamCity;

[TestFixture, Category("ApiLedgerControl")]
public sealed class ApiLedgerCurrentTests
{
    private string root = null!;

    [SetUp]
    public void Copy_canonical_registers()
    {
        var repository = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, Ledger.Workstream, "api-ledger.tsv"))) repository = repository.Parent;
        Assert.That(repository, Is.Not.Null);
        root = Path.Combine(Path.GetTempPath(), "vivarium-current-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, Ledger.Workstream));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repository!.FullName, Ledger.Workstream)))
            if (Path.GetExtension(file) is ".tsv" or ".json" or ".md")
                File.Copy(file, Path.Combine(root, Ledger.Workstream, Path.GetFileName(file)));
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(root, true);

    private CurrentStatus Read() => Ledger.ReadCurrent(root, _ => new Identity("new-head", "tree", true));

    [Test]
    public void Explicit_invalid_selected_assessment_fails_closed_without_historical_promotion()
    {
        File.WriteAllText(Path.Combine(root, "selected.json"), "{\"candidate_id\":\"other-head\",\"expected_head\":\"other-head\"}");
        var report = Ledger.ReadCurrent(root, _ => new Identity("new-head", "tree", true), r0Assessment: "selected.json");
        Assert.That(report.Errors, Is.Not.Empty);
        Assert.That(report.EvidenceState, Is.EqualTo("UNQUALIFIED_CURRENT_HEAD"));
        Assert.That(report.SelectedR0!.Green, Is.Zero);
        Assert.That(report.GlobalVariantDenominator, Is.Null);
        Assert.That(report.SelectedAssessments.Single().Path, Is.EqualTo("selected.json"));
    }

    [Test]
    public void Current_head_is_observed_without_relabeling_historical_results_or_inflating_green()
    {
        var report = Read();
        Assert.Multiple(() => {
            Assert.That(report.Head, Is.EqualTo("new-head"));
            Assert.That(report.LegacyAssessment["candidate_id"]!.GetValue<string>(), Is.EqualTo("ledger-accounting-working-tree"));
            Assert.That(report.EvidenceState, Is.EqualTo("UNQUALIFIED_CURRENT_HEAD"));
            Assert.That(report.GlobalVariantDenominator, Is.Null);
            Assert.That(report.GlobalApiComplete, Is.False);
            Assert.That(report.Subsystems.Sum(s => s.MaterializedCases.Length), Is.EqualTo(25));
            Assert.That(report.Subsystems.Sum(s => s.R0Bindings.Length), Is.EqualTo(28));
            Assert.That(report.Subsystems.SelectMany(s => s.HistoricalOutcomes).Count(s => s.EndsWith(":RED")), Is.EqualTo(5));
            Assert.That(report.Subsystems.All(s => s.EvidenceState == "UNKNOWN_CURRENT_COMPATIBILITY"), Is.True);
        });
    }

    [Test]
    public void Broken_case_join_fails_closed()
    {
        var path = Path.Combine(root, Ledger.Workstream, "api-ledger.tsv");
        File.WriteAllText(path, File.ReadAllText(path).Replace("TCREST-SERVER-API-VERSION-TEXT-001", "UNKNOWN-CASE"));
        Assert.Throws<InvalidDataException>(() => Read());
    }

    [Test]
    public void Missing_historical_case_cannot_silently_erase_previous_green()
    {
        var path = Path.Combine(root, Ledger.Workstream, "api-status.json");
        var snapshot = JsonNode.Parse(File.ReadAllText(path))!;
        snapshot["cases"]!.AsArray().RemoveAt(0);
        File.WriteAllText(path, snapshot.ToJsonString());
        Assert.Throws<InvalidDataException>(() => Read());
    }

    [Test]
    public void Historical_green_is_preserved_but_never_transferred_to_new_head()
    {
        var path = Path.Combine(root, Ledger.Workstream, "api-status.json");
        var snapshot = JsonNode.Parse(File.ReadAllText(path))!;
        snapshot["cases"]![0]!["state"] = "GREEN";
        File.WriteAllText(path, snapshot.ToJsonString());
        var report = Read();
        Assert.That(report.Subsystems.SelectMany(s => s.HistoricalOutcomes).Any(s => s.EndsWith(":GREEN")), Is.True);
        Assert.That(report.EvidenceState, Is.EqualTo("UNQUALIFIED_CURRENT_HEAD"));
    }

    [Test]
    public void Source_bytes_and_missing_sources_are_explicit_observations_not_binding_proof()
    {
        var directory = Path.Combine(root, "src/Vivarium.Controller/Catalog");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "CatalogEndpoints.cs");
        File.WriteAllText(path, "original");
        var before = Read();
        File.WriteAllText(path, "changed");
        var after = Read();
        Assert.That(after.CurrentHostSourceObservations.Single().Sha256, Is.Not.EqualTo(before.CurrentHostSourceObservations.Single().Sha256));
        Assert.That(after.Subsystems.SelectMany(s => s.SourceObservations).Any(s => s.Sha256 == null), Is.True);
        Assert.That(after.EvidenceState, Is.EqualTo("UNQUALIFIED_CURRENT_HEAD"));
    }
}

public sealed partial class ApiLedgerTests
{
    [Test]
    public void Git_sha_is_not_the_named_assessment_identity()
    {
        var valid = Read();
        Assert.That(valid.Errors, Is.Empty);
        var wrong = Ledger.Read(root, "arbitrary-git-sha", _ => identity);
        Assert.That(wrong.Errors, Has.Some.Contains("Unknown candidate"));
        Assert.That(wrong.Cases, Is.Empty);
    }
}
