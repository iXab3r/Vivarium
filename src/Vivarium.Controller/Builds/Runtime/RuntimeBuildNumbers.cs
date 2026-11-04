using System.Text;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Builds.Runtime;

internal static class RuntimeBuildNumbers
{
    // Caller owns the exact Agent/lease transaction. Validate the complete input before staging any projection.
    internal static async Task<bool> ApplyAsync(VivariumDbContext database, RuntimeBuild build, BuildAssignment assignment,
        IReadOnlyList<BuildNumberChanged> changes, bool complete, bool closed, DateTimeOffset now, CancellationToken token)
    {
        if (assignment.NumberReportingContractMajor == 0) return changes.Count == 0;
        if (assignment.NumberReportingContractMajor != 1 || changes.Count > BuildNumberProfile.MaximumChanges) return false;
        try { foreach (var change in changes) BuildNumberProfile.Validate(change, assignment.BuildId); }
        catch (InvalidDataException) { return false; }
        if (changes.Select(c => c.Sequence).Distinct().Count() != changes.Count) return false;
        if (complete && changes.Where((change, index) => change.Sequence != index + 1).Any()) return false;
        foreach (var change in changes)
        {
            var bytes = Encoding.UTF8.GetBytes(change.Value);
            foreach (var secret in assignment.OutputSecrets)
                if (secret.Length != 0 && bytes.AsSpan().IndexOf(secret.Span) >= 0) return false;
        }
        var retained = await database.RuntimeBuildNumberObservations.Where(n => n.BuildId == build.Id).ToArrayAsync(token);
        var values = retained.ToDictionary(n => checked((uint)n.Sequence), n => n.Value);
        if (complete && values.Keys.Any(sequence => !changes.Any(c => c.Sequence == sequence))) return false;
        foreach (var change in changes)
        {
            if (values.TryGetValue(change.Sequence, out var value)) { if (value != change.Value) return false; }
            else if (closed) return false;
        }
        var latest = retained.Length == 0 ? 0u : checked((uint)retained.Max(n => n.Sequence));
        foreach (var change in changes)
        {
            if (values.ContainsKey(change.Sequence)) continue;
            database.RuntimeBuildNumberObservations.Add(new() { BuildId = build.Id, Sequence = checked((int)change.Sequence), Value = change.Value, ReceivedAt = now });
            await RuntimeBuildAudit.StageAsync(database, build, now, "build.number.changed", details:
                new Dictionary<string, string> { ["sequence"] = change.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) }, token: token);
            if (change.Sequence > latest) { latest = change.Sequence; build.Number = change.Value; }
        }
        return true;
    }
}
