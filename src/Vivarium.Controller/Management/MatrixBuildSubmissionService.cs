using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Scheduling;

namespace Vivarium.Controller.Management;

public sealed class MatrixBuildValidationException(string message) : Exception(message);

/// <summary>Validates a complete matrix before handing one atomic write to <see cref="MatrixBuildStore"/>.</summary>
public sealed class MatrixBuildSubmissionService
{
    private readonly MatrixBuildStore store;
    private readonly AgentStore agents;
    private readonly BlobStore blobs;
    private readonly BuildQueueService queue;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan defaultQueueWaitTimeout;

    public MatrixBuildSubmissionService(
        MatrixBuildStore store,
        AgentStore agents,
        BlobStore blobs,
        BuildQueueService queue,
        TimeProvider timeProvider,
        TimeSpan? defaultQueueWaitTimeout = null)
    {
        this.store = store;
        this.agents = agents;
        this.blobs = blobs;
        this.queue = queue;
        this.timeProvider = timeProvider;
        this.defaultQueueWaitTimeout =
            defaultQueueWaitTimeout ?? ControllerOptions.DefaultBuildQueueWaitTimeout;
        if (this.defaultQueueWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultQueueWaitTimeout), "queue wait timeout must be positive");
        }
    }

    public async Task<BuildRef> SubmitAsync(SubmitBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = Normalize(request);
        var existing = await store.FindIdempotentAsync(canonical);
        if (existing != null)
        {
            queue.NotifyChanged();
            return existing;
        }

        var knownAgents = await agents.ListAsync();
        try
        {
            Validate(canonical, knownAgents);
        }
        catch (MatrixBuildValidationException)
        {
            // A concurrent exact submission may have committed after the first idempotency read.
            existing = await store.FindIdempotentAsync(canonical);
            if (existing != null)
            {
                queue.NotifyChanged();
                return existing;
            }

            throw;
        }

        var requestHash = Hash(canonical.ToByteArray());
        var definitionHash = Hash(canonical.DefinitionSnapshot.Span);
        var build = await store.SubmitAsync(
            canonical,
            requestHash,
            definitionHash,
            timeProvider.GetUtcNow(),
            defaultQueueWaitTimeout);
        queue.NotifyChanged();
        return build;
    }

    private void Validate(SubmitBuildRequest request, IReadOnlyList<StoredAgent> knownAgents)
    {
        if (request.RequestId.Length == 0)
        {
            throw new MatrixBuildValidationException("request_id is required");
        }

        if (request.Project.Length == 0)
        {
            throw new MatrixBuildValidationException("project is required");
        }

        if (request.Configuration.Length == 0)
        {
            throw new MatrixBuildValidationException("configuration is required");
        }

        if (request.Cells.Count == 0)
        {
            throw new MatrixBuildValidationException("at least one matrix cell is required");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in request.Cells)
        {
            if (cell.Name.Length == 0)
            {
                throw new MatrixBuildValidationException("matrix cell names cannot be empty");
            }

            if (!names.Add(cell.Name))
            {
                throw new MatrixBuildValidationException($"duplicate matrix cell name '{cell.Name}'");
            }

            if (cell.Assignment == null)
            {
                throw new MatrixBuildValidationException(
                    $"matrix cell '{cell.Name}' requires an assignment");
            }

            if (cell.QueueTimeoutSec < 0)
            {
                throw new MatrixBuildValidationException(
                    $"matrix cell '{cell.Name}' queue_timeout_sec cannot be negative");
            }

            if (cell.Assignment.BuildId.Length != 0)
            {
                throw new MatrixBuildValidationException(
                    $"matrix cell '{cell.Name}' assignment build_id must be empty");
            }

            try
            {
                BuildAdmission.EnsureSupported(cell.Assignment);
            }
            catch (NotSupportedException exception)
            {
                throw new MatrixBuildValidationException(exception.Message);
            }

            if (!AgentCompatibilityMatcher.TryParse(
                    cell.AgentExpression, out _, out var parseError))
            {
                throw new MatrixBuildValidationException(
                    $"matrix cell '{cell.Name}': {parseError}");
            }

            var compatible = knownAgents.Any(agent => AgentCompatibilityMatcher.Match(
                cell.AgentExpression, agent.Name, agent.Parameters).Compatible);
            if (!compatible)
            {
                throw new MatrixBuildValidationException(
                    $"matrix cell '{cell.Name}' has no compatible registered agent");
            }

            foreach (var payload in cell.Assignment.Payload)
            {
                if (!BlobStore.IsSha256(payload.Sha256) ||
                    payload.Sha256.Any(character => character is >= 'A' and <= 'F'))
                {
                    throw new MatrixBuildValidationException(
                        $"matrix cell '{cell.Name}' has malformed payload sha256 '{payload.Sha256}'");
                }

                if (!blobs.Contains(payload.Sha256))
                {
                    throw new MatrixBuildValidationException(
                        $"matrix cell '{cell.Name}' references missing payload '{payload.Sha256}'");
                }
            }
        }
    }

    private static SubmitBuildRequest Normalize(SubmitBuildRequest request)
    {
        var normalized = new SubmitBuildRequest
        {
            RequestId = request.RequestId.Trim(),
            Project = request.Project.Trim(),
            Configuration = request.Configuration.Trim(),
            DefinitionSnapshot = request.DefinitionSnapshot,
        };
        foreach (var source in request.Cells)
        {
            var target = new MatrixBuildCell
            {
                Name = source.Name.Trim(),
                AgentExpression = source.AgentExpression.Trim(),
                Rid = source.Rid.Trim(),
                QueueTimeoutSec = source.QueueTimeoutSec,
            };
            if (source.Assignment != null)
            {
                target.Assignment = source.Assignment.Clone();
            }

            normalized.Cells.Add(target);
        }

        return normalized;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
