using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Frontend.Dev;
using HCSL.Infrastructure.Storage;

if (args.Contains("--help"))
{
    Console.WriteLine("Daemon-owned Linux single-instance lifecycle. Default: --status.\n" +
        "--start | --status | --logs | --stop | --recover | --query <command> --run <UUID> [--confirm-write]\n" +
        "[--instance <UUID>] [--run <UUID>] [--after <sequence>] [--timeout 1..300] [--wait]\n" +
        "[--settings <absolute path>] [--pipe <name>] [--allow-low-memory]\n" +
        "Start requires current deployment and systemd/cgroup v2. Below 3072 MiB is blocked; 3072..4095 MiB requires --allow-low-memory (this start only).\n" +
        "Initial mode is loopback LAN, insecure, without plugin/access-rule deployment. --stop requires --run.\n" +
        "Closing this CLI does not stop the daemon-owned server. --wait waits for map readiness/cleanup.\n" +
        "Examples: --status | --query \"meta version\" (current run) | --start --instance <UUID> --wait | --stop --run current");
    return 0;
}
try
{
    var values = new Dictionary<string, string>(); bool wait = false, actionSeen = false, allowLowMemory = false, confirmWrite = false;
    var action = ServerAction.Status;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--allow-low-memory" && !allowLowMemory) { allowLowMemory = true; continue; }
        if (args[i] == "--confirm-write" && !confirmWrite) { confirmWrite = true; continue; }
        if (args[i] == "--wait" && !wait) { wait = true; continue; }
        if (args[i] is "--start" or "--status" or "--logs" or "--stop" or "--recover")
        {
            if (actionSeen) throw new ArgumentException("Choose one action."); actionSeen = true;
            action = Enum.Parse<ServerAction>(args[i][2..], true); continue;
        }
        if (args[i] is not ("--query" or "--instance" or "--run" or "--after" or "--timeout" or "--settings" or "--pipe"))
            throw new ArgumentException($"Unknown option '{args[i]}'. Known options: --query --instance --run --after --timeout --settings --pipe. See --help.");
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option {args[i]} needs a value. Example: --query \"meta version\" or --run current.");
        if (!values.TryAdd(args[i], args[++i]))
            throw new ArgumentException($"Duplicate option {args[i - 1]}.");
    }
    var query = values.GetValueOrDefault("--query");
    if (query is not null && (actionSeen || wait || allowLowMemory)) throw new ArgumentException("--query cannot be combined with lifecycle actions, --wait or --allow-low-memory.");
    if (confirmWrite && query is null) throw new ArgumentException("--confirm-write requires --query.");
    if (query is not null) ServerConsoleRules.ValidateConfirmed(new(Guid.Empty, Guid.Empty, query, confirmWrite));
    if (allowLowMemory && action != ServerAction.Start) throw new ArgumentException("--allow-low-memory is valid only with --start.");
    Guid ParseId(string name, bool required)
    {
        if (!values.TryGetValue(name, out var text) && !required) return Guid.Empty;
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty) throw new ArgumentException(name + " requires a nonempty UUID."); return id;
    }
    var id = ParseId("--instance", action is ServerAction.Start or ServerAction.Recover);
    // --run is optional for --query/--stop: resolve the current run from the daemon instead of failing.
    var run = values.TryGetValue("--run", out var runText) && !runText.Equals("current", StringComparison.OrdinalIgnoreCase)
        ? ParseId("--run", false) : Guid.Empty;
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
    if (query is not null || action == ServerAction.Stop)
    {
        if (run == Guid.Empty)
        {
            var current = await client.SendAsync(new(ServerAction.Status));
            run = current.RunId ?? throw new ArgumentException("No current run. Start a server or pass --run <UUID>.");
        }
    }
    if (query is not null)
    {
        var receipt = await client.SendConsoleAsync(new(run, Guid.NewGuid(), query, confirmWrite));
        Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        if (receipt.Delivery != ConsoleDelivery.Written)
            Console.Error.WriteLine(DevHints.With("console not written (" + receipt.Code + ")", receipt.Code));
        return receipt.Delivery == ConsoleDelivery.Written ? 0 : 2;
    }
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
catch (Exception e) when (e is ServerException or IOException or ArgumentException or HCSL.Application.Abstractions.WorkspaceException or DaemonStartupException or OperationCanceledException or DaemonProtocolException)
{
    var code = e switch { ServerException s => s.Code, HCSL.Application.Abstractions.WorkspaceException w => w.Code, _ => null };
    Console.Error.WriteLine("HCSL-SERVER-CHECK: " + DevHints.With(e.Message, code));
    return 2;
}
