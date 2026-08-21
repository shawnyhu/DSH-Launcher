using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DshLauncher.Services;

internal static class PortService
{
    public static bool IsListening(int port) => IPGlobalProperties.GetIPGlobalProperties()
        .GetActiveTcpListeners()
        .Any(endpoint => endpoint.Port == port &&
            (IPAddress.IsLoopback(endpoint.Address) ||
             endpoint.Address.Equals(IPAddress.Any) ||
             endpoint.Address.Equals(IPAddress.IPv6Any)));

    public static int FindAvailablePort(int preferred)
    {
        if (!IsListening(preferred))
        {
            return preferred;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
