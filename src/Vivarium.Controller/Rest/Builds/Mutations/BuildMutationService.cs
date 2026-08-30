using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Blobs.Access;
using Vivarium.Controller.Management;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;

namespace Vivarium.Controller.Rest.Builds.Mutations;

internal sealed class BuildMutationService(
    MatrixBuildSubmissionService submissions,
    MatrixBuildCancellationService cancellations,
    MatrixBuildStore matrixBuilds,
    BuildRestProjection projection,
    IBlobBuildAttachmentParticipant payloadAttachments,
    ManagementCommandAuthorizer authorization)
{
    internal const string SubmissionOperationKind = "rest:POST:/api/v1/builds";

    public async Task<BuildSubmissionResponse> SubmitAsync(
        ManagementRequestContext context,
        BuildSubmissionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await authorization.DemandAsync(
            context.WithRequestId(idempotencyKey),
            ManagementPermission.BuildSubmit,
            "matrix-build.submit",
            "project",
            string.IsNullOrWhiteSpace(request.Project) ? "(unspecified)" : request.Project);

        var canonical = Map(request, idempotencyKey);
        var requestHash = EffectiveRequestHash(canonical, request.BlobStagingId!);
        var receipt = await matrixBuilds.FindResponseReceiptAsync(
            context.Principal,
            SubmissionOperationKind,
            idempotencyKey,
            requestHash);
        if (receipt is not null)
        {
            return FromReceipt(receipt);
        }

        var build = await submissions.SubmitAsync(
            context,
            canonical,
            new MatrixBuildSubmissionIdentity(
                SubmissionOperationKind,
                requestHash,
                request.BlobStagingId),
            payloadAttachments);
        cancellationToken.ThrowIfCancellationRequested();
        var resource = await projection.GetBuildAsync(build.BuildId)
            ?? throw new InvalidDataException(
                $"matrix Build '{build.BuildId}' disappeared after durable submission");
        var json = JsonSerializer.Serialize(resource, RestJson.SerializerOptions);
        var etag = RestEtags.FromRevision($"{resource.Id}\n{resource.RuntimeRevision}");
        receipt = await matrixBuilds.FinalizeResponseReceiptAsync(
            context.Principal,
            SubmissionOperationKind,
            idempotencyKey,
            requestHash,
            new MatrixBuildResponseReceipt(StatusCodes.Status201Created, json, etag));
        return FromReceipt(receipt);
    }

    public async Task<BuildResource?> CancelAsync(
        ManagementRequestContext context,
        string matrixBuildId,
        string reason,
        BuildStopMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await cancellations.CancelAsync(context, matrixBuildId, reason, mode);
        if (snapshot is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await projection.GetBuildAsync(matrixBuildId)
            ?? throw new InvalidDataException(
                $"matrix Build '{matrixBuildId}' disappeared after durable cancellation");
    }

    private static BuildSubmissionResponse FromReceipt(MatrixBuildResponseReceipt receipt)
    {
        var resource = JsonSerializer.Deserialize<BuildResource>(
            receipt.Json,
            RestJson.SerializerOptions) ?? throw new InvalidDataException(
                "the durable Build idempotency response is malformed");
        return new BuildSubmissionResponse(
            receipt.Status,
            receipt.Json,
            receipt.ETag,
            resource.Url);
    }

    private static SubmitBuildRequest Map(BuildSubmissionRequest request, string idempotencyKey)
    {
        var project = Required(request.Project, 256, "project");
        var configuration = Required(request.Configuration, 256, "configuration");
        var stagingId = Required(request.BlobStagingId, 256, "blobStagingId");
        _ = stagingId;
        var definition = request.DefinitionSnapshot;
        if (definition is null || definition.Length is < 1 or > 1_048_576)
        {
            throw Invalid(
                "definitionSnapshot",
                "definitionSnapshot must contain 1-1048576 bytes of resolved configuration provenance");
        }

        if (request.Cells is null || request.Cells.Count is < 1 or > 256)
        {
            throw Invalid("cells", "cells must contain 1-256 matrix entries");
        }

        var result = new SubmitBuildRequest
        {
            RequestId = idempotencyKey,
            Project = project,
            Configuration = configuration,
            DefinitionSnapshot = ByteString.CopyFrom(definition),
        };
        foreach (var (cell, cellIndex) in request.Cells.Select((value, index) => (value, index)))
        {
            if (cell is null)
            {
                throw Invalid($"cells[{cellIndex}]", "matrix cells cannot be null");
            }

            if (cell.QueueTimeoutSeconds is < 0 or > 604_800)
            {
                throw Invalid(
                    $"cells[{cellIndex}].queueTimeoutSeconds",
                    "queueTimeoutSeconds must be between 0 and 604800");
            }

            result.Cells.Add(new MatrixBuildCell
            {
                Name = Required(cell.Name, 256, $"cells[{cellIndex}].name"),
                AgentExpression = Optional(
                    cell.AgentExpression, 2048, $"cells[{cellIndex}].agentExpression"),
                Rid = Optional(cell.Rid, 256, $"cells[{cellIndex}].rid"),
                QueueTimeoutSec = cell.QueueTimeoutSeconds,
                Assignment = MapAssignment(cell.Assignment, cellIndex),
            });
        }

        return result;
    }

    private static BuildAssignment MapAssignment(
        BuildSubmissionAssignmentRequest? assignment,
        int cellIndex)
    {
        if (assignment is null)
        {
            throw Invalid($"cells[{cellIndex}].assignment", "assignment is required");
        }

        if (assignment.Payload is null || assignment.Payload.Count > 256 ||
            assignment.Steps is null || assignment.Steps.Count > 256 ||
            assignment.Collect is null || assignment.Collect.Count > 256 ||
            assignment.Parameters is null || assignment.Parameters.Count > 256)
        {
            throw Invalid(
                $"cells[{cellIndex}].assignment",
                "payload, steps, collect, and parameters are required and each is limited to 256 entries");
        }

        var result = new BuildAssignment { OnFail = ParseOnFail(assignment.OnFail, cellIndex) };
        foreach (var (payload, index) in assignment.Payload.Select((value, index) => (value, index)))
        {
            if (payload is null)
            {
                throw Invalid(
                    $"cells[{cellIndex}].assignment.payload[{index}]",
                    "payload entries cannot be null");
            }

            var sha256 = Required(
                payload.Sha256, 64, $"cells[{cellIndex}].assignment.payload[{index}].sha256");
            if (sha256.Length != 64 || sha256.Any(character =>
                    !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw Invalid(
                    $"cells[{cellIndex}].assignment.payload[{index}].sha256",
                    "sha256 must be 64 lowercase hexadecimal characters");
            }

            var fileName = RelativePath(
                payload.FileName,
                allowEmpty: false,
                $"cells[{cellIndex}].assignment.payload[{index}].fileName");
            var unpackTo = RelativePath(
                payload.UnpackTo,
                allowEmpty: true,
                $"cells[{cellIndex}].assignment.payload[{index}].unpackTo");
            result.Payload.Add(new Blob
            {
                Sha256 = sha256,
                FileName = fileName,
                Archive = payload.Archive,
                UnpackTo = unpackTo,
            });
        }

        foreach (var (step, index) in assignment.Steps.Select((value, index) => (value, index)))
        {
            if (step is null || step.Args is null || step.Args.Count > 256 ||
                step.Env is null || step.Env.Count > 256)
            {
                throw Invalid(
                    $"cells[{cellIndex}].assignment.steps[{index}]",
                    "step, args, and env are required and collections are limited to 256 entries");
            }

            if (step.TimeoutSeconds is < 0 or > 604_800)
            {
                throw Invalid(
                    $"cells[{cellIndex}].assignment.steps[{index}].timeoutSeconds",
                    "timeoutSeconds must be between 0 and 604800");
            }

            var mapped = new Step
            {
                Program = Required(
                    step.Program, 1024, $"cells[{cellIndex}].assignment.steps[{index}].program"),
                Cwd = RelativePath(
                    step.Cwd,
                    allowEmpty: true,
                    $"cells[{cellIndex}].assignment.steps[{index}].cwd"),
                TimeoutSec = step.TimeoutSeconds,
                Policy = ParsePolicy(step.Policy, cellIndex, index),
                ExpectedReboot = step.ExpectedReboot,
            };
            mapped.Args.Add(step.Args.Select((argument, argumentIndex) => Required(
                argument,
                4096,
                $"cells[{cellIndex}].assignment.steps[{index}].args[{argumentIndex}]")));
            foreach (var pair in Ordered(step.Env))
            {
                mapped.Env.Add(
                    Required(
                        pair.Key, 256, $"cells[{cellIndex}].assignment.steps[{index}].env key"),
                    Optional(
                        pair.Value, 4096, $"cells[{cellIndex}].assignment.steps[{index}].env value"));
            }

            result.Steps.Add(mapped);
        }

        result.Collect.Add(assignment.Collect.Select((pattern, index) => SafePattern(
            pattern,
            $"cells[{cellIndex}].assignment.collect[{index}]")));
        foreach (var pair in Ordered(assignment.Parameters))
        {
            result.Parameters.Add(
                Required(pair.Key, 256, $"cells[{cellIndex}].assignment.parameters key"),
                Optional(pair.Value, 4096, $"cells[{cellIndex}].assignment.parameters value"));
        }

        return result;
    }

    private static OnFail ParseOnFail(string? value, int cellIndex) => value switch
    {
        "none" => OnFail.None,
        "keep-machine" => OnFail.KeepMachine,
        "snapshot-machine" => OnFail.SnapshotMachine,
        _ => throw Invalid(
            $"cells[{cellIndex}].assignment.onFail",
            "onFail must be none, keep-machine, or snapshot-machine"),
    };

    private static StepPolicy ParsePolicy(string? value, int cellIndex, int stepIndex) => value switch
    {
        "default" => StepPolicy.Default,
        "even-if-failed" => StepPolicy.EvenIfFailed,
        "always" => StepPolicy.Always,
        _ => throw Invalid(
            $"cells[{cellIndex}].assignment.steps[{stepIndex}].policy",
            "policy must be default, even-if-failed, or always"),
    };

    private static IEnumerable<KeyValuePair<string, string>> Ordered(
        IReadOnlyDictionary<string, string> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal);

    private static string RelativePath(string? value, bool allowEmpty, string path)
    {
        var result = Optional(value, 1024, path);
        if (result.Length == 0 && allowEmpty)
        {
            return result;
        }

        if (result.Length == 0 || result.StartsWith("/", StringComparison.Ordinal) ||
            result.Contains('\\') || result.Split('/').Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw Invalid(path, "path must be a safe forward-slash relative path");
        }

        return result;
    }

    private static string SafePattern(string? value, string path)
    {
        var result = Required(value, 1024, path);
        if (result.StartsWith("/", StringComparison.Ordinal) || result.Contains('\\') ||
            result.Split('/').Any(segment => segment is ".."))
        {
            throw Invalid(path, "artifact patterns must stay under the Build work directory");
        }

        return result;
    }

    private static string Required(string? value, int maximumLength, string path)
    {
        var normalized = Optional(value, maximumLength, path);
        return normalized.Length == 0
            ? throw Invalid(path, $"{path} is required")
            : normalized;
    }

    private static string Optional(string? value, int maximumLength, string path)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(character =>
                character is '\r' or '\n' or '\0'))
        {
            throw Invalid(path, $"{path} must contain at most {maximumLength} safe characters");
        }

        return normalized;
    }

    private static MatrixBuildValidationException Invalid(string path, string message) =>
        new($"{path}: {message}");

    private static string EffectiveRequestHash(SubmitBuildRequest request, string stagingId)
    {
        var withoutIdentity = request.Clone();
        withoutIdentity.RequestId = string.Empty;
        var canonical = JsonSerializer.Serialize(
            new[] { stagingId, Convert.ToBase64String(withoutIdentity.ToByteArray()) },
            RestJson.SerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
