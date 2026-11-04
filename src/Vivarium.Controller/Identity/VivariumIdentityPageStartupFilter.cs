namespace Vivarium.Controller.Identity;

/// <summary>Map Razor after Chiral startup: page discovery can resolve the stock Data Protection key ring.</summary>
internal sealed class VivariumIdentityPageStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.UseEndpoints(endpoints => endpoints.MapRazorPages());
    };
}
