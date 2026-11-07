using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Server;

public sealed class SystemdServerRuntime : IServerRuntimeService
{
    private readonly string assembly;
    private readonly Func<WorkspaceRequest, ServerStartOptions, CancellationToken, Task<ServerLaunch>> prepare;
    private readonly int memoryMiB;
    public SystemdServerRuntime(string gateAssembly) : this(gateAssembly, ServerPreparation.PrepareAsync, 2560) { }
    internal SystemdServerRuntime(string gateAssembly, Func<WorkspaceRequest, ServerStartOptions, CancellationToken, Task<ServerLaunch>> prepare, int memoryMiB = 64)
    { assembly = Path.GetFullPath(gateAssembly); this.prepare = prepare; this.memoryMiB = memoryMiB; }
    internal sealed record Session(int FormatVersion, Guid RunId, Guid InstanceId, Guid DataId, Guid LibraryId, int OwnerPid, long OwnerStartTicks, string PidNamespace);
    private static string Unit(Guid id) => "hcsl-server-" + id.ToString("N") + ".service";
    private static string Journal(WorkspaceLayout l) => Path.Combine(l.LibraryRoot, ".hcsl", "server-session.json");
    private static string Marker(string runtime) => Path.Combine(runtime, ".hcsl-running.json");
    private static ProcessStartInfo Command(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var keep = new[] { "HOME", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "PATH", "LANG" }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        start.Environment.Clear(); foreach (var pair in keep) if (pair.Value != null) start.Environment[pair.Key] = pair.Value;
        foreach (var arg in args) start.ArgumentList.Add(arg); return start;
    }
    private static async Task<(int Exit, string Text)> Control(params string[] args)
    {
        using var process = Process.Start(Command("/usr/bin/systemctl", ["--user", ..args]))!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
    private static async Task<bool> Empty(string unit)
    {
        try
        {
            var result = await Control("show", unit, "--property=LoadState,ActiveState,ControlGroup,MainPID");
            var data = result.Text.Split('\n').Where(l => l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            if (!data.TryGetValue("LoadState", out var load) || !data.TryGetValue("ActiveState", out var active)) return false;
            if (active is not ("inactive" or "failed")) return false;
            if (!data.TryGetValue("MainPID", out var pid) || pid != "0") return false;
            if (data.TryGetValue("ControlGroup", out var group) && group.Length > 0)
            {
                if (!group.StartsWith('/') || group.Split('/').Any(p => p == "..")) return false;
                var path = "/sys/fs/cgroup" + group;
                if (Directory.Exists(path))
                    foreach (var file in Directory.EnumerateFiles(path, "cgroup.procs", SearchOption.AllDirectories))
                        if (File.ReadAllText(file).Trim().Length > 0) return false;
            }
            return load is "loaded" or "not-found";
        }
        catch { return false; }
    }
    public async Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop)
    {
        if (!OperatingSystem.IsLinux()) throw new ServerException("HCSL-SERVER-PLATFORM", "Only Linux x64 is implemented.");
        if (!File.Exists("/sys/fs/cgroup/cgroup.controllers")) throw new ServerException("HCSL-SERVER-CGROUP", "cgroup v2 is required; no unsupervised fallback is available.");
        stop.ThrowIfCancellationRequested();
        ServerLaunch prepared;
        try { prepared = await prepare(workspace, options, stop); }
        // InstanceException carries the actionable code (HCSL-INSTANCE-BIND): replacing it with a generic
        // PREFLIGHT code told the operator nothing about the address they had just edited.
        catch (Exception e) when (e is not (ServerException or WorkspaceException or DeploymentException or InstanceException or OperationCanceledException))
        { throw new ServerException("HCSL-SERVER-PREFLIGHT", "Server preflight failed; no game process was launched."); }
        await using var launch = prepared; var l = launch.Workspace.Layout;
        var journal = Journal(l); var marker = Marker(launch.Runtime);
        using var owner = Process.GetCurrentProcess();
        var session = new Session(2, runId, options.InstanceId, launch.Workspace.Identity.DataId, launch.Workspace.Identity.LibraryId,
            owner.Id, LinuxProcessIdentity.StartTicks(owner.Id), new FileInfo("/proc/self/ns/pid").LinkTarget!);
        var unit = Unit(runId); bool mapLoaded = false, clean = true, journaled = false;
        Process? process = null; Task? stdout = null, stderr = null; int? exit = null; string code = "HCSL-SERVER-EXIT";
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parserLock = new object(); bool active = false, physics = false, players = false; long logBytes = 0, mapAt = 0;
        var logPath = Path.Combine(l.InstancesRoot, options.InstanceId.ToString("D"), "logs", "server-" + runId.ToString("N") + ".log");
        LocalPaths.RejectLink(logPath);
        using var disk = new StreamWriter(LocalPaths.CreateFile(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        void Line(string raw)
        {
            lock (parserLock)
            {
                var text = Regex.Replace(raw, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", "");
                text = new string(text.Where(c => !char.IsControl(c)).ToArray());
                if (text == "HCSL_SERVER_GATE_READY") { ready.TrySetResult(); return; }
                if (text == "HCSL_SERVER_CHILD_STARTED") { events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-PROCESS-STARTED")); return; }
                if (Regex.IsMatch(text, @"(?i)password|token|steamaccount|gslt|secret")) text = "[redacted sensitive configuration]";
                if (text.Length > 2048) text = text[..2048] + " [truncated]";
                if (logBytes < 8 * 1024 * 1024) { disk.WriteLine(text); logBytes += Encoding.UTF8.GetByteCount(text) + 1; }
                events.Report(new(ServerState.Starting, "HCSL-SERVER-LOG", text));
                active |= text.Contains("ss_loading -> ss_active", StringComparison.Ordinal);
                physics |= text.Contains("Created physics for " + launch.Map, StringComparison.Ordinal);
                players |= Regex.IsMatch(text, @"SV:\s+\d+ player server started");
                if (!mapLoaded && active && physics && players) { Volatile.Write(ref mapAt, Stopwatch.GetTimestamp()); Volatile.Write(ref mapLoaded, true); }
            }
        }
        async Task Pump(StreamReader stream)
        {
            var chars = new char[1024]; var line = new StringBuilder(); bool truncated = false;
            int count;
            while ((count = await stream.ReadAsync(chars)) > 0)
                foreach (var c in chars.AsSpan(0, count))
                {
                    if (c is '\n' or '\r') { if (line.Length > 0) Line(line.ToString() + (truncated ? " [truncated]" : "")); line.Clear(); truncated = false; }
                    else if (line.Length < 8192) line.Append(c); else truncated = true;
                }
            if (line.Length > 0) Line(line.ToString());
        }
        try
        {
            DeploymentFiles.AtomicJson(journal, session); journaled = true; DeploymentFiles.AtomicJson(marker, session);
            var start = Command("/usr/bin/systemd-run", "--user", "--wait", "--pipe", "--collect", "--unit=" + unit, "--service-type=exec", "--working-directory=" + Path.Combine(launch.Runtime, "game"),
                "-p", "KillMode=control-group", "-p", "KillSignal=SIGINT", "-p", "TimeoutStopSec=5", "-p", "SendSIGKILL=yes", "-p", "MemoryAccounting=yes",
                "-p", "MemoryHigh=" + Math.Max(32, memoryMiB * 4 / 5) + "M", "-p", "MemoryMax=" + memoryMiB + "M", "-p", "MemorySwapMax=0", "-p", "TasksMax=128", "-p", "CPUQuota=100%", "-p", "LimitCORE=0", "-p", "OOMPolicy=stop",
                "/usr/bin/env", "-i", "HOME=" + Path.Combine(launch.Runtime, "home"), "USRLOCALcsgo=" + Path.Combine(launch.Runtime, "userdata"), "PATH=/usr/bin:/bin", "LANG=C.UTF-8", "/usr/bin/dotnet", assembly, "--server-gate");
            events.Report(new(ServerState.Starting, "HCSL-SERVER-STARTING"));
            process = Process.Start(start)!; stdout = Pump(process.StandardOutput); stderr = Pump(process.StandardError);
            var exited = process.WaitForExitAsync(); var clock = Stopwatch.StartNew(); bool released = false, publishedReady = false;
            while (!exited.IsCompleted)
            {
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("Server output could not be drained.");
                if (stop.IsCancellationRequested) { code = "HCSL-SERVER-STOPPED"; break; }
                if (ready.Task.IsCompleted && !released)
                {
                    launch.ReleasePorts();
                    var command = new ServerGateCommand(session.OwnerPid, session.OwnerStartTicks, session.PidNamespace, launch.Executable, launch.Arguments, Path.Combine(launch.Runtime, "game"), memoryMiB * 1024L * 1024);
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)); await process.StandardInput.FlushAsync(); released = true;
                }
                if (Volatile.Read(ref mapLoaded) && !publishedReady && Stopwatch.GetElapsedTime(Volatile.Read(ref mapAt)).TotalSeconds >= 2)
                {
                    publishedReady = true;
                    events.Report(new(ServerState.Running, "HCSL-SERVER-MAP-READY"));
                    // Recording must never disturb a run that already reached readiness.
                    try { ServerLastRunStore.Write(l.InstancesRoot, options.InstanceId, runId, launch.Map); }
                    catch (Exception) { }
                }
                if (!publishedReady && clock.Elapsed.TotalSeconds >= options.StartTimeoutSeconds) { code = "HCSL-SERVER-START-TIMEOUT"; break; }
                await Task.Delay(100);
            }
            if (!exited.IsCompleted)
            {
                events.Report(new(ServerState.Stopping, code));
                try { await process.StandardInput.WriteLineAsync("STOP"); await process.StandardInput.FlushAsync(); } catch (IOException) { }
                try { await exited.WaitAsync(TimeSpan.FromSeconds(7)); } catch (TimeoutException) { }
            }
            if (process.HasExited) exit = process.ExitCode;
            if (code == "HCSL-SERVER-STOPPED" && exit != 0) code = "HCSL-SERVER-STOPPED-FORCED";
        }
        catch (Exception) { code = "HCSL-SERVER-PROCESS"; }
        finally
        {
            if (journaled)
            {
                // Parent EOF asks the gate to quit; systemd stop also drains descendants that escaped their original process group.
                try { process?.StandardInput.Close(); } catch { }
                try { await Control("stop", unit); } catch { }
                clean = await Empty(unit);
                if (process is { HasExited: false }) { try { process.Kill(); await process.WaitForExitAsync(); } catch { clean = false; } }
                try { await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)); } catch { clean = false; }
                if (clean) { LocalPaths.RejectLink(marker); File.Delete(marker); LocalPaths.RejectLink(journal); File.Delete(journal); }
                process?.Dispose();
            }
        }
        if (!clean) return new("HCSL-SERVER-RECOVERY", exit, mapLoaded, false);
        if (code == "HCSL-SERVER-EXIT" && exit == 0) code = "HCSL-SERVER-EXITED";
        return new(code, exit, mapLoaded, true);
    }
    public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null)
    {
        if (!OperatingSystem.IsLinux()) throw new ServerException("HCSL-SERVER-PLATFORM", "Only Linux is implemented.");
        using var lease = new LocalWorkspaceManager().AcquireForServerRecovery(workspace); var l = lease.Layout;
        var journal = Journal(l); LocalPaths.RejectLink(journal);
        if (Directory.EnumerateFiles(Path.GetDirectoryName(journal)!, "server-session.json.tmp-*").Any())
            throw new ServerException("HCSL-SERVER-RECOVERY", "Incomplete session journal requires inspection; no temporary record was removed.");
        if (!File.Exists(journal))
        {
            var runtimePath = Path.Combine(l.RuntimeRoot, instanceId.ToString("D")); LocalPaths.RejectLink(runtimePath); LocalPaths.RejectLink(Marker(runtimePath));
            if (expectedRunId != null && !File.Exists(Marker(runtimePath)) && Empty(Unit(expectedRunId.Value)).GetAwaiter().GetResult()) return;
            throw new ServerException("HCSL-SERVER-RECOVERY", "No known server session journal; unknown runtime markers are not removed.");
        }
        var session = DeploymentFiles.Read<Session>(journal);
        if (session.FormatVersion != 2 || session.RunId == Guid.Empty || session.InstanceId != instanceId || session.DataId != lease.Identity.DataId || session.LibraryId != lease.Identity.LibraryId || session.OwnerPid <= 0 || session.OwnerStartTicks <= 0 || session.PidNamespace != new FileInfo("/proc/self/ns/pid").LinkTarget)
            throw new ServerException("HCSL-SERVER-RECOVERY", "Session identity is unknown; nothing was changed.");
        if (expectedRunId != null && expectedRunId != session.RunId) throw new ServerException("HCSL-SERVER-STALE", "Recovery run ID does not match the recorded session.");
        try
        {
            using var owner = Process.GetProcessById(session.OwnerPid);
            if (!owner.HasExited && LinuxProcessIdentity.StartTicks(session.OwnerPid) == session.OwnerStartTicks && !(session.OwnerPid == Environment.ProcessId && expectedRunId == session.RunId))
                throw new ServerException("HCSL-SERVER-BUSY", "Original session owner is still alive.");
        }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException) { }
        if (!Empty(Unit(session.RunId)).GetAwaiter().GetResult()) throw new ServerException("HCSL-SERVER-BUSY", "Service/cgroup is active or cannot be inspected. Recovery does not kill unknown processes.");
        var runtime = Path.Combine(l.RuntimeRoot, session.InstanceId.ToString("D")); LocalPaths.RejectLink(runtime);
        var marker = Marker(runtime); LocalPaths.RejectLink(marker);
        if (Directory.EnumerateFiles(runtime, ".hcsl-running.json.tmp-*").Any())
            throw new ServerException("HCSL-SERVER-RECOVERY", "Incomplete runtime marker requires inspection; nothing was removed.");
        if (File.Exists(marker) && DeploymentFiles.Read<Session>(marker) != session) throw new ServerException("HCSL-SERVER-RECOVERY", "Runtime marker and workspace journal disagree.");
        File.Delete(marker); File.Delete(journal);
    }
}
