using System.ComponentModel;
using HCSL.Application.Abstractions;
using HCSL.Core;

namespace HCSL.Frontend;

/// <summary>UI-independent use-case state; never probes a server or reports a successful join.</summary>
public sealed class PlayerClientController(IPlayerServerStore store, IGameConnector connector, IGameRuntime? runtime = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<PlayerServer> Servers { get; private set; } = [];
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "未连接；选择或添加服务器。";
    public bool LastOperationSucceeded { get; private set; }
    public GameRunState GameState { get; private set; } = GameRunState.Unknown;
    private int checkingGame;
    public bool CanConnect(PlayerServer? server) => !Busy && server is not null && (!server.HasPassword || GameState != GameRunState.Stopped);
    public string GameHint => GameState switch
    {
        GameRunState.Running => "已检测到 CS2 进程，不代表已加载完成。进入主界面后手动点击连接服务器。",
        GameRunState.Stopped => "未检测到 CS2。请先启动游戏，进入主界面后点击连接服务器。",
        _ => "CS2 状态未知。请手动启动游戏；确认已进入主界面后再点击确认连接。"
    };
    public async Task RefreshGameStateAsync()
    {
        if (Interlocked.CompareExchange(ref checkingGame, 1, 0) != 0) return;
        try { await ProbeGameAsync(); Notify(); }
        finally { Interlocked.Exchange(ref checkingGame, 0); }
    }
    private async Task ProbeGameAsync()
    {
        try { GameState = runtime is null ? GameRunState.Unknown : await runtime.ProbeAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { GameState = GameRunState.Unknown; }
    }
    public Task StartGameAsync() => Execute(async () =>
    {
        if (runtime is null) throw new IOException("HCSL-CLIENT-LAUNCH: 请手动从 Steam 启动 CS2。");
        await ProbeGameAsync();
        if (GameState == GameRunState.Running) { Status = GameHint; return; }
        await runtime.RequestStartAsync();
        Status = "已请求启动 CS2，未附加任何启动参数。进入游戏主界面后，请手动点击连接服务器。";
    });
    private async Task Execute(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; LastOperationSucceeded = false; Notify();
        try { await action(); LastOperationSucceeded = true; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        { Status = e.Message; }
        finally { Busy = false; Notify(); }
    }
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    public Task LoadAsync() => Execute(async () => { Servers = await store.LoadAsync(); Status = $"已加载 {Servers.Count} 个服务器。"; });
    public Task SaveAsync(PlayerServer server, string? password) => Execute(async () =>
    {
        await store.SaveAsync(server, password);
        Servers = await store.LoadAsync(); Status = "服务器已保存。";
    });
    public Task DeleteAsync(Guid id) => Execute(async () =>
    {
        await store.DeleteAsync(id); Servers = await store.LoadAsync(); Status = "服务器已删除。";
    });
    public Task ConnectAsync(PlayerServer server, bool confirmGameReady = false) => Execute(async () =>
    {
        if (server.HasPassword)
        {
            await ProbeGameAsync();
            if (GameState == GameRunState.Stopped)
                throw new IOException("请先启动 CS2，进入主界面后再连接密码服务器。");
            if (GameState == GameRunState.Unknown && !confirmGameReady)
                throw new IOException("CS2 状态未知；请确认游戏已进入主界面，再使用确认连接入口。");
        }
        var password = server.HasPassword ? await store.PasswordAsync(server.Id) : "";
        await connector.RequestConnectAsync(server, password);
        Status = "已发送 Steam/CS2 连接请求，不代表已入服。若游戏未响应，请使用手动连接命令。";
    });
    public async Task<string> ManualCommandAsync(PlayerServer server)
    {
        server = PlayerServerRules.Normalize(server);
        if (!server.HasPassword) return $"connect {server.Endpoint}";
        var password = await store.PasswordAsync(server.Id);
        return PlayerServerRules.ConnectCommand(server, password);
    }
}
