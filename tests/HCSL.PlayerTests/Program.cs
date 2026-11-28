using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Player;
using HCSL.Application.Abstractions;

void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("invalid input accepted");
}
PlayerServer Server(string host, int port = 27015) => new(Guid.NewGuid(), "Test", host, port);
if (args is ["--only", "catalog-json"])
{
    AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    Check(!System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection must be disabled");
    var directory = Directory.CreateTempSubdirectory("hcsl-player-json-");
    try
    {
        File.WriteAllText(Path.Combine(directory.FullName, "servers.json"),
            """[{"Id":"11111111-1111-1111-1111-111111111111","Name":"Legacy","Host":"localhost","Port":27015,"HasPassword":false}]""");
        var catalog = new PlayerServerStore(directory.FullName, new FakeSecrets());
        Check((await catalog.LoadAsync()).Single().Name == "Legacy", "legacy catalog must load");
        await catalog.SaveAsync(Server("example.com"), null);
        Check((await catalog.LoadAsync()).Any(row => row.Host == "example.com"), "saved catalog must reload");
        Console.WriteLine("PASS player catalog read/write without JSON reflection");
        return;
    }
    finally { directory.Delete(recursive: true); }
}

foreach (var host in new[] { "127.0.0.1", "203.0.113.99", "::1", "[2001:db8::1]", "example.com", "localhost", "例子.测试" })
    Check(PlayerServerRules.Normalize(Server(host)).Host.Length > 0, "valid host rejected");
foreach (var host in new[] { "", "https://example.com", "example.com:27015", "127.1", "999.1.1.1", "1.2.3.04", "x;quit", "-evil", "x/path", "fe80::1%eth0" })
    Reject(() => PlayerServerRules.Normalize(Server(host)));
Reject(() => PlayerServerRules.Normalize(Server("example.com", 0)));
Reject(() => PlayerServerRules.Normalize(Server("example.com", 65536)));
foreach (var password in new[] { "a;quit", "a\"", "a\n", "a+quit", "a\\" }) Reject(() => PlayerServerRules.Password(password));
Check(PlayerServerRules.Normalize(Server("EXAMPLE.COM")).Host == "example.com", "normalization");
Console.WriteLine("PASS input.syntax-ip-dns-port-password-no-network-probes");
var uri = SteamGameConnector.ConnectUri(Server("2001:db8::1"), "test pass");
Check(uri.Scheme == "steam" && uri.Host == "connect" && Uri.UnescapeDataString(uri.AbsolutePath) == "/[2001:db8::1]:27015/test pass", "steam dedicated connect uri");
var noPassword = SteamGameConnector.ConnectUri(Server("192.168.1.11"), "");
Check(noPassword.AbsoluteUri == "steam://rungameid/730//+connect%20192.168.1.11:27015", "IPv4 rungameid URI");
foreach (var host in new[] { "2001:db8::1", "example.com", "例子.测试" })
{
    var target = PlayerServerRules.Normalize(Server(host));
    var launch = SteamGameConnector.ConnectUri(target, "");
    Check(launch.Host == "rungameid" && Uri.UnescapeDataString(launch.AbsolutePath) == "/730//+connect " + target.Endpoint,
        "IPv6/IDN/DNS rungameid command");
    Check(launch.Query == "" && launch.Fragment == "", "launch command escaped incorrectly");
}
var encoded = SteamGameConnector.ConnectUri(Server("example.com"), "p/a?#% 空格");
Check(encoded.Query == "" && encoded.Fragment == "" && Uri.UnescapeDataString(encoded.AbsolutePath) == "/example.com:27015/p/a?#% 空格", "password must stay encoded inside a path segment");
Check(!encoded.AbsoluteUri.Contains("run/730") && !encoded.AbsoluteUri.Contains("+connect"), "must not use launch-argument forwarding");
Console.WriteLine("PASS steam.rungameid-no-password-and-dedicated-password-fallback");
var root = Path.Combine(Path.GetTempPath(), "hcsl-player-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var secrets = new FakeSecrets();
    var store = new PlayerServerStore(root, secrets);
    var server = Server("example.com");
    await store.SaveAsync(server, "test-secret");
    var rows = await store.LoadAsync();
    Check(rows.Count == 1 && rows[0].HasPassword, "save");
    Check(!File.ReadAllText(Path.Combine(root, "servers.json")).Contains("test-secret"), "plaintext leaked");
    await store.SaveAsync(server with { Name = "Edited" }, null);
    Check(await store.PasswordAsync(server.Id) == "test-secret", "edit lost password");
    var game = new FakeGame();
    var runtime = new FakeRuntime();
    var controller = new PlayerClientController(store, game, runtime);
    await controller.LoadAsync();
    await controller.ConnectAsync(controller.Servers[0]);
    Check(game.Password == "test-secret" && controller.LastOperationSucceeded, "connect use case");
    Check(controller.Status.Contains("不代表已入服"), "fake connected state");
    Check(await controller.ManualCommandAsync(controller.Servers[0]) == "password \"test-secret\"; connect example.com:27015", "password manual command missing or malformed");
    Check(await controller.ManualCommandAsync(server) == "connect example.com:27015", "plain manual command changed");
    Check(PlayerServerRules.ConnectCommand(Server("2001:db8::1"), "test pass") == "password \"test pass\"; connect [2001:db8::1]:27015", "quoted password or IPv6 endpoint incorrect");
    var calls = game.Calls;
    runtime.State = GameRunState.Stopped;
    await controller.RefreshGameStateAsync();
    Check(!controller.CanConnect(controller.Servers[0]), "stopped password server must not connect");
    await controller.ConnectAsync(controller.Servers[0]);
    Check(game.Calls == calls && !controller.LastOperationSucceeded, "stopped game received password URI");
    await controller.StartGameAsync();
    Check(runtime.Starts == 1 && game.Calls == calls, "starting must not auto-connect");
    runtime.State = GameRunState.Running;
    await controller.RefreshGameStateAsync();
    Check(controller.CanConnect(controller.Servers[0]) && game.Calls == calls, "process detection auto-connected");
    await controller.ConnectAsync(controller.Servers[0]);
    Check(game.Calls == calls + 1, "manual second click did not connect");
    runtime.State = GameRunState.Unknown;
    calls = game.Calls;
    await controller.ConnectAsync(controller.Servers[0]);
    Check(game.Calls == calls && !controller.LastOperationSucceeded, "unknown state bypassed confirmation");
    await controller.ConnectAsync(controller.Servers[0], confirmGameReady: true);
    Check(game.Calls == calls + 1, "unknown state explicit confirmation failed");
    runtime.FailProbe = true;
    await controller.RefreshGameStateAsync();
    Check(controller.GameState == GameRunState.Unknown, "probe failure treated as stopped");
    runtime.FailProbe = false;
    runtime.State = GameRunState.Stopped;
    calls = game.Calls;
    await controller.ConnectAsync(controller.Servers[0] with { HasPassword = false });
    Check(game.Calls == calls + 1 && game.Password == "", "no-password one-click connect was blocked");
    Check(SteamGameRuntime.StartUri.AbsoluteUri == "steam://rungameid/730", "plain launch contains arguments");
    runtime.State = GameRunState.Running;
    game.Fail = true;
    await controller.ConnectAsync(controller.Servers[0]);
    Check(!controller.LastOperationSucceeded && !controller.Busy, "launch error");
    await store.SaveAsync(server, "");
    Check(!(await store.LoadAsync())[0].HasPassword && secrets.Values.Count == 0, "clear password");
    await store.SaveAsync(server, "again");
    await store.DeleteAsync(server.Id);
    Check((await store.LoadAsync()).Count == 0 && secrets.Values.Count == 0, "delete");
    Console.WriteLine("PASS store.crud-secret-preserve-clear-delete-and-controller-launch-error");
    File.WriteAllText(Path.Combine(root, "servers.json"), "{broken");
    try { await store.SaveAsync(server, null); throw new Exception("corrupt catalog overwritten"); }
    catch (IOException) { }
    Check(File.ReadAllText(Path.Combine(root, "servers.json")) == "{broken", "catalog clobbered");
    Console.WriteLine("PASS store.corruption-fails-without-overwrite");
}
finally { Directory.Delete(root, true); }
Console.WriteLine("4 player test groups passed; no Steam, game, keyring or network process started.");

sealed class FakeSecrets : IPlayerSecretStore
{
    public Dictionary<Guid, string> Values = [];
    public Task<string> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult(Values[id]);
    public Task WriteAsync(Guid id, string password, CancellationToken ct) { Values[id] = password; return Task.CompletedTask; }
    public Task DeleteAsync(Guid id, CancellationToken ct) { Values.Remove(id); return Task.CompletedTask; }
}
sealed class FakeRuntime : IGameRuntime
{
    public GameRunState State = GameRunState.Running;
    public bool FailProbe;
    public int Starts;
    public Task<GameRunState> ProbeAsync(CancellationToken ct = default) => FailProbe
        ? throw new IOException("probe failed") : Task.FromResult(State);
    public Task RequestStartAsync(CancellationToken ct = default) { Starts++; return Task.CompletedTask; }
}
sealed class FakeGame : IGameConnector
{
    public string Password = "";
    public bool Fail;
    public int Calls;
    public Task RequestConnectAsync(PlayerServer server, string password, CancellationToken ct)
    { Calls++; if (Fail) throw new IOException("HCSL-CLIENT-LAUNCH: test failure"); Password = password; return Task.CompletedTask; }
}
