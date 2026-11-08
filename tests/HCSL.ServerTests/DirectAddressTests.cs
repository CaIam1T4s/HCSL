using HCSL.Core;
using HCSL.Infrastructure.Server;

namespace HCSL.ServerTests;

public static class DirectAddressTests
{
    public static async Task RunAsync()
    {
        var addresses = LocalServerAddresses.Discover();
        if (!addresses.Contains("127.0.0.1") || addresses.Contains("0.0.0.0") || addresses.Distinct().Count() != addresses.Length)
            throw new Exception("Connection candidates must be unique local addresses, not wildcard bindings.");
        await using var provider = new DirectAccessProvider();
        // A stale interface, invalid legacy mode or old non-IPv4 text must not become a launch gate.
        foreach (var legacy in new[] { new ServerNetwork(NetworkAccessMode.Lan, "192.0.2.254"), new ServerNetwork((NetworkAccessMode)3, "old-interface") })
        {
            var session = await provider.PrepareAsync(legacy, 27015, CancellationToken.None);
            if (!addresses.Contains(session.BindAddress)) throw new Exception("Provider used legacy binding instead of observed address.");
        }
        Console.WriteLine("PASS direct.addresses.discovery-and-stale-legacy-network-not-a-launch-gate");
    }
}
