using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Storage;

if (args is ["--only", "wire-json"])
{
    var legacy = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, MaxDepth = 16 };
    var id = Guid.NewGuid();
    await CheckWire(new LocalRequest(LocalProtocol.Version, id, RequestKind.Hello,
        new ProbeOptions(new WorkspacePaths("settings", "data", "library"))));
    await CheckWire(new LocalResponse(LocalProtocol.Version, id, true, Snapshot:
        new OperationSnapshot(id, 1, true, ProbeStage.Ready, "ready", new Challenge(id, ChallengeKind.GuardCode),
            null, [new OperationEvent(1, ProbeStage.Ready, "ready", Transfer: new TransferSnapshot(TransferPhase.Downloading))])));
    Console.WriteLine("PASS source-generated IPC wire JSON matches legacy request and response");
    return 0;

    async Task CheckWire<T>(T message)
    {
        var expected = JsonSerializer.SerializeToUtf8Bytes(message, legacy);
        using var stream = new MemoryStream();
        await LocalProtocol.WriteAsync(stream, message, default);
        var frame = stream.ToArray();
        Check(BinaryPrimitives.ReadInt32LittleEndian(frame) == expected.Length && frame.AsSpan(4).SequenceEqual(expected),
            "IPC wire format changed");
        stream.Position = 0;
        var decoded = await LocalProtocol.ReadAsync<T>(stream, default);
        Check(JsonSerializer.SerializeToUtf8Bytes(decoded, legacy).AsSpan().SequenceEqual(expected), "IPC round-trip changed data");
    }
}

if (args is ["--only", "workspace-json"])
{
    AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    Check(!JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection must be disabled");
    var stub = new WorkspaceJsonDaemon();
    var catalog = await new InstanceClient(stub).ExecuteAsync(new(InstanceAction.List));
    Check(catalog.Instances is [] && catalog.ConnectionAddresses is ["127.0.0.1"], "instance catalog was not decoded");
    var plan = await new ServerClient(stub).PlanDeploymentAsync(stub.InstanceId, WorkspaceJsonDaemon.Paths);
    Check(plan.InstanceId == stub.InstanceId && plan.CopyMode == DeploymentCopyMode.FullCopy &&
        plan.PersistentMappings is [{ RuntimePath: "cfg" }], "deployment plan was not decoded");
    var instances = new InstanceClient(stub);
    var library = await instances.MapPoolsAsync(new(MapPoolLibraryAction.List, stub.InstanceId));
    Check(library.Library.Pools is [] && library.Library.Assignments.Count == 0, "map pool library was not decoded");
    var pool = await instances.MapPoolAsync(new(MapPoolAction.Read, stub.InstanceId, Guid.NewGuid()));
    Check(pool.Pool.Maps is [], "preset map pool was not decoded");
    var maps = await instances.MapCatalogAsync(stub.InstanceId);
    Check(maps.Entries is [{ Name: "de_inferno" }], "map catalog was not decoded");
    var baseline = await new BaselineClient(stub).StatusAsync();
    Check(baseline.BuildId == "123", "baseline was not decoded");
    var server = await new ServerClient(stub).SendAsync(new(ServerAction.Status));
    Check(server.State == ServerState.Idle && server.Lines is [], "server status was not decoded");
    using var steam = new SteamCmdController(stub, action => action());
    await steam.ConnectAsync();
    await steam.RefreshContentStatusAsync();
    Check(steam.ContentInstalled == true && steam.ContentBuildId == "123", "content status was not decoded");
    Console.WriteLine("PASS workspace and startup JSON with reflection disabled");
    return 0;
}

if (args is ["--only", "workspace-storage-json"])
{
    AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
    Check(!JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection must be disabled");
    var dir = Directory.CreateTempSubdirectory("hcsl-workspace-json-");
    try
    {
        var settings = Path.Combine(dir.FullName, "settings");
        var data = Path.Combine(dir.FullName, "saved-data");
        var library = Path.Combine(dir.FullName, "saved-library");
        var workspace = new HCSL.Application.Abstractions.WorkspaceRequest(settings, data, library);
        using (new LocalWorkspaceManager().Acquire(workspace)) { }
        var saved = LocalPaths.Defaults(settings);
        Check(saved.DataRoot == data && saved.LibraryRoot == library, "startup must reuse the saved workspace paths");
        using (new LocalWorkspaceManager().Acquire(workspace)) { }
        Console.WriteLine("PASS saved workspace startup with JSON reflection disabled");
        return 0;
    }
    finally { dir.Delete(recursive: true); }
}

var root = Path.Combine(Path.GetTempPath(), "hcsl-ipc-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
var configuredSettings = Path.Combine(root, "app-config", "hcsl");
if (OperatingSystem.IsLinux())
{
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "app-config"));
    using var initial = new LocalWorkspaceManager().Acquire(new(configuredSettings, Path.Combine(root, "saved-data"), Path.Combine(root, "saved-library")));
}
var oldState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(root, "state"));
var pipe = "hcsl-test-" + Guid.NewGuid().ToString("N");
var client = new DaemonClient(pipe);
var processes = new List<Process>();
var groups = new List<int>();
var controllers = new List<SteamCmdController>();
var solution = new DirectoryInfo(AppContext.BaseDirectory);
while (solution is not null && !File.Exists(Path.Combine(solution.FullName, "Hello_Counter_Strike_Launcher.slnx"))) solution = solution.Parent;
if (solution is null) throw new Exception("Run from a built source checkout.");
var configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
var fakeDirectory = Path.Combine(solution.FullName, "build", "HCSL.FakeSteamCmd", "bin", configuration, "net10.0");
var daemon = StartDaemon();
var count = 0;
try
{
    await Until(async () => (await Send(RequestKind.Hello)).Success);
    await Run("hello/defaults and incompatible protocol version", async () =>
    {
        var hello = await Send(RequestKind.Hello);
        Check(hello.Defaults is not null && hello.InstanceId != Guid.Empty, "missing defaults/daemon instance");
        var wrong = await client.SendAsync(new(999, Guid.NewGuid(), RequestKind.Hello));
        Check(!wrong.Success && wrong.Code == "HCSL-IPC-VERSION", "version was accepted");
    });
    await Run("instance CRUD through daemon and independent clients", async () =>
    {
        var first = new InstanceClient(client);
        var second = new InstanceClient(new DaemonClient(pipe));
        var catalog = await first.ExecuteAsync(new(InstanceAction.Create, Name: "IPC instance", GamePort: 27999));
        var entry = catalog.Instances.Single(); var d = entry.Definition!;
        Check((await second.ExecuteAsync(new(InstanceAction.List))).Instances.Single().Id == d.Id, "second frontend cannot read instance");
        await first.ExecuteAsync(new(InstanceAction.Update, d.Id, Revision: d.Revision, Name: "Updated", GamePort: d.GamePort));
        try { await second.ExecuteAsync(new(InstanceAction.Delete, d.Id, Revision: d.Revision)); throw new Exception("stale mutation accepted"); }
        catch (IOException e) when (e.Message.Contains("HCSL-INSTANCE-CONFLICT")) { }
        d = (await second.ExecuteAsync(new(InstanceAction.List))).Instances.Single().Definition!;
        await first.ExecuteAsync(new(InstanceAction.Delete, d.Id, Revision: d.Revision));
    });
    await Run("second daemon cannot claim the same endpoint", async () =>
    {
        var second = StartDaemon();
        Check(await Exit(second, 10000) == 2, "duplicate daemon was accepted");
    });
    await Run("oversized and incomplete local requests do not stop the server", async () =>
    {
        using (var broken = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await broken.ConnectAsync(3000);
            var size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, LocalProtocol.MaxFrameBytes + 1);
            await broken.WriteAsync(size);
            await broken.FlushAsync();
        }
        using (var partial = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await partial.ConnectAsync(3000);
            await partial.WriteAsync(new byte[] { 10, 0 });
        }
        Check((await Send(RequestKind.Hello)).Success, "malformed client killed server");
    });
    await Run("two frontends race to start one daemon-owned operation", async () =>
    {
        var options = Fixture("race", "hang");
        var responses = await Task.WhenAll(Send(RequestKind.StartProbe, options), Send(RequestKind.StartProbe, options));
        Check(responses.Count(r => r.Success) == 1 && responses.Count(r => r.Code == "HCSL-TASK-BUSY") == 1, "duplicate start not serialized");
        var current = responses.Single(r => r.Success).Snapshot!;
        await WaitForFile(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "child.pid"));
        var stale = await Send(RequestKind.Cancel, operation: Guid.NewGuid());
        Check(stale.Code == "HCSL-TASK-NOTFOUND", "wrong operation cancelled active task");
        var recover = await Send(RequestKind.Recover, options);
        Check(recover.Code == "HCSL-TASK-BUSY", "recovery raced active task");
        await CancelAndFinish(current.Id!.Value);
    });
    await Run("closing the initiating frontend preserves the task; another frontend can cancel", async () =>
    {
        var options = Fixture("detach", "hang");
        var first = Controller();
        await first.ConnectAsync();
        Configure(first, options);
        await first.StartAsync();
        var id = first.LastSnapshot!.Id;
        first.Dispose();
        await WaitForFile(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "child.pid"));
        Check((await Send(RequestKind.Status)).Snapshot!.Running, "client disposal cancelled task");
        var second = Controller();
        await second.ConnectAsync();
        Check(second.LastSnapshot?.Id == id && second.CanCancel && !second.CanStart, "reconnect did not attach to active task");
        await second.CancelAsync();
        await Finished(id!.Value, ProbeStage.Cancelled);
        second.Dispose();
    });
    await Run("password/Guard challenges can be resumed from another frontend without replay or logging", async () =>
    {
        var options = Fixture("account", "account") with { AccountName = "test_account" };
        var start = await Send(RequestKind.StartProbe, options);
        var id = start.Snapshot!.Id!.Value;
        var password = await Pending(ChallengeKind.Password);
        var stale = await Send(RequestKind.SubmitInput, operation: id, challenge: Guid.NewGuid(), secret: "not-used");
        Check(stale.Code == "HCSL-CHALLENGE-STALE", "stale challenge accepted");
        Check((await Send(RequestKind.SubmitInput, operation: id, challenge: password.Id, secret: "test-password-secret")).Success, "password submit failed");
        var replay = await Send(RequestKind.SubmitInput, operation: id, challenge: password.Id, secret: "test-password-secret");
        Check(replay.Code == "HCSL-CHALLENGE-STALE", "password challenge replay accepted");
        await Pending(ChallengeKind.GuardCode);
        var second = Controller();
        await second.ConnectAsync();
        Check(second.NeedsInput && second.CanSubmit, "resumed frontend did not expose challenge");
        await second.SubmitAsync("12345-secret");
        var completed = await Finished(id, ProbeStage.Succeeded);
        var serialized = JsonSerializer.Serialize(completed);
        Check(!serialized.Contains("test-password-secret") && !serialized.Contains("12345-secret"), "snapshot/history leaked secret");
        second.Dispose();
    });
    await Run("frontend installation and validation share daemon ownership and progress", async () =>
    {
        var options = Fixture("install-ui");
        var ui = Controller(); await ui.ConnectAsync(); Configure(ui, options);
        ui.InstallTimeoutSeconds = "7200";
        await ui.InstallAsync();
        var installed = await Finished(ui.LastSnapshot!.Id!.Value, ProbeStage.Succeeded);
        // The fake SteamCMD install ships no HCSL baseline inventory, so a Ready install legitimately
        // reports a pending baseline instead of HCSL-CS2-OK; the install itself must still have succeeded
        // and produced measured progress.
        var installedCode = installed.Result?.Code;
        Check(installedCode is "HCSL-CS2-OK" or "HCSL-CS2-BASELINE-PENDING" && installed.Events.Any(e => e.Percent == 12.5),
            $"installation progress/result missing: code={installedCode ?? "<none>"} percents=[{string.Join(",", installed.Events.Select(e => e.Percent?.ToString() ?? "null"))}]");
        Check(ui.ServerDirectory == Path.Combine(options.Workspace.LibraryRoot, "server"), "wrong install path displayed");
        await Until(() => Task.FromResult(!ui.Running));
        await ui.ValidateAsync();
        var validated = await Finished(ui.LastSnapshot!.Id!.Value, ProbeStage.Succeeded);
        Check(validated.Events.Any(e => e.Stage == ProbeStage.ValidatingContent && e.Percent == 75), "validation not routed");
        ui.Dispose();
    });
    await Run("installation races are rejected and closing frontend retains download", async () =>
    {
        var options = Fixture("install-detach", "install-hang");
        var ui = Controller(); await ui.ConnectAsync(); Configure(ui, options); await ui.InstallAsync();
        var id = ui.LastSnapshot!.Id!.Value; ui.Dispose();
        await WaitForFile(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "install.pid"));
        Check((await Send(RequestKind.ValidateContent, options)).Code == "HCSL-TASK-BUSY", "validation raced download");
        Check((await Send(RequestKind.StartProbe, options)).Code == "HCSL-TASK-BUSY", "probe raced download");
        Check((await Send(RequestKind.Status)).Snapshot!.Running, "frontend disposal cancelled installation");
        await CancelAndFinish(id);
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Workspace.LibraryRoot, ".hcsl", "cs2-install.json")));
        Check(state.RootElement.GetProperty("Status").GetString() == "Incomplete", "cancelled install became ready");
    });
    if (OperatingSystem.IsLinux()) await Run("saved defaults are advertised and broken preferences cannot block task cancellation", async () =>
    {
        var hello = await Send(RequestKind.Hello);
        Check(hello.Defaults!.LibraryRoot == Path.Combine(root, "saved-library"), "daemon ignored saved library");
        var options = Fixture("preferences-cancel", "hang");
        var start = await Send(RequestKind.StartProbe, options);
        await WaitForFile(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "child.pid"));
        var path = Path.Combine(configuredSettings, "settings.json");
        var original = File.ReadAllBytes(path);
        try
        {
            File.WriteAllText(path, "{broken");
            Check((await Send(RequestKind.Hello)).Code == "HCSL-FORMAT", "broken preferences not reported");
            Check((await Send(RequestKind.Status)).Snapshot!.Running, "preferences broke task status");
            await CancelAndFinish(start.Snapshot!.Id!.Value);
        }
        finally { File.WriteAllBytes(path, original); }
    });
    await Run("recover check is a visible daemon task", async () =>
    {
        var options = Fixture("recover");
        var start = await Send(RequestKind.Recover, options);
        var final = await Finished(start.Snapshot!.Id!.Value, ProbeStage.Succeeded);
        Check(final.Result?.Code == "HCSL-STEAMCMD-RECOVERED", "wrong recovery result");
    });
    await Run("frontend reports unknown state on daemon crash and detects a replacement instance", async () =>
    {
        var options = Fixture("crash", "hang");
        var ui = Controller();
        await ui.ConnectAsync();
        Configure(ui, options);
        await ui.StartAsync();
        await WaitForFile(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "child.pid"));
        var journal = Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, ".hcsl-session.json");
        using var document = JsonDocument.Parse(File.ReadAllText(journal));
        int? group = OperatingSystem.IsLinux() ? document.RootElement.GetProperty("ProcessGroup").GetInt32() : null;
        if (group is { } pg) groups.Add(pg);
        daemon.Kill(); // Do not kill the tool tree: test the actual daemon-crash behavior.
        await Exit(daemon, 10000);
        daemon = StartDaemon();
        await Until(async () => (await Send(RequestKind.Hello)).Success);
        await Until(() => Task.FromResult(!ui.Connected));
        Check(ui.StageText == "状态未知" && !ui.CanStart && !ui.CanSubmit, "disconnect claimed a terminal result");
        if (group is { } live) Kill(-live, 9);
        await Task.Delay(500);
        await ui.ConnectAsync();
        Check(ui.Connected && ui.LastSnapshot?.Stage == ProbeStage.Idle, "new daemon snapshot not accepted");
        Configure(ui, options);
        await ui.RecoverAsync();
        await Finished(ui.LastSnapshot!.Id!.Value, ProbeStage.Succeeded);
        ui.Dispose();
    });
    await Run("baseline status/refresh use real codes and fail closed without an installation", async () =>
    {
        var options = Fixture("baseline", "already-up-to-date");
        var status = await SendBaseline(options, new BaselineCommand(BaselineAction.Status));
        Check(!status.Success && status.Code == "HCSL-BASELINE-NOT-READY", "status without installation: " + status.Code);
        var refresh = await SendBaseline(options, new BaselineCommand(BaselineAction.Refresh));
        Check(!refresh.Success && refresh.Code == "HCSL-BASELINE-NOT-READY", "refresh without installation: " + refresh.Code);
        var unknown = await SendBaseline(options, new BaselineCommand((BaselineAction)99));
        Check(!unknown.Success && unknown.Code == "HCSL-BASELINE-REQUEST", "unknown baseline action: " + unknown.Code);
        Check(!File.Exists(Path.Combine(options.Workspace.LibraryRoot, ".hcsl", "linux-startup-baseline.json")), "failed refresh wrote a baseline");
    });
    if (OperatingSystem.IsLinux()) await Run("graceful daemon shutdown cancels and drains its tool process group", async () =>
    {
        var options = Fixture("shutdown", "hang");
        await Send(RequestKind.StartProbe, options);
        var childFile = Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "child.pid");
        await WaitForFile(childFile);
        Kill(daemon.Id, 15);
        Check(await Exit(daemon, 15000) == 0, "daemon shutdown failed");
        var pid = int.Parse(File.ReadAllText(childFile));
        Check(!Live(pid), "child survived graceful shutdown");
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, ".hcsl-session.json")), "shutdown left journal");
    });
    await Run("idle daemon exits normally and the endpoint can be reused", async () =>
    {
        if (!daemon.HasExited) { daemon.Kill(); await Exit(daemon, 10000); }
        daemon = StartDaemon(2);
        await Until(async () => (await Send(RequestKind.Hello)).Success);
        Check(await Exit(daemon, 10000) == 0, "idle daemon failed to exit normally");
        daemon = StartDaemon(2);
        await Until(async () => (await Send(RequestKind.Hello)).Success);
    });
    await Run("frontend polling and detached tasks prevent idle exit until cleanup finishes", async () =>
    {
        // Periodic frontend traffic must retain an otherwise idle daemon.
        for (int i = 0; i < 7; i++)
        {
            await Task.Delay(500);
            Check((await Send(RequestKind.Status)).Success, "polling frontend lost its daemon");
        }
        var options = Fixture("idle-active", "hang");
        var started = await Send(RequestKind.StartProbe, options);
        Check(started.Success, "task start rejected");
        var childFile = Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, "child.pid");
        await WaitForFile(childFile);
        // No client traffic for longer than the idle deadline, while a tool is alive.
        await Task.Delay(3500);
        Check(!daemon.HasExited, "idle timer terminated an active detached task");
        Check((await Send(RequestKind.Status)).Snapshot!.Running, "detached task stopped");
        await CancelAndFinish(started.Snapshot!.Id!.Value);
        Check(await Exit(daemon, 10000) == 0, "daemon did not exit after task cleanup and idle grace");
        Check(!File.Exists(Path.Combine(Path.GetDirectoryName(options.ExecutablePath)!, ".hcsl-session.json")), "idle exit preceded cleanup");
        if (OperatingSystem.IsLinux()) Check(!Live(int.Parse(File.ReadAllText(childFile))), "tool survived idle exit");
    });
    Console.WriteLine($"PASS: {count} local IPC/frontend scenarios ({RuntimeInformation.OSDescription})");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    if (daemon.HasExited) Console.Error.WriteLine("Daemon stderr: " + await daemon.StandardError.ReadToEndAsync());
    return 1;
}
finally
{
    foreach (var model in controllers) model.Dispose();
    foreach (var process in processes)
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); }
        process.Dispose();
    }
    if (OperatingSystem.IsLinux()) foreach (var group in groups) Kill(-group, 9);
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
    Directory.Delete(root, recursive: true);
    if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("XDG_STATE_HOME", oldState);
}

SteamCmdController Controller()
{
    var gate = new object();
    var model = new SteamCmdController(new DaemonClient(pipe), action => { lock (gate) action(); });
    controllers.Add(model);
    return model;
}
static void Configure(SteamCmdController model, ProbeOptions options)
{
    model.SettingsRoot = options.Workspace.SettingsRoot; model.DataRoot = options.Workspace.DataRoot;
    model.LibraryRoot = options.Workspace.LibraryRoot; model.ExecutablePath = options.ExecutablePath!;
    model.InstallIfMissing = false; model.AccountName = options.AccountName ?? "";
}
Process StartDaemon(int? idleSeconds = null)
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(typeof(DaemonEntry).Assembly.Location);
    start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipe);
    if (idleSeconds is { } seconds) { start.ArgumentList.Add("--idle-seconds"); start.ArgumentList.Add(seconds.ToString()); }
    var process = Process.Start(start)!; processes.Add(process); return process;
}
ProbeOptions Fixture(string name, string scenario = "success")
{
    var directory = Path.Combine(root, name + "-tool");
    Directory.CreateDirectory(directory);
    foreach (var file in Directory.EnumerateFiles(fakeDirectory))
    {
        var target = Path.Combine(directory, Path.GetFileName(file));
        File.Copy(file, target);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
    }
    File.WriteAllText(Path.Combine(directory, "scenario.txt"), scenario);
    var parent = Path.Combine(root, name);
    return new(new(Path.Combine(parent, "config"), Path.Combine(parent, "data"), Path.Combine(parent, "library")),
        Path.Combine(directory, "HCSL.FakeSteamCmd" + (OperatingSystem.IsWindows() ? ".exe" : "")), false);
}
Task<LocalResponse> Send(RequestKind kind, ProbeOptions? options = null, Guid? operation = null, Guid? challenge = null, string? secret = null)
    => client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), kind, options, operation, challenge, secret));
Task<LocalResponse> SendBaseline(ProbeOptions options, BaselineCommand command)
    => client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Baseline, options,
        BaselineJson: JsonSerializer.Serialize(command)));
async Task<Challenge> Pending(ChallengeKind kind)
{
    Challenge? challenge = null;
    await Until(async () => { challenge = (await Send(RequestKind.Status)).Snapshot?.Challenge; return challenge?.Kind == kind; });
    return challenge!;
}
async Task<OperationSnapshot> Finished(Guid id, ProbeStage expected)
{
    OperationSnapshot? snapshot = null;
    await Until(async () => { snapshot = (await Send(RequestKind.Status)).Snapshot; return snapshot?.Id == id && !snapshot.Running; });
    Check(snapshot!.Stage == expected, $"Expected {expected}, got {snapshot.Stage}: {snapshot.Message}");
    return snapshot;
}
async Task CancelAndFinish(Guid id) { Check((await Send(RequestKind.Cancel, operation: id)).Success, "cancel rejected"); await Finished(id, ProbeStage.Cancelled); }
async Task Run(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS: " + name); }
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Until(Func<Task<bool>> condition)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        try { if (await condition()) return; }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        await Task.Delay(50);
    }
    throw new Exception("Timed out waiting for local daemon state.");
}
static async Task WaitForFile(string path) => await Until(() => Task.FromResult(File.Exists(path) && new FileInfo(path).Length > 0));
static async Task<int> Exit(Process process, int milliseconds)
{
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(milliseconds));
    return process.ExitCode;
}
static bool Live(int pid)
{
    try { var stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[stat.LastIndexOf(')') + 2] is not ('Z' or 'X'); }
    catch (IOException) { return false; }
}
[DllImport("libc", EntryPoint = "kill", SetLastError = true)]
static extern int Kill(int pid, int signal);

sealed class WorkspaceJsonDaemon : IDaemonClient
{
    public static readonly WorkspacePaths Paths = new("settings", "data", "library");
    public Guid InstanceId { get; } = Guid.NewGuid();

    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        var response = new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: Paths);
        if (request.Kind is RequestKind.Hello or RequestKind.Status)
            return Task.FromResult(response with { InstanceId = InstanceId });
        if (request.Kind == RequestKind.ContentStatus)
            return Task.FromResult(response with { InstanceId = InstanceId, ContentStatusJson = """{"Installed":true,"BuildId":"123"}""" });

        if (request.Kind == RequestKind.MapCatalog)
        {
            using var json = JsonDocument.Parse(request.MapCatalogJson!);
            if (json.RootElement.GetProperty("InstanceId").GetGuid() != InstanceId)
                throw new InvalidOperationException("Unexpected map catalog instance");
            return Task.FromResult(response with { MapCatalogResultJson = """{"Entries":[{"Name":"de_inferno","Source":0}]}""" });
        }

        using var command = JsonDocument.Parse(request.InstanceCommandJson ?? request.ServerCommandJson ?? request.BaselineJson ?? request.MapPoolLibraryJson ?? request.MapPoolJson!);
        var action = command.RootElement.GetProperty("Action").GetInt32();
        return Task.FromResult(request.Kind switch
        {
            RequestKind.Instances when action == (int)InstanceAction.List => response with
            {
                InstanceCatalogJson = """{"Instances":[],"ConnectionAddresses":["127.0.0.1"]}"""
            },
            RequestKind.Server when action == (int)ServerAction.PlanDeployment &&
                command.RootElement.GetProperty("InstanceId").GetGuid() == InstanceId => response with
            {
                DeploymentPlanJson = $$"""{"InstanceId":"{{InstanceId}}","CopyMode":1,"PersistentMappings":[{"RuntimePath":"cfg","PersistentPath":"preset/cfg"}],"Notes":[]}"""
            },
            RequestKind.MapPoolLibrary when action == (int)MapPoolLibraryAction.List => response with
            {
                MapPoolLibraryResultJson = """{"Library":{"FormatVersion":1,"Revision":0,"Pools":[],"Assignments":{}}}"""
            },
            RequestKind.MapPool when action == (int)MapPoolAction.Read => response with
            {
                MapPoolResultJson = """{"Pool":{"FormatVersion":1,"Revision":0,"Maps":[]}}"""
            },
            RequestKind.Baseline when action == (int)BaselineAction.Status => response with
            {
                BaselineResultJson = """{"BuildId":"123","Source":"local"}"""
            },
            RequestKind.Server when action == (int)ServerAction.Status => response with
            {
                ServerSnapshotJson = """{"State":0,"Code":"idle","Lines":[]}"""
            },
            _ => throw new InvalidOperationException("Unexpected workspace command")
        });
    }
}
