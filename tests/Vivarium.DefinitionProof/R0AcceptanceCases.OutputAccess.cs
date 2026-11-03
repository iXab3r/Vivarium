using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation;

namespace Vivarium.DefinitionProof;

public sealed partial class R0AcceptanceCases
{
    [Test]
    public async Task Output_requires_only_current_Project_visibility_and_old_links_never_preserve_withdrawn_eligibility()
    {
        var clock = new AcceptanceClock();
        await using var host = await installation.CreateCaseAsync(catalogued: true,
            configureServices: services => services.Configure<OpenIddictValidationOptions>(options => options.TimeProvider = clock));
        using var administrator = await host.AdministratorAsync(); var csrf = await host.AntiforgeryAsync(administrator);
        await host.StartWindowsAgentAsync(administrator, csrf);
        using var actor = await ProjectActorAsync(host, administrator, csrf, "OWNED_VIEW", ["view_project"]);
        var shortKey = actor.BuildClient.DefaultRequestHeaders.Authorization;
        await actor.CreateKeyAsync(lifetimeDays: 3); var revokedKey = actor.BuildClient.DefaultRequestHeaders.Authorization;
        actor.BuildClient.DefaultRequestHeaders.Authorization = shortKey;
        var p = await SubmitAsync(administrator, "P_LocalSmoke", csrf: csrf); await FinishedAsync(administrator, p);
        var q = await SubmitAsync(administrator, "Q_LocalSmoke", csrf: csrf); await FinishedAsync(administrator, q);
        var output = await OutputAsync(actor.BuildClient, p);
        var entry = output.GetProperty("items").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "data");
        var content = entry.GetProperty("content").GetProperty("href").GetString()!;
        var paths = new[] { $"/api/v1/builds/{p}/output", content,
            $"/api/v1/builds/{p}/output?cursor=" + Uri.EscapeDataString(output.GetProperty("output").GetProperty("resumeCursor").GetString()!) };
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        Assert.That(await actor.BuildClient.GetStringAsync(content), Does.Contain("owned-r0-stdout\n"));
        var qOutput = await OutputAsync(administrator, q);
        var qContent = qOutput.GetProperty("items").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "data")
            .GetProperty("content").GetProperty("href").GetString()!;
        var protectedPaths = new[] { $"/api/v1/builds/{q}/output", qContent };
        await ExpectPathsAsync(administrator, protectedPaths, HttpStatusCode.OK);
        await ExpectPathsAsync(actor.BuildClient, protectedPaths, HttpStatusCode.NotFound);
        var role = $"/api/v1/users/{actor.UserId}/roles/OWNED_VIEW/projects/P";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Delete, role, null, HttpStatusCode.NoContent);
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.NotFound);
        Assert.That(await administrator.GetStringAsync(content), Does.Contain("owned-r0-stdout\n"));
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, role, null, HttpStatusCode.NoContent);
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        var enabled = $"/api/v1/users/{actor.UserId}/enabled";
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, enabled, "{\"enabled\":false}", HttpStatusCode.NoContent);
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        await R0AcceptanceInstallation.Case.MutateAsync(administrator, csrf, HttpMethod.Put, enabled, "{\"enabled\":true}", HttpStatusCode.NoContent);
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        actor.BuildClient.DefaultRequestHeaders.Authorization = revokedKey;
        await actor.RevokeOwnKeyAsync(); await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        actor.BuildClient.DefaultRequestHeaders.Authorization = shortKey;
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromDays(2)); await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        actor.BuildClient.DefaultRequestHeaders.Authorization = revokedKey;
        await ExpectPathsAsync(actor.BuildClient, paths, HttpStatusCode.Unauthorized);
        await ExpectPathsAsync(administrator, protectedPaths, HttpStatusCode.OK);
        Assert.That(await administrator.GetStringAsync(content), Does.Contain("owned-r0-stdout\n"));
    }
}
