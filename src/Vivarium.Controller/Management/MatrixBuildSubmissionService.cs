using System.Security.Cryptography;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Blobs;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Builds;
using Vivarium.Controller.Scheduling;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Management;

public sealed class MatrixBuildValidationException(string message) : Exception(message);

internal sealed record MatrixBuildSubmissionIdentity(
    string OperationKind,
    string RequestHash,
    string? StagingId)
{
    public const string LegacyOperationKind = "legacy-control-plane";
}

/// <summary>Validates a complete matrix before handing one atomic write to <see cref="MatrixBuildStore"/>.</summary>
public sealed class MatrixBuildSubmissionService
{
    private readonly MatrixBuildStore store;
    private readonly AgentStore agents;
    private readonly BlobStore blobs;
    private readonly BuildQueueService queue;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan defaultQueueWaitTimeout;
    private readonly ManagementCommandAuthorizer? authorization;

    public MatrixBuildSubmissionService(
        MatrixBuildStore store,
        AgentStore agents,
        BlobStore blobs,
        BuildQueueService queue,
        TimeProvider timeProvider,
        TimeSpan? defaultQueueWaitTimeout = null,
        ManagementCommandAuthorizer? authorization = null)
    {
        this.store = store;
        this.agents = agents;
        this.blobs = blobs;
        this.queue = queue;
        this.timeProvider = timeProvider;
        this.authorization = authorization;
        this.defaultQueueWaitTimeout =
            defaultQueueWaitTimeout ?? ControllerOptions.DefaultBuildQueueWaitTimeout;
        if (this.defaultQueueWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultQueueWaitTimeout), "queue wait timeout must be positive");
        }
    }

    public async Task<BuildRef> SubmitAsync(
        ManagementRequestContext context,
        SubmitBuildRequest request) => await SubmitAsync(
            context,
            request,
            identity: null,
            attachmentParticipant: null);

    internal async Task<BuildRef> SubmitAsync(
        ManagementRequestContext context,
        SubmitBuildRequest request,
        MatrixBuildSubmissionIdentity? identity,
        IBlobBuildAttachmentParticipant? attachmentParticipant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context = context.WithRequestId(request.RequestId);
        await (authorization ?? throw new InvalidOperationException(
                "application command authorization is not configured"))
            .DemandAsync(
                context,
                ManagementPermission.BuildSubmit,
                "matrix-build.submit",
                "project",
                request.Project);
        var canonical = Normalize(request);
        var operationKind = identity?.OperationKind ?? MatrixBuildSubmissionIdentity.LegacyOperationKind;
        var requestHash = identity?.RequestHash ?? Hash(canonical.ToByteArray());
        ValidateSubmissionIdentity(operationKind, requestHash, identity?.StagingId);
        var existing = await store.FindIdempotentAsync(
            context.Principal, canonical.RequestId, operationKind, requestHash);
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
            existing = await store.FindIdempotentAsync(
                context.Principal, canonical.RequestId, operationKind, requestHash);
            if (existing != null)
            {
                queue.NotifyChanged();
                return existing;
            }

            throw;
        }

        var definitionHash = Hash(canonical.DefinitionSnapshot.Span);
        var now = timeProvider.GetUtcNow();
        var build = await store.SubmitScopedAsync(
            context.Principal,
            canonical,
            operationKind,
            requestHash,
            definitionHash,
            now,
            defaultQueueWaitTimeout,
            identity?.StagingId,
            attachmentParticipant,
            context,
            matrixBuildId => AuditEventDraft.Create(
                context,
                now,
                "matrix-build.submit",
                "matrix-build",
                matrixBuildId));
        queue.NotifyChanged();
        return build;
    }

    private static void ValidateSubmissionIdentity(
        string operationKind,
        string requestHash,
        string? stagingId)
    {
        if (string.IsNullOrWhiteSpace(operationKind) || operationKind.Length > 256 ||
            operationKind.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new MatrixBuildValidationException(
                "submission operation kind must contain 1-256 safe characters");
        }

        if (requestHash.Length != 64 || requestHash.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new MatrixBuildValidationException(
                "submission request hash must be a lowercase SHA-256 value");
        }

        if (stagingId is not null && (string.IsNullOrWhiteSpace(stagingId) ||
                stagingId.Length > 256 || stagingId.Any(character => character is '\r' or '\n' or '\0')))
        {
            throw new MatrixBuildValidationException(
                "blob staging ID must contain 1-256 safe characters");
        }

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
                target.Assignment = NormalizeAssignment(source.Assignment);
            }

            normalized.Cells.Add(target);
        }

        return normalized;
    }

    private static BuildAssignment NormalizeAssignment(BuildAssignment source)
    {
        var normalized = source.Clone();
        normalized.Parameters.Clear();
        foreach (var pair in source.Parameters.OrderBy(
                     pair => pair.Key, StringComparer.Ordinal))
        {
            normalized.Parameters.Add(pair.Key, pair.Value);
        }
        for (var index = 0; index < normalized.Steps.Count; index++)
        {
            var sourceStep = source.Steps[index];
            var normalizedStep = normalized.Steps[index];
            normalizedStep.Env.Clear();
            foreach (var pair in sourceStep.Env.OrderBy(
                         pair => pair.Key, StringComparer.Ordinal))
            {
                normalizedStep.Env.Add(pair.Key, pair.Value);
            }
        }

        return normalized;
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
