using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Vivarium.Controller.Auditing;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Identity;
using Vivarium.Controller.Persistence;
using Vivarium.Controller.Rest.Common;
using Vivarium.Controller.Security;
using Vivarium.Definitions;

namespace Vivarium.Controller.Builds.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeRunInputRequest(string ConfigurationId, Dictionary<string, Dictionary<string, string>> Environments);
internal sealed record RuntimeRunInputView(string Reference, string ConfigurationId, Guid DefinitionRevisionId,
    DateTimeOffset ExpiresAt, string State, long? AcceptedBuildId);

/// <summary>Native, one-use secret execution input for clients that only transport ordinary parameters.</summary>
internal sealed class RuntimeBuildRunInputs(IDbContextFactory<VivariumDbContext> databases, IConfiguration configuration,
    RuntimeBuildSecrets secrets, TimeProvider time, ILogger<RuntimeBuildRunInputs> logger)
{
    internal const string Parameter = "vivarium.run-input";
    private static readonly string[] Permissions = ["run_build", "customize_build_parameters"];

    internal static void Validate(Dictionary<string, Dictionary<string, string>> inputs)
    {
        if (inputs.Count is < 1 or > 16) throw new InvalidDataException("Run input target count is invalid.");
        foreach (var (target, environment) in inputs)
        {
            DefinitionValidation.Identity(target);
            if (environment is null || environment.Count == 0) throw new InvalidDataException("Run input environment is empty.");
            RuntimeBuildSecrets.Validate(environment);
        }
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(inputs)) > 256 * 1024)
            throw new InvalidDataException("Run input exceeds its total bound.");
    }

    internal static Guid? Extract(Dictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue(Parameter, out var reference) || !reference.StartsWith("vri_", StringComparison.Ordinal)) return null;
        if (!Guid.TryParseExact(reference.AsSpan(4), "N", out var id)) throw Invalid();
        parameters.Remove(Parameter);
        return id;
    }

    internal async Task<RuntimeRunInputView> CreateAsync(ManagementRequestContext actor, RuntimeRunInputRequest request, CancellationToken token)
    {
        try
        {
            DefinitionValidation.Identity(request.ConfigurationId);
            if (request.Environments is null) throw new InvalidDataException();
            Validate(request.Environments);
        }
        catch (InvalidDataException) { throw Invalid(); }
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await RuntimeBuildAccess.FencePermissionsAsync(database, token);
        await RuntimeBuildAccess.FenceQueueAsync(database, token);
        var queries = new CatalogQueries(database, configuration, new CurrentAccess(database, configuration));
        async Task<Exception> RejectAsync(Exception error, AuditOutcome outcome)
        {
            new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), "build.run-input.create",
                "build-configuration", request.ConfigurationId, outcome));
            await database.SaveChangesAsync(token); await transaction.CommitAsync(token); return error;
        }
        var snapshot = await queries.ActiveAsync(token) ?? throw new RestApiException(503, "catalogue_unavailable", "Catalogue unavailable", "Activate a valid catalogue first.");
        var project = snapshot.Definition.Projects.SingleOrDefault(p => p.Definition.Configurations.Any(c => c.Id == request.ConfigurationId));
        if (project is null || !await queries.VisibleAsync(actor, snapshot, project.Control.Id, token))
            throw await RejectAsync(Missing(), AuditOutcome.Denied);
        if (!await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, Permissions, snapshot.Ancestry(project.Control.Id), token))
            throw await RejectAsync(new AccessPermissionException(), AuditOutcome.Denied);
        // Validate membership against this exact desired graph, without admitting Builds or allocating counters.
        var configurations = project.Definition.Configurations.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var graph = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!graph.Add(id)) return;
            if (graph.Count > 128 || !configurations.TryGetValue(id, out var member)) throw Invalid();
            foreach (var dependency in member.Snapshots) Visit(dependency.Configuration);
        }
        try
        {
            Visit(request.ConfigurationId);
            if (request.Environments.Keys.Any(k => !graph.Contains(k))) throw Invalid();
        }
        catch (RestApiException error) { throw await RejectAsync(error, AuditOutcome.Failed); }
        var actorId = Guid.Parse(actor.Principal.ActorId);
        var now = time.GetUtcNow();
        if (await database.RuntimeBuildRunInputs.CountAsync(i => i.ActorId == actorId && i.State == "PENDING" && i.ExpiresAt > now, token) >= 16)
            throw await RejectAsync(new RestApiException(429, "run_input_limit", "Run input limit reached", "Consume an existing input or wait for expiry."), AuditOutcome.Failed);
        var input = new RuntimeBuildRunInput { Id = Guid.NewGuid(), ActorId = actorId,
            ProjectId = snapshot.ProjectIds[project.Control.Id], BuildTypeId = snapshot.BuildTypeIds[request.ConfigurationId],
            RevisionId = snapshot.Revision.Id, CreatedAt = now, ExpiresAt = now.AddMinutes(15) };
        input.ProtectedInputs = secrets.ProtectRunInput(input.Id, request.Environments);
        database.RuntimeBuildRunInputs.Add(input);
        Audit(database, actor, input, "build.run-input.create", snapshot.Revision.ControlCommit);
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return View(input, request.ConfigurationId, now);
    }

    internal async Task<RuntimeRunInputView?> GetAsync(ManagementRequestContext actor, Guid id, CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var actorId = Guid.Parse(actor.Principal.ActorId);
        var input = await database.RuntimeBuildRunInputs.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.ActorId == actorId, token);
        if (input is null) return null;
        var ancestry = await RuntimeBuildAccess.AncestryAsync(database, input.ProjectId, token);
        if (!await RuntimeBuildAccess.AllowsAsync(database, configuration, actor, ["view_project", .. Permissions], ancestry, token))
            throw new AccessPermissionException();
        var identity = await database.CatalogBuildTypes.Where(t => t.Id == input.BuildTypeId).Select(t => t.ExternalId).SingleAsync(token);
        return View(input, identity, time.GetUtcNow());
    }

    // The caller already holds both fences and the admission transaction. Consumption commits with the entire graph.
    internal async Task<(RuntimeBuildRunInput Input, Dictionary<string, Dictionary<string, string>> Environments)> ReadForAdmissionAsync(
        VivariumDbContext database, ManagementRequestContext actor, Guid id, long type, Guid revision, CancellationToken token)
    {
        var actorId = Guid.Parse(actor.Principal.ActorId);
        var input = await database.RuntimeBuildRunInputs.SingleOrDefaultAsync(i => i.Id == id && i.ActorId == actorId && i.BuildTypeId == type, token)
            ?? throw Missing();
        if (input.State == "CONSUMED") throw new RestApiException(409, "run_input_consumed", "Run input already consumed", "Read its metadata to recover the accepted Build identity.");
        if (input.State == "EXPIRED" || input.ExpiresAt <= time.GetUtcNow())
            throw new RestApiException(410, "run_input_expired", "Run input expired", "Prepare new temporary execution input.");
        if (input.RevisionId != revision) throw new RestApiException(409, "run_input_revision_changed", "Catalogue revision changed", "Prepare input against the current catalogue.");
        return (input, secrets.ReadRunInput(input.Id, input.ProtectedInputs));
    }

    internal async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await ExpireAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is System.Data.Common.DbException or DbUpdateException)
            { logger.LogWarning("Temporary run input expiry is unavailable; encrypted input remains bounded and unusable after expiry."); }
            await Task.Delay(TimeSpan.FromMinutes(1), time, token);
        }
    }

    internal async Task ExpireAsync(CancellationToken token)
    {
        await using var database = await databases.CreateDbContextAsync(token);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await RuntimeBuildAccess.FenceQueueAsync(database, token);
        var now = time.GetUtcNow();
        var expired = await database.RuntimeBuildRunInputs.Where(i => i.State == "PENDING" && i.ExpiresAt <= now)
            .OrderBy(i => i.ExpiresAt).Take(64).ToArrayAsync(token);
        foreach (var input in expired)
        {
            input.State = "EXPIRED"; input.ProtectedInputs = [];
            Audit(database, ManagementRequestContext.System("build-run-inputs"), input, "build.run-input.expire");
        }
        await database.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }

    internal void Consume(VivariumDbContext database, ManagementRequestContext actor, RuntimeBuildRunInput input, long buildId, string revision)
    {
        input.State = "CONSUMED"; input.AcceptedBuildId = buildId; input.ProtectedInputs = [];
        Audit(database, actor, input, "build.run-input.consume", revision);
    }

    private static RuntimeRunInputView View(RuntimeBuildRunInput input, string configurationId, DateTimeOffset now) =>
        new("vri_" + input.Id.ToString("N"), configurationId, input.RevisionId, input.ExpiresAt,
            (input.State == "PENDING" && input.ExpiresAt <= now ? "EXPIRED" : input.State).ToLowerInvariant(), input.AcceptedBuildId);
    private void Audit(VivariumDbContext database, ManagementRequestContext actor, RuntimeBuildRunInput input, string action, string? revision = null) =>
        new PostgresAuditStore(database).Stage(AuditEventDraft.Create(actor, time.GetUtcNow(), action, "build-run-input", input.Id.ToString("N"), AuditOutcome.Succeeded)
            with { ResultRevision = revision });
    private static RestApiException Invalid() => new(400, "invalid_run_input", "Invalid run input", "Supply bounded per-configuration secret environments for the selected dependency graph.");
    private static RestApiException Missing() => new(404, "run_input_not_found", "Run input not found", "No input is available for this actor and configuration.");
}
