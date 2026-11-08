using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HCSL.Infrastructure.Server;

/// <summary>Local interface discovery only. Not a public-IP, reachability or firewall probe.</summary>
public static class LocalServerAddresses
{
    public static string[] Discover()
    {
        var addresses = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var entry in nic.GetIPProperties().UnicastAddresses)
                    if (entry.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(entry.Address)
                        && !entry.Address.Equals(IPAddress.Any)) addresses.Add(entry.Address.ToString());
            }
        }
        catch (Exception e) when (e is NetworkInformationException or SocketException or PlatformNotSupportedException) { }
        // Local clients always have a loopback candidate; it does not mean other interfaces are blocked.
        return addresses.Take(16).Append("127.0.0.1").ToArray();
    }
}
