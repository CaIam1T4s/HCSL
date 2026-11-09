using System.Diagnostics;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;
namespace HCSL.Daemon.Workers;

public sealed class ServerOperations : IAsyncDisposable
{
    private readonly IServerRuntimeService service;
    private readonly object sync = new();

    /// <param name="sessionMarker">Library session record; a leftover one means the previous daemon died
    /// with a server possibly alive, so a fresh daemon must not report an idle workspace.</param>
    /// <param name="instancesRoot">Instance directory; the last loaded map of a finished run is recorded there.</param>
    public ServerOperations(IServerRuntimeService service, string? sessionMarker = null, string? instancesRoot = null)
    {
        this.service = service;
        this.instancesRoot = instancesRoot;
        if (sessionMarker is null || !File.Exists(sessionMarker)) return;
        // The record names the instance and run that must be recovered, so a fresh frontend can act on it.
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sessionMarker));
            if (document.RootElement.TryGetProperty("InstanceId", out var known) && known.TryGetGuid(out var id)) instanceId = id;
            if (document.RootElement.TryGetProperty("RunId", out var run) && run.TryGetGuid(out var recorded)) runId = recorded;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        state = ServerState.RecoveryRequired;
        code = "HCSL-SERVER-RECOVERY";
        clean = false;
        detail = "A previous server session was not closed; recover before starting.";
    }
 private bool closing, active; private Guid? runId, instanceId;
    private ServerState state; private string code = "HCSL-SERVER-IDLE"; private bool loaded, clean = true; private int? exit; private string? detail, map;
    private ServerNetworkReadiness networkReadiness = new();
    private ServerPresetIdentity? preset;
    private ServerMapProgress mapProgress = new(ServerMapPhase.None);
    private long? networkWaitSince;
    private long sequence, lastBusy = Stopwatch.GetTimestamp(); private readonly Queue<ServerLogLine> logs = new();
    // A page must stay well inside the 64 KiB IPC frame even with long lines, and the ring must be
    // large enough that a client paging at its poll interval does not fall behind a chatty server.
    private const int LogPageSize = 128;
    private const int LogCapacity = 2048;
    private readonly string? instancesRoot;
    private Guid lastRunInstance; private DateTime lastRunStamp; private string? lastRunMap;
    private CancellationTokenSource? cancellation; private Task? pending;
    public bool CanIdle(TimeSpan timeout) { lock (sync) return !active && Stopwatch.GetElapsedTime(lastBusy) >= timeout; }
    public void CloseAdmission() { lock (sync) closing = true; }
    public bool Active { get { lock (sync) return active; } }
    public ServerSnapshot Snapshot(long? after = null, Guid? expected = null, Guid? hint = null)
    {
        lock (sync)
        {
            if (expected != null && expected != runId) throw new ServerException("HCSL-SERVER-STALE", "Log cursor belongs to another run.");
            if (after is < 0 || after > sequence) throw new ServerException("HCSL-SERVER-CURSOR", "Log cursor is outside this run; refresh status after a new run.");
            var page = after is { } cursor ? logs.Where(l => l.Sequence > cursor).Take(LogPageSize).ToArray() : [];
            var addresses = LocalServerAddresses.Discover();
            var reported = map; var historical = false;
            if (reported is null && !active && hint is { } wanted && wanted != Guid.Empty) { reported = LastRunMap(wanted); historical = reported is not null; }
            return new(runId, instanceId, state, active, code, loaded, clean, exit, after == null ? sequence : page.LastOrDefault()?.Sequence ?? after.Value,
                after != null && logs.Count > 0 && logs.Peek().Sequence > after + 1, page, detail,
                Mode: "Direct", Map: reported, MapHistorical: historical,
                BindAddress: addresses[0], Access: addresses[0] == "127.0.0.1" ? NetworkAccessMode.Loopback : NetworkAccessMode.Lan,
                ConnectionAddresses: addresses, Preset: preset, MapProgress: active ? mapProgress : new(ServerMapPhase.None),
                NetworkReadiness: active && state is ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap or ServerState.Running
                    ? networkReadiness.WithElapsed(networkWaitSince is { } since ? Stopwatch.GetElapsedTime(since) : TimeSpan.Zero) : new());
        }
    }
    /// <summary>Map of the last run that reached readiness, read from the instance directory on demand.
    /// Cached by file timestamp so a 700ms status poll does not re-read the record.</summary>
    private string? LastRunMap(Guid instance)
    {
        if (instancesRoot is null) return null;
        var stamp = ServerLastRunStore.Stamp(instancesRoot, instance);
        if (instance != lastRunInstance || stamp != lastRunStamp)
        {
            lastRunMap = ServerLastRunStore.ReadMap(instancesRoot, instance, out _);
            lastRunInstance = instance; lastRunStamp = stamp;
        }
        return lastRunMap;
    }
    private void Admit(Guid id)
    {
        if (closing || active || pending is { IsCompleted: false }) throw new ServerException("HCSL-SERVER-BUSY", "A server is active, cleaning up, or the daemon is stopping.");
        if (id == Guid.Empty) throw new ServerException("HCSL-SERVER-ID", "Instance ID is required.");
        networkReadiness = new(); networkWaitSince = null; preset = null; mapProgress = new(ServerMapPhase.None);
        cancellation?.Dispose(); cancellation = new(); runId = Guid.NewGuid(); instanceId = id;
        loaded = false; clean = false; exit = null; map = null; active = true; state = ServerState.Preparing; code = "HCSL-SERVER-PREPARING"; logs.Clear(); sequence = 0; detail = null;
    }
    public ServerSnapshot Start(WorkspaceRequest workspace, ServerStartOptions options)
    {
        if (options.StartTimeoutSeconds is < 1 or > 300) throw new ServerException("HCSL-SERVER-OPTIONS", "Startup timeout must be 1..300 seconds.");
        lock (sync)
        {
            Admit(options.InstanceId); var current = runId!.Value; var token = cancellation!.Token;
            pending = Task.Run(async () =>
            {
                try
                {
                    var outcome = await service.RunAsync(workspace, current, options, new Sink(this, current), token);
                    lock (sync)
                    {
                        code = outcome.Code; loaded = outcome.MapLoaded; clean = outcome.CleanupConfirmed; exit = outcome.ExitCode;
                        state = !clean ? ServerState.RecoveryRequired : code is "HCSL-SERVER-STOPPED" or "HCSL-SERVER-EXITED" or "HCSL-SERVER-STOPPED-FORCED" ? ServerState.Stopped : ServerState.Failed;
                    }
                }
                catch (Exception e)
                {
                    lock (sync)
                    {
                        code = e switch { ServerException s => s.Code, WorkspaceException w => w.Code, DeploymentException d => d.Code, InstanceException i => i.Code, OperationCanceledException => "HCSL-SERVER-STOPPED", _ => "HCSL-SERVER-ERROR" };
                        detail = e is ServerException or WorkspaceException or DeploymentException or InstanceException ? e.Message[..Math.Min(e.Message.Length, 512)] : "Server task failed; inspect the redacted logs.";
                        // Runtime owns cleanup once it launches; thrown errors are preflight rejection or unknown failure.
                        clean = e is ServerException or WorkspaceException or DeploymentException or InstanceException or OperationCanceledException;
                        state = clean ? code == "HCSL-SERVER-STOPPED" ? ServerState.Stopped : ServerState.Failed : ServerState.RecoveryRequired;
                    }
                }
                finally { lock (sync) { active = !clean; lastBusy = Stopwatch.GetTimestamp(); } }
            });
            return Snapshot();
        }
    }
    public Task<ServerConsoleReceipt> SendConsoleAsync(ServerConsoleCommand command, CancellationToken ct = default)
    {
        ServerConsoleRules.ValidateConfirmed(command);
        lock (sync)
        {
            if (command.CommandId == Guid.Empty || command.RunId == Guid.Empty || command.RunId != runId)
                return Task.FromResult(new ServerConsoleReceipt(command.RunId, command.CommandId, ConsoleDelivery.Rejected, "HCSL-CONSOLE-STALE"));
            if (closing || !active || state is not (ServerState.Starting or ServerState.LoadingMap or ServerState.Running) || cancellation?.IsCancellationRequested == true)
                return Task.FromResult(new ServerConsoleReceipt(command.RunId, command.CommandId, ConsoleDelivery.Rejected, "HCSL-CONSOLE-STATE"));
            // Admission is synchronous up to the runtime's first await; stop marks cancellation before any later write.
            return service.SendConsoleAsync(command, ct);
        }
    }
    public ServerSnapshot Stop(Guid? expected)
    {
        lock (sync)
        {
            if (expected == null || expected != runId) throw new ServerException("HCSL-SERVER-STALE", "Stop requires the current run ID.");
            if (state == ServerState.RecoveryRequired) throw new ServerException("HCSL-SERVER-RECOVERY", "Inspect and recover the previous session first.");
            if (!active) return Snapshot();
            state = ServerState.Stopping; code = "HCSL-SERVER-STOPPING"; cancellation!.Cancel(); return Snapshot();
        }
    }
    public ServerSnapshot Recover(WorkspaceRequest workspace, Guid id, Guid? expected)
    {
        lock (sync)
        {
            if (closing || pending is { IsCompleted: false }) throw new ServerException("HCSL-SERVER-BUSY", "A task is still active.");
            if (id == Guid.Empty) throw new ServerException("HCSL-SERVER-ID", "Instance ID is required.");
            active = false; Admit(id); code = "HCSL-SERVER-RECOVERING";
            pending = Task.Run(() =>
            {
                try { service.Recover(workspace, id, expected); lock (sync) { clean = true; state = ServerState.Stopped; code = "HCSL-SERVER-RECOVERED"; } }
                catch (Exception e) { lock (sync) { clean = false; state = ServerState.RecoveryRequired; code = e is ServerException s ? s.Code : e is WorkspaceException w ? w.Code : "HCSL-SERVER-RECOVERY"; } }
                finally { lock (sync) { active = !clean; lastBusy = Stopwatch.GetTimestamp(); } }
            });
            return Snapshot();
        }
    }
    public static string? MapFrom(string line) => ServerLastRunStore.MapFrom(line);
    private sealed class Sink(ServerOperations owner, Guid run) : IProgress<ServerRuntimeEvent>
    {
        public void Report(ServerRuntimeEvent e)
        {
            lock (owner.sync)
            {
                if (owner.runId != run) return;
                if (owner.cancellation?.IsCancellationRequested != true && e.NetworkReadiness is { } readiness)
                    owner.networkReadiness = readiness;
                if (owner.cancellation?.IsCancellationRequested != true && e.MapProgress is { } progress) owner.mapProgress = progress;
                if (e.Log != null)
                {
                    var line = e.Log.Length > 512 ? e.Log[..512] : e.Log;
                    owner.logs.Enqueue(new(++owner.sequence, line));
                    while (owner.logs.Count > LogCapacity) owner.logs.Dequeue();
                    // The map name only ever appears once, in the launch log; keep it so later status
                    // replies and log pages can still name the running map.
                    if (MapFrom(line) is { } name) owner.map = name;
                    return;
                }
                if (owner.cancellation?.IsCancellationRequested == true) return;
                if (e.Preset is { } preparedPreset) owner.preset = preparedPreset;
                if (e.Map is { } actualMap && ServerLastRunStore.IsMapName(actualMap)) owner.map = actualMap;
                if (e.State == ServerState.LoadingMap) { owner.loaded = false; owner.map = null; }
                owner.state = e.State; owner.code = e.Code;
                if (e.State == ServerState.Running)
                {
                    owner.loaded = true;
                    owner.networkWaitSince ??= Stopwatch.GetTimestamp();
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task? task; lock (sync) { closing = true; cancellation?.Cancel(); task = pending; }
        if (task != null) await task; cancellation?.Dispose();
        lock (sync) if (active && !clean) throw new ServerException("HCSL-SERVER-RECOVERY", "Server cleanup was not confirmed; ownership markers were preserved.");
    }
}
