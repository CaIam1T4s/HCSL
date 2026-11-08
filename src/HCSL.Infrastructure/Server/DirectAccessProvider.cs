using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Server;

/// <summary>Direct CS2 access needs no helper. Legacy Network is not a bind/security policy.</summary>
internal sealed class DirectAccessProvider : IServerAccessProvider
{
    public string Name => "direct";
    public Task<AccessSession> PrepareAsync(ServerNetwork network, int gamePort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var address = LocalServerAddresses.Discover()[0];
        var observed = new ServerNetwork(address == "127.0.0.1" ? NetworkAccessMode.Loopback : NetworkAccessMode.Lan, address);
        return Task.FromResult(new AccessSession(observed, address, address + ":" + gamePort, null, Name));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class AccessProviders
{
    // Retained for the existing lifecycle contract; old instance fields cannot prevent direct startup.
    internal static IServerAccessProvider For(ServerNetwork network) => new DirectAccessProvider();
}
