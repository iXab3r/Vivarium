using System.Text.Json.Nodes;
using Vivarium.ApiLedger;

namespace Vivarium.Tests.TeamCity;

public sealed partial class ApiLedgerTests
{
    private void PreparePilotDeclaration()
    {
        File.AppendAllText(Path.Combine(root, Ledger.Workstream + "subsystem-slices.tsv"),
            "SS-PROTO@PILOT\tSS-PROTO\tPILOT\t12\t35;36\t33\t42\tUNVERIFIED\n");
        File.AppendAllText(Path.Combine(root, Ledger.Workstream + "api-blocks.tsv"),
            "PILOT\tSS-PROTO@PILOT\t33\t35;36\t42\tUNVERIFIED\tunfrozen\n");
        ReplaceTable("api-ledger.tsv", "variant_case_ids", "variant_case_ids\tdeclared_variant_ids");
        ReplaceTable("api-ledger.tsv", "CASE-1;CASE-2", "CASE-1;CASE-2\tDECLARED-1");
        Table("pilot-contract-variants.tsv", "variant_id\tapi_ids\tintegration_slice\tprimary_owner\tcontribution_subsystems\tcontract_issue\tacceptance_issue\tstate\tsource_path\tprincipal_and_current_authority\tseed_and_prior_state\trequest_or_action\texpected_response\texpected_effect\tcompletion\tcomparison_bindings\tproduct_binding",
            "DECLARED-1\tTCREST:rootapi.getApiVersion\tSS-PROTO@PILOT\tSS-PROTO\tSS-PROTO\t33\t42\tDECLARED_REFERENCE_GAPS_NOT_WIRE_GREEN\tinput.cs\tunknown authority\tseed\trequest\tunknown\tunknown\tpending\tselected only\tunbound");
        Write(Ledger.Workstream + "pilot-contract-scope.json", "{\"contract_issue\":33,\"acceptance_issue\":42,\"declared_variant_ids\":[\"DECLARED-1\"]}");
        RebindMetadataInputs();
    }

    [Test]
    public void Pilot_declaration_does_not_inflate_materialized_or_green_or_change_R0()
    {
        var before = Read();
        PreparePilotDeclaration();
        var after = Read();
        Assert.That(after.Errors, Is.Empty);
        Assert.That(after.DeclaredPilotVariants, Is.EqualTo(1));
        Assert.That(after.MaterializedCases, Is.EqualTo(before.MaterializedCases));
        Assert.That(after.ProductGreenCases, Is.EqualTo(before.ProductGreenCases));
        Assert.That(after.SpecifiedR0Variants, Is.EqualTo(before.SpecifiedR0Variants));
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("foreign-owner")]
    [TestCase("api")]
    [TestCase("reverse")]
    public void Pilot_declaration_rejects_invalid_joins(string corruption)
    {
        PreparePilotDeclaration();
        Assert.That(Read().Errors, Is.Empty);
        switch (corruption)
        {
            case "missing": File.Delete(Path.Combine(root, Ledger.Workstream + "pilot-contract-variants.tsv")); break;
            case "duplicate":
                var path = Path.Combine(root, Ledger.Workstream + "pilot-contract-variants.tsv");
                File.AppendAllText(path, File.ReadAllLines(path)[1] + "\n"); break;
            case "foreign-owner": ReplaceTable("pilot-contract-variants.tsv", "SS-PROTO@PILOT\tSS-PROTO", "SS-PROTO@PILOT\tSS-FOREIGN"); break;
            case "api": ReplaceTable("pilot-contract-variants.tsv", "TCREST:rootapi.getApiVersion", "TCREST:foreign.api"); break;
            case "reverse": ReplaceTable("api-ledger.tsv", "CASE-1;CASE-2\tDECLARED-1", "CASE-1;CASE-2\t"); break;
        }
        Assert.That(Read().Status, Is.EqualTo("INVALID_ACCOUNTING"));
        Assert.That(Read().ProductGreenCases, Is.Zero);
    }
}
