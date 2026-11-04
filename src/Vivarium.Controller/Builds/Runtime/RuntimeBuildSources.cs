using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vivarium.Contracts;
using Vivarium.Contracts.V1;
using Vivarium.Controller.Agents;
using Vivarium.Controller.Catalog;
using Vivarium.Controller.Configuration.Git;
using Vivarium.Controller.Persistence;
using Vivarium.Definitions;

namespace Vivarium.Controller.Builds.Runtime;

internal sealed class RuntimeSourceCollectionException() : IOException("Workload source collection failed.");

/// <summary>Collect workload revisions without evaluating author code or inheriting Host Git credentials.</summary>
internal sealed class RuntimeBuildSources(IDbContextFactory<VivariumDbContext> databases, TimeProvider time)
{
    internal static void ValidateReceipt(VcsCheckoutReceipt receipt, BuildAssignment assignment)
    {
        if (assignment.VcsCheckout is null || (receipt.Succeeded
            ? receipt.Revision != assignment.VcsCheckout.Revision || receipt.FailureCode.Length != 0
            : receipt.Revision.Length != 0 || receipt.FailureCode is not ("git_unavailable" or "git_init_failed" or "fetch_failed" or "checkout_failed" or "checkout_denied" or "revision_mismatch")))
            throw new InvalidDataException("VCS checkout evidence differs from the assignment.");
    }

    internal static async Task<bool> ObserveAsync(VivariumDbContext database, long id, VcsCheckoutReceipt receipt,
        BuildAssignment assignment, DateTimeOffset now, CancellationToken token)
    {
        ValidateReceipt(receipt, assignment);
        var bytes = Google.Protobuf.MessageExtensions.ToByteArray(receipt);
        var prior = await database.RuntimeBuildSourceReceipts.SingleOrDefaultAsync(r => r.BuildId == id, token);
        if (prior is not null) return prior.Evidence.AsSpan().SequenceEqual(bytes);
        database.RuntimeBuildSourceReceipts.Add(new() { BuildId = id, Evidence = bytes, ReceivedAt = now });
        return true;
    }

    internal static VcsCheckout Profile(Source source)
    {
        var result = new VcsCheckout { Url = source.Url, Branch = source.Branch };
        var rules = source.CheckoutRules.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rules.Length == 0 || rules[0] != "+:." || rules.Skip(1).Any(r => !r.StartsWith("-:", StringComparison.Ordinal)))
            throw new InvalidDataException("This checkout profile requires a root include and literal top-level exclusions.");
        result.ExcludedDirectories.AddRange(rules.Skip(1).Select(r => r[2..]));
        VcsCheckoutProfile.Validate(result, requireRevision: false);
        return result;
    }

    internal static async Task<long> InstanceAsync(VivariumDbContext database, long projectId, Source source, CancellationToken token)
    {
        var properties = JsonSerializer.Serialize(new { source.Url, source.Branch }, DefinitionValidation.Json);
        var hash = IsolatedDefinitionEvaluator.Hash(properties);
        var existing = await database.RuntimeVcsRootInstances.SingleOrDefaultAsync(r => r.ProjectId == projectId
            && r.RootId == source.RootId && r.PropertiesHash == hash, token);
        if (existing is not null) return existing.Id;
        var instance = new RuntimeVcsRootInstance { ProjectId = projectId, RootId = source.RootId, PropertiesHash = hash, PropertiesJson = properties };
        database.RuntimeVcsRootInstances.Add(instance); await database.SaveChangesAsync(token);
        return instance.Id;
    }

    internal async Task<VcsCheckout?> SelectAsync(RuntimeBuildInput input, RuntimeBuildExecution execution, CancellationToken token)
    {
        if (input.SourceJson is null) return null;
        var source = JsonSerializer.Deserialize<Source>(input.SourceJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("The collected workload source is invalid.");
        var result = Profile(source);
        await using var database = await databases.CreateDbContextAsync(token);
        var selected = await database.RuntimeBuildSourceSelections.AsNoTracking().SingleOrDefaultAsync(s => s.BuildId == input.BuildId, token);
        if (selected is null)
        {
            var version = await ResolveVersionAsync(database, input, source, token);
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await AgentCredentials.LockAgentAsync(database, execution.AgentId, token);
            var owner = await database.RuntimeBuildExecutions.SingleAsync(e => e.BuildId == input.BuildId, token);
            if (owner.WorkloadId != execution.WorkloadId || owner.ReleasedAt is not null) throw new IOException("Source collection ownership expired.");
            selected = await database.RuntimeBuildSourceSelections.SingleOrDefaultAsync(s => s.BuildId == input.BuildId, token);
            if (selected is null)
            {
                selected = new() { BuildId = input.BuildId, Version = version, SelectedAt = time.GetUtcNow() };
                database.RuntimeBuildSourceSelections.Add(selected);
                var build = await database.RuntimeBuilds.SingleAsync(b => b.Id == input.BuildId, token);
                await RuntimeBuildAudit.StageAsync(database, build, time.GetUtcNow(), "build.source.collected", details:
                    new Dictionary<string, string> { ["rootInstanceId"] = input.VcsRootInstanceId!.Value.ToString(CultureInfo.InvariantCulture), ["revision"] = version }, token: token);
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        if (selected.Version == "N/A") throw new RuntimeSourceCollectionException();
        result.Revision = selected.Version;
        return result;
    }

    // Anonymous source discovery owns no Agent, counter or execution. PG selection is the durable fact.
    internal async Task CollectQueuedAsync(RuntimeBuildInput input, CancellationToken token)
    {
        if (input.SourceJson is null) return;
        var source = JsonSerializer.Deserialize<Source>(input.SourceJson, DefinitionValidation.Json)
            ?? throw new InvalidDataException("The collected workload source is invalid.");
        Profile(source);
        await using var database = await databases.CreateDbContextAsync(token);
        if (await database.RuntimeBuildSourceSelections.AnyAsync(s => s.BuildId == input.BuildId, token)) return;
        string version;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), time))
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
        {
            try { version = await ResolveVersionAsync(database, input, source, deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { version = "N/A"; }
        }
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RuntimeBuildAccess.FenceQueueAsync(database, token);
        var build = await database.RuntimeBuilds.SingleAsync(b => b.Id == input.BuildId, token);
        if (build.State != "QUEUED" || await database.RuntimeBuildExecutions.AnyAsync(e => e.BuildId == input.BuildId, token)
            || await database.RuntimeBuildResolutions.AnyAsync(r => r.RequestedBuildId == input.BuildId, token)) return;
        var current = await UpstreamVersionsAsync(database, input, token);
        if (current.Length > 1 || current.Length == 1 && current[0] != version) return;
        if (!await database.RuntimeBuildSourceSelections.AnyAsync(s => s.BuildId == input.BuildId, token))
        {
            database.RuntimeBuildSourceSelections.Add(new() { BuildId = input.BuildId, Version = version, SelectedAt = time.GetUtcNow() });
            await RuntimeBuildAudit.StageAsync(database, build, time.GetUtcNow(), "build.source.collected", details:
                new Dictionary<string, string> { ["rootInstanceId"] = input.VcsRootInstanceId!.Value.ToString(CultureInfo.InvariantCulture), ["revision"] = version }, token: token);
            await database.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private static Task<string[]> UpstreamVersionsAsync(VivariumDbContext database, RuntimeBuildInput input, CancellationToken token)
    {
        // Explicit finished selections retain their own revision; they do not pin this new checkout.
        // Keep this immutable request fact with admission so restart uses the same processing.
        using var admission = JsonDocument.Parse(input.AdmissionJson);
        var retained = admission.RootElement.TryGetProperty("explicitSnapshotBuildIds", out var ids)
            ? ids.EnumerateArray().Select(id => id.GetInt64()).ToArray() : [];
        return (from dependency in RuntimeBuildService.DependencyHandles(database, input.BuildId)
         join producerInput in database.RuntimeBuildInputs on dependency.Id equals producerInput.BuildId
         join selection in database.RuntimeBuildSourceSelections on dependency.Id equals selection.BuildId
         where dependency.Kind == "SNAPSHOT" && producerInput.VcsRootInstanceId == input.VcsRootInstanceId && !retained.Contains(dependency.Id)
         select selection.Version).Distinct().ToArrayAsync(token);
    }

    private static async Task<string> ResolveVersionAsync(VivariumDbContext database, RuntimeBuildInput input, Source source, CancellationToken token)
    {
        var upstream = await UpstreamVersionsAsync(database, input, token);
        if (upstream.Length > 1 || upstream.Contains("N/A")) throw new InvalidDataException("Snapshot source revisions conflict.");
        string version;
        if (upstream.Length == 1) version = upstream[0];
        else
        {
            var directory = Directory.CreateTempSubdirectory("vivarium-source-resolution-");
            try
            {
                // Empty working directory plus a discovery ceiling excludes local repository configuration.
                var environment = DefinitionGitReader.GitEnvironment();
                foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase))) environment[key] = null;
                environment["GIT_CEILING_DIRECTORIES"] = directory.Parent!.FullName;
                foreach (var key in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME", "CURL_HOME" }) environment[key] = directory.FullName;
                environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GCM_INTERACTIVE"] = "never";
                environment["GIT_ALLOW_PROTOCOL"] = "https:http:git";
                var output = await new GitCommandRunner(directory.FullName).RunAsync([.. VcsCheckoutProfile.ConfigurationArguments,
                    "ls-remote", "--exit-code", "--refs", "--", source.Url, source.Branch], environment, cancellationToken: token);
                var fields = output.Split('\t');
                if (fields.Length != 2 || fields[1] != source.Branch) throw new InvalidDataException("Git source resolution differs.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(fields[0], "^[a-f0-9]{40}$")) throw new InvalidDataException("Git source identity is invalid.");
                version = fields[0];
            }
            catch (Exception error) when (error is ConfigurationRepositoryException or InvalidDataException)
            { version = "N/A"; }
            finally { directory.Delete(); }
        }
        return version;
    }

}
