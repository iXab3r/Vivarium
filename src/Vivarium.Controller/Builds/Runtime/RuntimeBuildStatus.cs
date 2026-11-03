using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Persistence;
using Vivarium.Definitions;
using System.Text.Json;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildStatus
{
    internal static async Task<string> FailureAsync(VivariumDbContext database, RuntimeBuild build, BuildResult result, CancellationToken token)
    {
        var failed = result.Steps.FirstOrDefault(s => !s.Skipped && !s.TimedOut && s.ExitCode != 0);
        if (failed is null) return "Failure";
        var current = await DefinitionAsync(database, build.Id, token);
        var step = current.Steps[failed.StepIndex];
        var prior = await database.RuntimeBuilds.AsNoTracking().Where(b => b.BuildTypeId == build.BuildTypeId
            && b.Id != build.Id && b.State == "FINISHED").OrderByDescending(b => b.FinishedAt).ThenByDescending(b => b.Id).FirstOrDefaultAsync(token);
        var repeated = false;
        if (prior?.Status == "FAILURE")
        {
            var receipt = await database.RuntimeBuildResults.AsNoTracking().SingleOrDefaultAsync(r => r.BuildId == prior.Id, token);
            if (receipt is not null)
            {
                var previous = BuildResult.Parser.ParseFrom(receipt.Evidence).Steps.FirstOrDefault(s => !s.Skipped && !s.TimedOut && s.ExitCode != 0);
                if (previous is not null && previous.ExitCode == failed.ExitCode)
                {
                    var definition = await DefinitionAsync(database, prior.Id, token);
                    repeated = definition.Steps[previous.StepIndex].Id == step.Id;
                }
            }
        }
        return ExitFailure(failed.ExitCode, step.Name, repeated);
    }

    internal static string ExitFailure(int code, string step, bool repeated) =>
        $"Exit code {code.ToString(System.Globalization.CultureInfo.InvariantCulture)} (Step: {step} (Command Line))" + (repeated ? "" : " (new)");

    private static async Task<ResolvedConfiguration> DefinitionAsync(VivariumDbContext database, long id, CancellationToken token)
    {
        var input = await database.RuntimeBuildInputs.AsNoTracking().SingleAsync(i => i.BuildId == id, token);
        var definition = await database.CatalogBuildDefinitions.AsNoTracking().SingleAsync(d => d.BuildTypeId == input.BuildTypeId && d.RevisionId == input.RevisionId, token);
        var value = JsonSerializer.Deserialize<ResolvedConfiguration>(definition.PlanJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("The collected Build definition is invalid.");
        if (IsolatedDefinitionEvaluator.Hash(JsonSerializer.Serialize(value, DefinitionValidation.Json)) != definition.PlanHash)
            throw new InvalidDataException("The collected Build definition integrity differs.");
        return value;
    }
}
