using Vivarium.Definitions;
using System.Text.Json.Serialization;

namespace Vivarium.Controller.Catalog;

public sealed class CatalogChange
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public required string CorrelationId { get; set; }
    public required string CredentialKind { get; set; }
    public string? RequestId { get; set; }
    public string Origin { get; set; } = "MANAGED";
    public string Kind { get; set; } = "RECONCILE";
    public string? TargetAgentId { get; set; }
    public required string RepositoryId { get; set; }
    public required string CandidateCommit { get; set; }
    public required string SelectedRevisionsJson { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public string State { get; set; } = "PENDING";
    public string? FailureCode { get; set; }
    public string? MaterializedJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CatalogEvaluation
{
    public Guid Id { get; set; }
    public Guid ChangeId { get; set; }
    public required string ProjectId { get; set; }
    public required string Phase { get; set; }
    public string State { get; set; } = "RUNNING";
    public required string InputHashesJson { get; set; }
    public string? OutputJson { get; set; }
    public string? OutputHash { get; set; }
    public string? FailureCode { get; set; }
    public bool CleanupConfirmed { get; set; }
}

public sealed class CatalogRevision
{
    public Guid Id { get; set; }
    public Guid ChangeId { get; set; }
    public required string RepositoryId { get; set; }
    public required string ControlCommit { get; set; }
    public required string CompilerImage { get; set; }
    public required string CompilerSdk { get; set; }
    public required string ContractHash { get; set; }
    public required string DefinitionHash { get; set; }
    public required string DefinitionJson { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
}

public sealed class CatalogActive
{
    public required string RepositoryId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid LastKnownGoodRevisionId { get; set; }
}

public sealed record CatalogMember(string RepositoryId, string Branch, string Commit);
public sealed record CatalogProject(Project Control, EvaluatedDefinition Definition, string SettingsRepositoryId,
    string SettingsCommit, string SettingsPath);
public sealed record CatalogDefinition(CatalogMember[] Members, CatalogProject[] Projects,
    string CompilerImage, string CompilerSdk, string ContractHash, string SchemaVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CatalogAgentSetting[]? Agents = null);
public sealed record CatalogAgentSetting(string Id, bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ChangedAt = null);

public sealed record DefinitionRepository(string Id, string Path, string TrustReference);
public sealed record CatalogSources(string RepositoryId, string Path, DefinitionRepository[] SettingsRepositories);

// Mutable indexes of accepted Git data; IDs survive names, revisions and removal from active use.
public sealed class CatalogProjectIdentity
{
    public long Id { get; set; }
    public required string ExternalId { get; set; }
    public long? ParentId { get; set; }
    public string? RepositoryId { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public bool IsActive { get; set; }
    public Guid? ActiveRevisionId { get; set; }
}

public sealed class CatalogBuildType
{
    public long Id { get; set; }
    public required string ExternalId { get; set; }
    public long ProjectId { get; set; }
    public required string Name { get; set; }
    public bool IsTemplate { get; set; }
    public bool IsActive { get; set; }
    public Guid? ActiveRevisionId { get; set; }
}

public sealed class CatalogBuildDefinition
{
    public long BuildTypeId { get; set; }
    public long ProjectId { get; set; }
    public Guid RevisionId { get; set; }
    public required string PlanHash { get; set; }
    public required string PlanJson { get; set; }
    public required string SettingsRepositoryId { get; set; }
    public required string SettingsCommit { get; set; }
    public required string SettingsPath { get; set; }
}
