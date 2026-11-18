using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Server;

public sealed partial class SystemdServerRuntime : IServerRuntimeService
{
    private readonly string assembly;
    private readonly Func<WorkspaceRequest, ServerStartOptions, CancellationToken, Task<ServerLaunch>> prepare;
    private readonly int memoryMiB;
    public SystemdServerRuntime(string gateAssembly) : this(gateAssembly, ServerPreparation.PrepareAsync, 2560) { }
    internal SystemdServerRuntime(string gateAssembly, Func<WorkspaceRequest, ServerStartOptions, CancellationToken, Task<ServerLaunch>> prepare, int memoryMiB = 64)
    { assembly = Path.GetFullPath(gateAssembly); this.prepare = prepare; this.memoryMiB = memoryMiB; }
    internal sealed record Session(int FormatVersion, Guid RunId, Guid InstanceId, Guid DataId, Guid LibraryId, int OwnerPid, long OwnerStartTicks, string PidNamespace);
    private static string Unit(Guid id) => "hcsl-server-" + id.ToString("N") + ".service";
    internal static bool IsWorkshopMap(string? map) =>
        map is not null && (map.StartsWith("workshop/", StringComparison.Ordinal) || map.StartsWith("maps/workshop/", StringComparison.Ordinal));

    /// <summary>
    /// A collection launch starts on a built-in seed map. Once CS2 accepts the collection it
    /// switches the active mapgroup to "workshop" and may report the workshop map by its bare
    /// internal name; that map must no longer be mistaken for the seed.
    /// </summary>
    internal static bool IsSeedOnly(bool collectionStart, bool workshopContext, string? map) =>
        collectionStart && !workshopContext && !IsWorkshopMap(map);
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
    /// <summary>Deploy-only path for the GUI/CLI: no CS2 process, no systemd unit, no console.</summary>
    public Task<DeploymentReceipt> DeployAsync(WorkspaceRequest workspace, Guid instanceId, IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new ServerException("HCSL-SERVER-PLATFORM", "Only Linux x64 is implemented.");
        if (instanceId == Guid.Empty) throw new ServerException("HCSL-SERVER-ID", "Instance ID is required.");
        var mode = RuntimeDeploymentService.ProbeCopyMode(Path.Combine(workspace.LibraryRoot, "runtime"));
        events?.Report(new(ServerState.Preparing, "HCSL-SERVER-DEPLOYING"));
        var progress = new Progress<DeploymentEvent>(e => events?.Report(new(ServerState.Preparing,
            "HCSL-SERVER-DEPLOYING", e.Stage + " " + e.FilesCompleted + "/" + e.TotalFiles)));
        return Task.Run(() => new RuntimeDeploymentService().Deploy(workspace, new(instanceId, mode), progress, ct), ct);
    }

    /// <summary>Read-only deployment plan: same validation as deploy, but no runtime or persistent write.</summary>
    public Task<DeploymentPlan> PlanDeploymentAsync(WorkspaceRequest workspace, Guid instanceId, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new ServerException("HCSL-SERVER-PLATFORM", "Only Linux x64 is implemented.");
        if (instanceId == Guid.Empty) throw new ServerException("HCSL-SERVER-ID", "Instance ID is required.");
        var mode = RuntimeDeploymentService.ProbeCopyMode(Path.Combine(workspace.LibraryRoot, "runtime"));
        return Task.Run(() => new RuntimeDeploymentService().Plan(workspace, new(instanceId, mode), ct), ct);
    }

    /// <summary>Recover an interrupted deployment without deploying or starting anything.</summary>
    public Task<string> RecoverDeploymentAsync(WorkspaceRequest workspace, Guid instanceId, IProgress<ServerRuntimeEvent>? events = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new ServerException("HCSL-SERVER-PLATFORM", "Only Linux x64 is implemented.");
        if (instanceId == Guid.Empty) throw new ServerException("HCSL-SERVER-ID", "Instance ID is required.");
        events?.Report(new(ServerState.Preparing, "HCSL-SERVER-DEPLOY-RECOVERING"));
        return Task.Run(() => new RuntimeDeploymentService().Recover(workspace, instanceId), ct);
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
        ConsoleSession? console = null;
        var networkReadiness = new ServerNetworkReadiness();
        var parserLock = new object(); var maps = new ServerMapTracker(launch.ReadinessProfile); bool publishedReady = false, everReady = false, mapWaitReported = false, seedReadyReported = false; long logBytes = 0, mapAt = 0, mapWaitSince = 0, downloadSince = 0;
        var collectionStart = launch.Preset?.MapKind == ServerMapKind.WorkshopCollection;
        var workshopContext = false; var workshopContextSignal = false;
        // A first Workshop collection download can exceed the normal 120-second start budget;
        // the seed map proves the server booted but is deliberately not published as final readiness.
        var startupLimit = collectionStart ? Math.Max(options.StartTimeoutSeconds, 600) : options.StartTimeoutSeconds;
        string? emptyChangeTarget = null; long emptyChangeAt = 0; bool emptyChangeRecoveryTried = false;
        var logPath = Path.Combine(l.InstancesRoot, options.InstanceId.ToString("D"), "logs", "server-" + runId.ToString("N") + ".log");
        LocalPaths.RejectLink(logPath);
        using var disk = new StreamWriter(LocalPaths.CreateFile(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        void Line(string raw)
        {
            var receivedAt = DateTimeOffset.UtcNow;
            lock (parserLock)
            {
                if (raw.StartsWith("HCSL_CONSOLE_ACK:", StringComparison.Ordinal)
                    && Guid.TryParse(raw[17..], out var ack) && console?.Acks.TryGetValue(ack, out var confirmation) == true)
                { confirmation.TrySetResult(true); return; }
                // SteamID only exists in the log stream; status never prints it. Both lines are cheap prefix checks.
                if (PlayerRosterRules.TrySteamNet(raw, out var netId, out var netAddress)) console?.Identities.BindAddress(netAddress, netId);
                else if (PlayerRosterRules.TryValidated(raw, out var validatedId, out var validatedUser)) console?.Identities.BindUser(validatedUser, validatedId);
                var text = Regex.Replace(raw, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", "");
                text = new string(text.Where(c => !char.IsControl(c)).ToArray());
                if (text == "HCSL_SERVER_GATE_READY") { ready.TrySetResult(); return; }
                if (text == "HCSL_SERVER_CHILD_STARTED") { events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-PROCESS-STARTED")); return; }
                var protocolText = text; // readiness parsing must not depend on secret redaction
                if (collectionStart && (protocolText.Contains("Setting mapgroup to 'workshop'", StringComparison.Ordinal)
                    || protocolText.Contains("host_workshop_collection", StringComparison.Ordinal)))
                {
                    workshopContextSignal = true;
                    // Do not let an echoed startup argument mark the seed itself as the collection map.
                    if (seedReadyReported) workshopContext = true;
                }
                networkReadiness = networkReadiness.Observe(protocolText);
                text = launch.Redact(text);
                if (Regex.IsMatch(text, @"(?i)password|token|steamaccount|gslt|secret")) text = "[redacted sensitive configuration]";
                if (text.Length > 2048) text = text[..2048] + " [truncated]";
                if (logBytes < 8 * 1024 * 1024) { disk.WriteLine(text); logBytes += Encoding.UTF8.GetByteCount(text) + 1; }
                var loading = maps.Observe(protocolText);
                var emptyChange = maps.ObserveEmptyChangeLevel(protocolText);
                if (maps.Progress.Phase == ServerMapPhase.DownloadObserved) downloadSince = downloadSince == 0 ? Stopwatch.GetTimestamp() : downloadSince;
                else downloadSince = 0;
                events.Report(new(ServerState.Starting, "HCSL-SERVER-LOG", text, NetworkReadiness: networkReadiness, MapProgress: maps.Progress, LogReceivedAt: receivedAt));
                if (emptyChange is not null)
                {
                    // CS2 may queue Changelevel () after printing the intended map when the
                    // end-match vote did not write nextlevel. Surface it immediately instead
                    // of waiting for the generic 120-second readiness timeout.
                    emptyChangeTarget = emptyChange; emptyChangeAt = Stopwatch.GetTimestamp(); emptyChangeRecoveryTried = false;
                    maps.BeginTransition(); mapLoaded = false; publishedReady = false; mapAt = 0; seedReadyReported = false;
                    mapWaitSince = Stopwatch.GetTimestamp(); mapWaitReported = false;
                    events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-MAP-EMPTY", MapProgress: maps.Progress));
                }
                if (loading)
                {
                    emptyChangeTarget = null; emptyChangeAt = 0;
                    mapLoaded = false; publishedReady = false; mapAt = 0; seedReadyReported = false;
                    mapWaitSince = Stopwatch.GetTimestamp(); mapWaitReported = false;
                    events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-MAP-LOADING"));
                }
                if (!mapLoaded && maps.Complete)
                {
                    // A collection launch seeds the game server with a built-in map. That seed is not the
                    // collection result: publishing it as Ready would hide a stalled/failed workshop download.
                    if (IsSeedOnly(collectionStart, workshopContext, maps.Map))
                    {
                        if (!seedReadyReported)
                        {
                            seedReadyReported = true; mapWaitSince = 0; mapWaitReported = false;
                            if (workshopContextSignal) workshopContext = true;
                            events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-MAP-SEED", MapProgress: maps.Progress));
                        }
                    }
                    else { mapAt = Stopwatch.GetTimestamp(); mapLoaded = true; }
                }
            }
        }
        void RecoveryFailed()
        {
            lock (parserLock)
            {
                // A late rejection must not downgrade a map that already became ready.
                if (maps.Complete || maps.Progress.Phase == ServerMapPhase.Ready) return;
                maps.Unconfirmed(); mapWaitReported = true;
                events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-MAP-RECOVERY-FAILED", MapProgress: maps.Progress));
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
            events.Report(new(ServerState.Starting, "HCSL-SERVER-STARTING", Preset: launch.Preset));
            process = Process.Start(start)!;
            console = new ConsoleSession(process, stop, launch.Redact)
            {
                GamePort = launch.GamePort,
                // Per-run random password: kept in memory only, never logged, never sent to a frontend.
                RconPassword = "hcsl-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
            };
            consoleSessions[runId] = console;
            stdout = Pump(process.StandardOutput); stderr = Pump(process.StandardError);
            var exited = process.WaitForExitAsync(); var clock = Stopwatch.StartNew(); bool released = false;
            while (!exited.IsCompleted)
            {
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("Server output could not be drained.");
                if (stop.IsCancellationRequested) { code = "HCSL-SERVER-STOPPED"; break; }
                if (ready.Task.IsCompleted && !released)
                {
                    launch.ReleasePorts();
                    var command = new ServerGateCommand(session.OwnerPid, session.OwnerStartTicks, session.PidNamespace, launch.Executable, launch.Arguments, Path.Combine(launch.Runtime, "game"), memoryMiB * 1024L * 1024, runId);
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)); await process.StandardInput.FlushAsync(); released = true;
                    // The RCON password is applied lazily on the first roster query, so startup never consumes
                    // the console rate-limit window that an operator command may need right after map load.
                }
                string? readyMap = null;
                lock (parserLock)
                {
                    if (downloadSince != 0 && Stopwatch.GetElapsedTime(downloadSince).TotalSeconds >= 120)
                    {
                        downloadSince = 0; maps.Unconfirmed();
                        events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-LOG", "HCSL: Workshop download remains unconfirmed after 120s; inspect logs.", MapProgress: maps.Progress));
                    }
                    if (everReady && !publishedReady && mapWaitSince != 0 && !mapWaitReported && Stopwatch.GetElapsedTime(mapWaitSince).TotalSeconds >= 120)
                    {
                        mapWaitReported = true;
                        maps.Unconfirmed();
                        events.Report(new(ServerState.LoadingMap, "HCSL-SERVER-MAP-UNCONFIRMED", MapProgress: maps.Progress));
                    }
                    if (mapLoaded && !publishedReady && Stopwatch.GetElapsedTime(mapAt).TotalSeconds >= 2)
                    {
                        publishedReady = everReady = true; readyMap = maps.Map; maps.MarkReady();
                        events.Report(new(ServerState.Running, "HCSL-SERVER-MAP-READY", Map: readyMap, MapProgress: new(ServerMapPhase.Ready)));
                    }
                }
                string? recoverMap = null;
                lock (parserLock)
                {
                    if (emptyChangeTarget is not null && emptyChangeAt != 0 && !emptyChangeRecoveryTried
                        && Stopwatch.GetElapsedTime(emptyChangeAt) >= TimeSpan.FromMilliseconds(750))
                    { emptyChangeRecoveryTried = true; recoverMap = emptyChangeTarget; emptyChangeTarget = null; }
                }
                if (recoverMap is not null)
                {
                    var recovery = ServerMapRecovery.Command(recoverMap, launch.Preset?.MapKind ?? ServerMapKind.Standard);
                    if (recovery is null) RecoveryFailed();
                    else
                    {
                        try
                        {
                            var receipt = await SendConsoleAsync(new(runId, Guid.NewGuid(), recovery), stop);
                            if (receipt.Delivery != ConsoleDelivery.Written) RecoveryFailed();
                        }
                        catch (Exception e) when (e is IOException or OperationCanceledException or ServerException)
                        { RecoveryFailed(); }
                    }
                }
                if (readyMap is not null)
                {
                    // Recording must never disturb a run that already reached readiness.
                    try { ServerLastRunStore.Write(l.InstancesRoot, options.InstanceId, runId, readyMap); }
                    catch (Exception) { }
                }
                if (!everReady && clock.Elapsed.TotalSeconds >= startupLimit) { code = "HCSL-SERVER-START-TIMEOUT"; break; }
                await Task.Delay(100);
            }
            if (!exited.IsCompleted)
            {
                events.Report(new(ServerState.Stopping, code));
                using var stopWrite = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await console.Writer.WaitAsync(stopWrite.Token);
                    try { await process.StandardInput.WriteLineAsync("STOP".AsMemory(), stopWrite.Token); await process.StandardInput.FlushAsync(stopWrite.Token); }
                    finally { console.Writer.Release(); }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
                try { await exited.WaitAsync(TimeSpan.FromSeconds(7)); } catch (TimeoutException) { }
            }
            if (process.HasExited) exit = process.ExitCode;
            if (code == "HCSL-SERVER-STOPPED" && exit != 0) code = "HCSL-SERVER-STOPPED-FORCED";
        }
        catch (Exception) { code = "HCSL-SERVER-PROCESS"; }
        finally
        {
            consoleSessions.TryRemove(runId, out _);
            if (console is not null) foreach (var ack in console.Acks.Values) ack.TrySetCanceled();
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
