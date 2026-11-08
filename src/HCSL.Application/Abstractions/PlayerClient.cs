using HCSL.Core;

namespace HCSL.Application.Abstractions;

public interface IPlayerServerStore
{
    Task<IReadOnlyList<PlayerServer>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(PlayerServer server, string? password, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<string> PasswordAsync(Guid id, CancellationToken ct = default);
}

public enum GameRunState { Unknown, Stopped, Running }

public interface IGameRuntime
{
    Task<GameRunState> ProbeAsync(CancellationToken ct = default);
    Task RequestStartAsync(CancellationToken ct = default);
}

public interface IGameConnector
{
    Task RequestConnectAsync(PlayerServer server, string password, CancellationToken ct = default);
}
