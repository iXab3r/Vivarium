using System.Reflection;

namespace Vivarium.Contracts;

public static class VivariumProductVersion
{
    public static string FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var productVersion = informationalVersion?.Split('+', 2)[0];
        return string.IsNullOrWhiteSpace(productVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : productVersion;
    }
}
