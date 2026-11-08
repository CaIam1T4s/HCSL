using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Daemon.Ipc;
using HCSL.Daemon.Workers;
using HCSL.Frontend;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;

await HCSL.ServerTests.ServerNetworkReadinessTests.RunAsync();
await HCSL.ServerTests.HostInstanceCrudTests.RunAsync();
await HCSL.ServerTests.DirectAddressTests.RunAsync();

await HCSL.ServerTests.HostServerControllerTests.RunAsync();
HCSL.ServerTests.InstancePasswordTests.Run();
HCSL.ServerTests.ServerMemoryTests.Run();
if (await HCSL.ServerTests.ServerControllerTests.RunAsync() != 0) return 1;
if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux systemd lifecycle tests"); return 0; }
var gateAssembly = typeof(DaemonEntry).Assembly.Location;
WorkspaceRequest Workspace(string root) => new(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
SystemdServerRuntime Service(string root) => new(gateAssembly, (workspace, options, ct) =>
{
    if (File.ReadAllText(Path.Combine(root, "scenario")) == "prepare-slow") { ct.WaitHandle.WaitOne(1000); ct.ThrowIfCancellationRequested(); }
    var lease = new LocalWorkspaceManager().Acquire(workspace);
    var runtime = Path.Combine(lease.Layout.RuntimeRoot, options.InstanceId.ToString("D"));
    foreach (var dir in new[] { "game", "home", "userdata" }) Directory.CreateDirectory(Path.Combine(runtime, dir));
    return Task.FromResult(new ServerLaunch(lease, runtime, "de_dust2", "/bin/bash", [Path.Combine(root, "fake.sh"), File.ReadAllText(Path.Combine(root, "scenario")), root], [], new DirectAccessProvider()));
}, 128);
if (args is ["--fixture-daemon", var fixture, var endpoint])
{
    using var stop = new CancellationTokenSource();
    using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });
    await using var steam = new SteamCmdOperations(new NoSteam());
    await using var servers = new ServerOperations(Service(fixture));
    await new LocalDaemonServer(endpoint, steam, TimeSpan.FromSeconds(1), servers).RunAsync(stop.Token); return 0;
}
var root = Path.Combine(Path.GetTempPath(), "hcsl-server-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var oldState = Environment.GetEnvironmentVariable("XDG_STATE_HOME"); Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(root, "state"));
var request = Workspace(root); var workspacePaths = new WorkspacePaths(request.SettingsRoot, request.DataRoot, request.LibraryRoot);
var instance = new LocalInstanceStore().Execute(request, new(InstanceAction.Create, Name: "Fixture", GamePort: 27980)).Instances.Single();
var brokenDefaults = Path.Combine(root, "broken-defaults"); Directory.CreateDirectory(Path.Combine(brokenDefaults, "hcsl"));
File.WriteAllText(Path.Combine(brokenDefaults, "hcsl/settings.json"), "{broken");
var pipe = "hcsl-server-test-" + Guid.NewGuid().ToString("N"); var daemonClient = new DaemonClient(pipe); var client = new ServerClient(daemonClient);
var children = new List<Process>(); var units = new List<string>(); int passed = 0; ServerSnapshot? observed = null;
File.WriteAllText(Path.Combine(root, "fake.sh"), """
#!/bin/bash
stty -echo
test -t 0 && test -t 1 || exit 90
[[ "$HOME" == */home && "$USRLOCALcsgo" == */userdata && -z "$LD_PRELOAD" ]] || exit 91
printf 'FIXTURE_PTY_ENV_OK\n'
printf 'AuthStatus (invalid): Waiting (Waiting for Steam login)\nSDR network config fetch attempt #1 failed. No response from server. Trying again.\nGot SDR network config. Loaded revision 1 OK\nAuthStatus (steamid:123): OK (OK)\n'
group=$(sed -n 's/^0:://p' /proc/self/cgroup)
[[ "$(cat "/sys/fs/cgroup$group/memory.max")" == 134217728 && "$(cat "/sys/fs/cgroup$group/memory.swap.max")" == 0 ]] || exit 92
printf 'FIXTURE_LIMITS_OK\n' 
mode="$1"
if [[ "$mode" == exit ]]; then exit 7; fi
if [[ "$mode" == ignore ]]; then
  trap '' INT TERM
  setsid /bin/bash -c 'trap "" INT TERM; while true; do sleep 1; done' &
  echo "$!" > "$2/escaped.pid"
fi
if [[ "$mode" != timeout ]]; then
  printf 'CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)\nCreated physics for de_dust2\nSV: 16 player server started\n'
fi
printf 'sv_password SUPER_SECRET_FIXTURE_VALUE\n'
if [[ "$mode" == flood ]]; then
  for ((i=0; i<2200; i++)); do printf 'flood-%04d payload\n' "$i"; done
fi
while IFS= read -r line; do
  if [[ "$line" == quit && "$mode" != ignore ]]; then printf 'MainLoop returning\n'; exit 0; fi
done
""");
Process StartDaemon()
{
    var start = new ProcessStartInfo("/usr/bin/dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--fixture-daemon", root, pipe }) start.ArgumentList.Add(arg);
    start.Environment["XDG_CONFIG_HOME"] = brokenDefaults;
    var p = Process.Start(start)!; children.Add(p); return p;
}
async Task Until(Func<Task<bool>> predicate, string label, int seconds = 35)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed.TotalSeconds < seconds)
    { try { if (await predicate()) return; } catch (Exception e) when (e is IOException or OperationCanceledException) { } await Task.Delay(100); }
    throw new Exception("Timed out: " + label + " " + System.Text.Json.JsonSerializer.Serialize(observed));
}
async Task Test(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
async Task Reject(string code, Func<Task> action)
{
    try { await action(); throw new Exception("Expected " + code); } catch (IOException e) when (e.Message.Contains(code, StringComparison.Ordinal)) { }
}
async Task<ServerSnapshot> Status() => observed = await client.SendAsync(new(ServerAction.Status));
async Task<ServerSnapshot> Start(string scenario, int timeout = 10)
{
    File.WriteAllText(Path.Combine(root, "scenario"), scenario);
    var accepted = await client.SendAsync(new(ServerAction.Start, instance.Id, StartTimeoutSeconds: timeout), workspacePaths);
    units.Add("hcsl-server-" + accepted.RunId!.Value.ToString("N") + ".service"); return accepted;
}
async Task Stop(ServerSnapshot run)
{
    await client.SendAsync(new(ServerAction.Stop, RunId: run.RunId));
    await Until(async () => !(await Status()).Active, "stop cleanup");
    var final = await Status(); Check(final.CleanupConfirmed && final.State == ServerState.Stopped, "stop did not confirm cleanup: " + final.Code);
}
bool Live(int pid)
{
    try { var stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)] is not ('Z' or 'X'); } catch (IOException) { return false; }
}
var daemon = StartDaemon();
try
{
    await Until(async () => (await Status()).State == ServerState.Idle, "daemon startup");
    await Test("stop while preparing cancels without launching a process", async () =>
    {
        await Start("prepare-slow"); await Stop(await Status());
        Check(!File.Exists(Path.Combine(request.LibraryRoot, ".hcsl/server-session.json")), "preflight cancellation created session");
    });
    await Test("daemon start, PTY environment and actual map readiness", async () =>
    {
        var run = await Start("ready"); Check(run.Active && run.State == ServerState.Preparing, "start was not asynchronous");
        await Until(async () => (await Status()).State == ServerState.Running, "map readiness");
        var readyStatus = await Status();
        Check(readyStatus.MapLoaded, "map evidence missing");
        Check(readyStatus.NetworkReadiness is { Authentication: SteamAuthenticationState.Ready, Configuration: SdrConfigurationState.Ready }, "network readiness lost between PTY parser, daemon and IPC");
    });
    await Test("duplicate starts, stale stop and SteamCMD conflict are rejected", async () =>
    {
        await Reject("HCSL-SERVER-BUSY", () => Start("ready"));
        await Reject("HCSL-SERVER-STALE", () => client.SendAsync(new(ServerAction.Stop, RunId: Guid.NewGuid())));
        var response = await daemonClient.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.InstallOrUpdate, Options: new(workspacePaths)));
        Check(!response.Success && response.Code == "HCSL-SERVER-BUSY", "SteamCMD was admitted");
        try { using var lease = new LocalWorkspaceManager().Acquire(request); throw new Exception("workspace mutation admitted"); } catch (WorkspaceException e) when (e.Code == "HCSL-LOCK") { }
    });
    await Test("another frontend sees the same run and no requests cannot idle-exit an active daemon", async () =>
    {
        var before = await Status(); await Task.Delay(1600);
        var other = new ServerClient(new DaemonClient(pipe)); var after = await other.SendAsync(new(ServerAction.Status));
        Check(!daemon.HasExited && after.RunId == before.RunId && after.Active, "frontend detach/idle terminated server");
    });
    await Test("paged logs redact credentials and bind cursor to run ID", async () =>
    {
        var run = await Status(); var page = await client.SendAsync(new(ServerAction.Logs, RunId: run.RunId));
        Check(page.Lines.Length <= 128 && page.Lines.Any(l => l.Text.Contains("FIXTURE_PTY_ENV_OK")), "PTY evidence missing");
        var all = string.Join("\n", page.Lines.Select(l => l.Text));
        Check(!all.Contains("SUPER_SECRET_FIXTURE_VALUE") && all.Contains("redacted"), "credential appeared in logs");
        foreach (var file in Directory.GetFiles(Path.Combine(request.DataRoot, "instances"), "server-*.log", SearchOption.AllDirectories))
            Check(!File.ReadAllText(file).Contains("SUPER_SECRET_FIXTURE_VALUE"), "disk log contains credential");
        await Reject("HCSL-SERVER-STALE", () => client.SendAsync(new(ServerAction.Logs, RunId: Guid.NewGuid())));
    });
    await Test("graceful stop releases processes and workspace markers", async () =>
    {
        var settingsPath = Path.Combine(request.SettingsRoot, "settings.json"); var saved = File.ReadAllText(settingsPath);
        File.WriteAllText(settingsPath, "{broken");
        try
        {
            var run = await Status();
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "Hello_Counter_Strike_Launcher.slnx"))) repo = repo.Parent;
            var configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var cli = Path.Combine(repo!.FullName, "build/HCSL.ServerCheck/bin", configuration, "net10.0/HCSL.ServerCheck.dll");
            var start = new ProcessStartInfo("/usr/bin/dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { cli, "--stop", "--run", run.RunId!.Value.ToString(), "--wait", "--pipe", pipe }) start.ArgumentList.Add(arg);
            start.Environment["XDG_CONFIG_HOME"] = brokenDefaults;
            using var cliProcess = Process.Start(start)!; var output = cliProcess.StandardOutput.ReadToEndAsync(); var error = cliProcess.StandardError.ReadToEndAsync();
            await cliProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(cliProcess.ExitCode == 0, "CLI could not stop with broken preferences: " + await error);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<ServerSnapshot>(await output, new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
            Check(snapshot.State == ServerState.Stopped && snapshot.CleanupConfirmed, "CLI stop did not confirm cleanup");
        }
        finally { File.WriteAllText(settingsPath, saved); }
        Check(!File.Exists(Path.Combine(request.LibraryRoot, ".hcsl/server-session.json")), "workspace marker remains");
        using var lease = new LocalWorkspaceManager().Acquire(request);
    });
    await Test("idle exit resumes only after process cleanup", async () =>
    {
        await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Check(daemon.ExitCode == 0, "idle exit failed");
        daemon = StartDaemon(); await Until(async () => (await Status()).State == ServerState.Idle, "replacement daemon");
    });
    await Test("map startup timeout cleans its cgroup", async () =>
    {
        await Start("timeout", 1); await Until(async () => !(await Status()).Active, "startup timeout cleanup");
        var s = await Status(); Check(s.State == ServerState.Failed && s.Code == "HCSL-SERVER-START-TIMEOUT" && !s.MapLoaded && s.CleanupConfirmed, "timeout confused with ready");
    });
    await Test("early nonzero exit is failure rather than map success", async () =>
    {
        await Start("exit"); await Until(async () => !(await Status()).Active, "early exit");
        var s = await Status(); Check(s.State == ServerState.Failed && !s.MapLoaded && s.ExitCode != 0 && s.Code == "HCSL-SERVER-EXIT" && s.CleanupConfirmed, "early exit accepted");
    });
    await Test("log ring is bounded and explicitly reports truncated history", async () =>
    {
        await Start("flood"); await Until(async () => (await Status()).NextSequence > 2100, "log flood");
        var s = await Status(); var page = await client.SendAsync(new(ServerAction.Logs, RunId: s.RunId));
        Check(page.HistoryTruncated && page.Lines.Length == 128, "ring overflow not reported"); await Stop(s);
    });
    await Test("ignored quit and escaped process group are killed by cgroup cleanup", async () =>
    {
        await Start("ignore"); await Until(async () => (await Status()).MapLoaded && File.Exists(Path.Combine(root, "escaped.pid")), "escaped child");
        var pid = int.Parse(File.ReadAllText(Path.Combine(root, "escaped.pid"))); Check(Live(pid), "escaped child not alive");
        await Stop(await Status()); await Until(() => Task.FromResult(!Live(pid)), "escaped descendant cleanup");
        Check((await Status()).Code == "HCSL-SERVER-STOPPED-FORCED", "forced stop not distinguished");
    });
    await Test("daemon crash ends service and leaves fail-closed recoverable ownership", async () =>
    {
        await Start("ready"); await Until(async () => (await Status()).MapLoaded, "ready before crash");
        daemon.Kill(); await daemon.WaitForExitAsync();
        try { using var lease = new LocalWorkspaceManager().Acquire(request); throw new Exception("orphan session admitted"); } catch (WorkspaceException e) when (e.Code == "HCSL-SERVER-RECOVERY") { }
        await Until(() =>
        {
            try { Service(root).Recover(request, instance.Id); return Task.FromResult(true); } catch (ServerException) { return Task.FromResult(false); }
        }, "crash recovery after gate EOF");
        using (var lease = new LocalWorkspaceManager().Acquire(request)) { }
        daemon = StartDaemon(); await Until(async () => (await Status()).State == ServerState.Idle, "daemon after recovery");
    });
    await Test("graceful daemon termination stops its server before exit", async () =>
    {
        await Start("ready"); await Until(async () => (await Status()).MapLoaded, "ready before shutdown");
        using var kill = Process.Start(new ProcessStartInfo("/bin/kill", "-TERM " + daemon.Id) { UseShellExecute = false })!; await kill.WaitForExitAsync();
        await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(daemon.ExitCode == 0 && !File.Exists(Path.Combine(request.LibraryRoot, ".hcsl/server-session.json")), "daemon shutdown left session");
    });
    await Test("unknown and incomplete recovery records are preserved", () =>
    {
        var journal = Path.Combine(request.LibraryRoot, ".hcsl/server-session.json");
        File.WriteAllText(journal, "{\"FormatVersion\":999}");
        try { Service(root).Recover(request, instance.Id); throw new Exception("unknown record accepted"); }
        catch (ServerException e) when (e.Code == "HCSL-SERVER-RECOVERY") { }
        Check(File.Exists(journal), "unknown journal removed"); File.Delete(journal);
        var temp = journal + ".tmp-fixture"; File.WriteAllText(temp, "{");
        try { using var lease = new LocalWorkspaceManager().Acquire(request); throw new Exception("incomplete session admitted"); }
        catch (WorkspaceException e) when (e.Code == "HCSL-SERVER-RECOVERY") { }
        try { Service(root).Recover(request, instance.Id); throw new Exception("incomplete recovery accepted"); }
        catch (ServerException e) when (e.Code == "HCSL-SERVER-RECOVERY") { }
        Check(File.Exists(temp), "incomplete journal removed"); File.Delete(temp); return Task.CompletedTask;
    });
    Console.WriteLine($"{passed} daemon/systemd server lifecycle groups passed; no CS2 started."); return 0;
}
finally
{
    foreach (var file in Directory.GetFiles(root, "server-*.log", SearchOption.AllDirectories))
        if (passed < 14) Console.Error.WriteLine(File.ReadAllText(file));
    foreach (var child in children) { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } var error = await child.StandardError.ReadToEndAsync(); if (error.Length > 0) Console.Error.WriteLine(error[..Math.Min(error.Length, 4096)]); child.Dispose(); }
    foreach (var unit in units)
    {
        using var stop = Process.Start(new ProcessStartInfo("/usr/bin/systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--user", "stop", unit } })!;
        await stop.WaitForExitAsync();
    }
    Environment.SetEnvironmentVariable("XDG_STATE_HOME", oldState);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
sealed class NoSteam : ISteamCmdService
{
    public Task<SteamCmdResult> ProbeAsync(SteamCmdProbeRequest request, IProgress<SteamCmdEvent>? events = null, ISteamCmdInteraction? interaction = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SteamCmdResult> InstallAsync(SteamCmdInstallRequest request, IProgress<SteamCmdEvent>? events = null, ISteamCmdInteraction? interaction = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Recover(WorkspaceRequest workspace, string? executablePath = null) => throw new NotSupportedException();
}
