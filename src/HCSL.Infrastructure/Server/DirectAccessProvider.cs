using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Server;

/// <summary>
/// Loopback and LAN access need no helper process: the address already exists on this host, so
/// preparing a run only verifies it. A virtual-network provider will implement the same contract by
/// starting its own unit and waiting for its address.
/// </summary>
internal sealed class DirectAccessProvider : IServerAccessProvider
{
    public string Name => "direct";
    public Task<AccessSession> PrepareAsync(ServerNetwork network, int gamePort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceRules.Network(network);
        var resolved = InstanceRules.Resolve(network);
        var address = IPAddress.Parse(resolved.BindAddress);
        var endpoint = new IPEndPoint(address, gamePort).ToString();
        if (resolved.Mode == NetworkAccessMode.Loopback)
            return Task.FromResult(new AccessSession(resolved, resolved.BindAddress, endpoint, "lo", Name));
        var match = Interface(address);
        if (match is null)
            throw new ServerException("HCSL-SERVER-BIND",
                $"{resolved.BindAddress} is not an address of an active local interface. Choose the address this host currently has, or switch the instance back to loopback.");
        return Task.FromResult(new AccessSession(resolved, resolved.BindAddress, endpoint, match, Name));
    }
    /// <summary>Name of the active interface that owns this address, or null.</summary>
    private static string? Interface(IPAddress address)
    {
        foreach (var candidate in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (candidate.OperationalStatus != OperationalStatus.Up || candidate.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (candidate.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(address))) return candidate.Name;
        }
        return null;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class AccessProviders
{
    internal static IServerAccessProvider For(ServerNetwork network) => network.Mode switch
    {
        NetworkAccessMode.Loopback or NetworkAccessMode.Lan => new DirectAccessProvider(),
        // Adding a provider here is the only place that changes when virtual networks arrive.
        _ => throw new ServerException("HCSL-SERVER-BIND", "Unsupported network access mode.")
    };
}
