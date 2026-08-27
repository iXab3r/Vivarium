namespace Vivarium.Controller;

public sealed class ControllerOptions
{
    public required string DataDir { get; init; }

    /// <summary>Listen address; loopback in tests, Any for a real deployment.</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>0 = dynamic port (tests).</summary>
    public int Port { get; init; } = 8443;
}
