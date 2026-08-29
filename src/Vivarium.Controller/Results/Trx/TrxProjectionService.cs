using Vivarium.Controller.Blobs;

namespace Vivarium.Controller.ResultAdapters.Trx;

public sealed class TrxProjectionService(
    TrxProjectionStore store,
    BlobStore blobs,
    TimeProvider timeProvider,
    TrxResultAdapter? adapter = null) : IBuildResultProjectionParticipant
{
    private readonly TrxResultAdapter adapter = adapter ?? new TrxResultAdapter();
    private readonly SemaphoreSlim projector = new(1, 1);

    public async Task ProjectTerminalBuildAsync(
        string buildId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        await projector.WaitAsync(cancellationToken);
        try
        {
            var inputs = await store.ListInputsAsync(buildId);
            var fingerprint = TrxProjectionStore.InputFingerprint(inputs);
            await store.BeginAsync(buildId, fingerprint, inputs.Count, timeProvider.GetUtcNow());
            var attempts = new List<TrxProjectionAttempt>(inputs.Count);
            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts.Add(await ProjectOneAsync(input, cancellationToken));
            }

            await store.CompleteAsync(
                buildId,
                fingerprint,
                attempts,
                timeProvider.GetUtcNow());
        }
        finally
        {
            projector.Release();
        }
    }

    public async Task ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        foreach (var buildId in await store.ListPendingBuildIdsAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProjectTerminalBuildAsync(buildId, cancellationToken);
        }
    }

    private async Task<TrxProjectionAttempt> ProjectOneAsync(
        TrxProjectionInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = blobs.GetPath(input.Sha256);
            if (path is null || new FileInfo(path).Length != input.Size)
            {
                return Failed(input, "trx_input_unavailable", "The raw TRX artifact is unavailable.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var projection = await adapter.ProjectAsync(
                new TrxProjectionContext(
                    input.BuildId,
                    input.ProjectId,
                    TrxProjectionStore.TestSourceId(input),
                    input.ArtifactId,
                    input.ArtifactPath),
                stream,
                cancellationToken);
            return new TrxProjectionAttempt(input, projection);
        }
        catch (TrxProjectionException exception)
        {
            return Failed(input, exception.Code, Bound(exception.Message));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failed(input, "trx_input_unavailable", "The raw TRX artifact is unavailable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(input, "trx_projection_failed", "TRX projection failed safely.");
        }
    }

    private static TrxProjectionAttempt Failed(
        TrxProjectionInput input,
        string code,
        string summary) => new(input, Projection: null, code, summary);

    private static string Bound(string value)
    {
        var safe = new string(value
            .Take(512)
            .Select(character => char.IsControl(character) ? '?' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "TRX projection failed." : safe;
    }
}
