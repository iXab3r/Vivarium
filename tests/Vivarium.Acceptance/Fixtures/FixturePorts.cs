// Adapted from EyeAuras.Depot 20238384013326cddb3296963a6d101a0970efc0; consumer-owned fixture policy.
using System.Net;
using System.Net.Sockets;

namespace Vivarium.Acceptance.Fixtures;

/// <summary>Choose an ephemeral loopback port for run-owned test processes.</summary>
public static class FixturePorts
{
    /// <summary>Return a currently available port; the real process must still bind it successfully.</summary>
    public static int Loopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
