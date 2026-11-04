using Microsoft.EntityFrameworkCore;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Persistence;

namespace Vivarium.Controller.Builds.Runtime;

/// <summary>Bounded actual process observations; neither an execution owner nor a final-result substitute.</summary>
internal static class RuntimeBuildProgress
{
    internal static string Validate(StepStatus status, BuildAssignment assignment)
    {
        if (status.Phase is not ("FETCHING" or "RUNNING" or "CANCELLATION_CLEANUP" or "COLLECTING")
            || status.StepIndex < -1 || status.StepIndex >= 128 || status.StepIndex >= assignment.Steps.Count
            || (status.Phase is "RUNNING" or "CANCELLATION_CLEANUP") && status.StepIndex < 0
            || (status.Phase is "FETCHING" or "COLLECTING") && status.StepIndex != -1
            || status.Completed is { } completed && (status.Phase != "RUNNING" || completed.StepIndex != status.StepIndex || completed.Skipped))
            throw new InvalidDataException("Execution progress is invalid.");
        if (status.Completed is { } result) RuntimeContainerReceipt.Validate(result, assignment.Steps[result.StepIndex]);
        return status.Completed is not null ? "COMPLETED" : status.Phase == "CANCELLATION_CLEANUP" ? "RUNNING" : status.Phase;
    }

    internal static bool SameResult(RuntimeBuildObservation observed, StepResult? result) => result is not null
        && observed.StepIndex == result.StepIndex && observed.ExitCode == result.ExitCode
        && observed.TimedOut == result.TimedOut && observed.Skipped == result.Skipped
        && (observed.ContainerEvidence is null ? result.Container is null
            : result.Container is not null && ContainerStepReceipt.Parser.ParseFrom(observed.ContainerEvidence).Equals(result.Container));

    // The caller holds the same Agent/Build transaction fence as assignment/result processing.
    internal static async Task<(bool Added, bool Conflict)> ObserveAsync(VivariumDbContext database, long buildId,
        StepStatus status, string phase, DateTimeOffset now, CancellationToken token)
    {
        var observations = await database.RuntimeBuildObservations.Where(o => o.BuildId == buildId).OrderBy(o => o.Ordinal).ToArrayAsync(token);
        var prior = observations.SingleOrDefault(o => o.Phase == phase && o.StepIndex == status.StepIndex);
        if (prior is not null) return (false, phase == "COMPLETED" && !SameResult(prior, status.Completed));
        if (observations.Length >= 258 || phase == "FETCHING" && observations.Length > 0
            || observations.Any(o => o.Phase == "COLLECTING")
            || phase == "RUNNING" && observations.Any(o => o.StepIndex >= status.StepIndex)
            || phase == "COMPLETED" && !observations.Any(o => o.Phase == "RUNNING" && o.StepIndex == status.StepIndex))
            throw new InvalidDataException("Execution progress order is invalid.");
        database.RuntimeBuildObservations.Add(new() { BuildId = buildId, Ordinal = observations.Length,
            Phase = phase, StepIndex = status.StepIndex, ReceivedAt = now,
            ExitCode = status.Completed?.ExitCode, TimedOut = status.Completed?.TimedOut, Skipped = status.Completed?.Skipped,
            ContainerEvidence = status.Completed?.Container?.ToByteArray() });
        return (true, false);
    }
}
