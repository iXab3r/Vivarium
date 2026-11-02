using Microsoft.AspNetCore.Identity;

namespace Vivarium.Controller.Identity;

/// <summary>One framework account; the numeric TeamCity ID is a facet, never a JWT subject.</summary>
public sealed class VivariumUser : IdentityUser<Guid>
{
    public long TeamCityId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? DisplayName { get; set; }
}
