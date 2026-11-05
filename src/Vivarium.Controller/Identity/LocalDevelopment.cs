namespace Vivarium.Controller.Identity;

/// <summary>Explicit local tooling opt-in; never changes the published default.</summary>
internal static class LocalDevelopment
{
    internal static string? Root(IConfiguration configuration)
    {
        var root = Environment.GetEnvironmentVariable("VIVARIUM_LOCAL_DEV_ROOT");
        if (root is null) return null;
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development"
            || !Path.IsPathFullyQualified(root)
            || !File.Exists(Path.Combine(root, "owner.json")))
            throw new InvalidOperationException("Local development requires an owned absolute root and Development environment.");
        foreach (var key in new[] { "Vivarium:PublicOrigin", "Kestrel:Endpoints:Public:Url", "Kestrel:Endpoints:Storage:Url" })
            if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1"
                || (key != "Kestrel:Endpoints:Storage:Url" && uri.Scheme != "https"))
                throw new InvalidOperationException("Local development endpoints must use IPv4 loopback.");
        return root;
    }
}
