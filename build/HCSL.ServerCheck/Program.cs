using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Storage;

if (args.Contains("--help"))
{
    Console.WriteLine("Daemon-owned Linux single-instance lifecycle. Default: --status.\n" +
        "--start | --status | --logs | --stop | --recover\n" +
        "[--instance <UUID>] [--run <UUID>] [--after <sequence>] [--timeout 1..300] [--wait]\n" +
        "[--settings <absolute path>] [--pipe <name>] [--allow-low-memory]\n" +
        "Start requires current deployment and systemd/cgroup v2. Below 3072 MiB is blocked; 3072..4095 MiB requires --allow-low-memory (this start only).\n" +
        "Initial mode is loopback LAN, insecure, without plugin/access-rule deployment. --stop requires --run.\n" +
        "Closing this CLI does not stop the daemon-owned server. --wait waits for map readiness/cleanup.");
    return 0;
}
try
{
    var values = new Dictionary<string, string>(); bool wait = false, actionSeen = false, allowLowMemory = false;
    var action = ServerAction.Status;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--allow-low-memory" && !allowLowMemory) { allowLowMemory = true; continue; }
        if (args[i] == "--wait" && !wait) { wait = true; continue; }
        if (args[i] is "--start" or "--status" or "--logs" or "--stop" or "--recover")
        {
            if (actionSeen) throw new ArgumentException("Choose one action."); actionSeen = true;
            action = Enum.Parse<ServerAction>(args[i][2..], true); continue;
        }
        if (args[i] is not ("--instance" or "--run" or "--after" or "--timeout" or "--settings" or "--pipe") || i + 1 >= args.Length || !values.TryAdd(args[i], args[++i]))
            throw new ArgumentException("Invalid arguments. Use --help.");
    }
    if (allowLowMemory && action != ServerAction.Start) throw new ArgumentException("--allow-low-memory is valid only with --start.");
    Guid ParseId(string name, bool required)
    {
        if (!values.TryGetValue(name, out var text) && !required) return Guid.Empty;
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty) throw new ArgumentException(name + " requires a nonempty UUID."); return id;
    }
    var id = ParseId("--instance", action is ServerAction.Start or ServerAction.Recover);
    var run = ParseId("--run", action == ServerAction.Stop);
    if (values.TryGetValue("--after", out var rawCursor) && !long.TryParse(rawCursor, out _)) throw new ArgumentException("Invalid log cursor.");
    if (values.TryGetValue("--timeout", out var rawTimeout) && !int.TryParse(rawTimeout, out _)) throw new ArgumentException("Invalid startup timeout.");
    long after = values.TryGetValue("--after", out var a) && long.TryParse(a, out var n) ? n : 0;
    int timeout = values.TryGetValue("--timeout", out var t) && int.TryParse(t, out var seconds) ? seconds : 120;
    if (after < 0 || timeout is < 1 or > 300) throw new ArgumentException("Invalid cursor or startup timeout.");
    var pipe = values.GetValueOrDefault("--pipe"); var daemon = new DaemonClient(pipe);
    // Status is independent of preferences; a broken Hello must never block stopping an existing server.
    try
    {
        using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var response = await daemon.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Status), probe.Token);
        if (!response.Success) throw new IOException(response.Code + ": " + response.Message);
    }
    catch (Exception e) when (e is IOException or OperationCanceledException)
    { await new DaemonBootstrapper(pipe).EnsureRunningAsync(); }
    WorkspacePaths? workspace = null;
    if (action is ServerAction.Start or ServerAction.Recover)
    { var p = LocalPaths.Defaults(values.GetValueOrDefault("--settings")); workspace = new(p.SettingsRoot, p.DataRoot, p.LibraryRoot); }
    var client = new ServerClient(daemon);
    var snapshot = await client.SendAsync(new(action, id, run == Guid.Empty ? null : run, after, timeout, AllowLowMemory: allowLowMemory), workspace);
    if (wait && action is ServerAction.Start or ServerAction.Stop or ServerAction.Recover)
    {
        var expected = snapshot.RunId; var clock = System.Diagnostics.Stopwatch.StartNew();
        while (snapshot.Active && snapshot.State is not (ServerState.Running or ServerState.RecoveryRequired))
        {
            if (clock.Elapsed.TotalSeconds > timeout + 45) throw new IOException("Server task is still pending; query --status. CLI timeout does not cancel the task.");
            await Task.Delay(250); snapshot = await client.SendAsync(new(ServerAction.Status));
            if (snapshot.RunId != expected) throw new IOException("Daemon run changed; refresh status.");
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
    return snapshot.State is ServerState.Failed or ServerState.RecoveryRequired ? 2 : 0;
}
catch (Exception e) when (e is IOException or ArgumentException or HCSL.Application.Abstractions.WorkspaceException or DaemonStartupException or OperationCanceledException or DaemonProtocolException)
{ Console.Error.WriteLine("HCSL-SERVER-CHECK: " + e.Message); return 2; }
