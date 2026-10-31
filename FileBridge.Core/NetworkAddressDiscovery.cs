using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FileBridge.Core;

public static class NetworkAddressDiscovery
{
    /// <summary>Prefer an active Tailscale IPv4, then a private LAN IPv4, then loopback.</summary>
    public static string DiscoverAddress() => SelectAddress(NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n =>
        {
            var properties = n.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any));
            return properties.UnicastAddresses.Select(a => (a.Address, hasGateway));
        })).ToString();

    internal static IPAddress SelectAddress(IEnumerable<(IPAddress Address, bool HasGateway)> candidates)
    {
        var ipv4 = candidates.Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        return ipv4.Select(c => c.Address).FirstOrDefault(a => a.GetAddressBytes() is [100, >= 64 and <= 127, _, _])
            ?? ipv4.Where(c => c.Address.GetAddressBytes() is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _])
                .OrderByDescending(c => c.HasGateway).Select(c => c.Address).FirstOrDefault()
            ?? IPAddress.Loopback;
    }
}
