using HCSL.Core;
namespace HCSL.Application.Abstractions;

/// <summary>Where the game server binds and what players should use, resolved before launch.</summary>
public sealed record AccessSession(ServerNetwork Network, string BindAddress, string AdvertisedEndpoint, string? InterfaceName, string Provider);

/// <summary>
/// Resolves the bind address for one run. Direct (loopback/LAN) needs no process; a future virtual
/// network provider starts and owns its own unit here, which is why the contract is asynchronous and
/// disposable.
/// </summary>
public interface IServerAccessProvider : IAsyncDisposable
{
    string Name { get; }
    Task<AccessSession> PrepareAsync(ServerNetwork network, int gamePort, CancellationToken cancellationToken);
}
