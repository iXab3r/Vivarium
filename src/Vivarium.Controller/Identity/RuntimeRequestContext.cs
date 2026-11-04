using Vivarium.Controller.Security;
using Vivarium.Controller.Rest.Common;

namespace Vivarium.Controller.Identity;

/// <summary>Framework callers share the existing actor/audit contract and one internal request correlation.</summary>
public sealed class RuntimeRequestContext(IHttpContextAccessor http)
{
    private readonly string localCorrelation = ManagementIdentifiers.NewId();

    public ManagementRequestContext ForUser(Guid userId, string credentialKind, string source) =>
        Create(new ManagementPrincipal("user", userId.ToString(), credentialKind, LegacyScope: null), source);

    public ManagementRequestContext ForSystem(string source) => Create(ManagementPrincipal.System, source);

    private ManagementRequestContext Create(ManagementPrincipal actor, string source)
    {
        var correlation = localCorrelation;
        if (http.HttpContext is { } context)
        {
            correlation = RestCorrelation.Get(context);
            RestCorrelation.Set(context, correlation);
        }
        return new(actor, correlation, RequestId: null, source);
    }
}
