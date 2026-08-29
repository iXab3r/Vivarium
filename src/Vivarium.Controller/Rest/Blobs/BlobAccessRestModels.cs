namespace Vivarium.Controller.Rest.Blobs;

public sealed record BlobUploadPlanCreateRequest(
    string? ProjectId,
    IReadOnlyList<BlobUploadPlanCreateItem>? Blobs);

public sealed record BlobUploadPlanCreateItem(string? Sha256, long? Size);

public sealed record BlobUploadPlanResource(
    string Id,
    string ProjectId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<BlobUploadPlanItemResource> Items);

public sealed record BlobUploadPlanItemResource(
    string Sha256,
    long Size,
    bool UploadRequired,
    string UploadUrl);
