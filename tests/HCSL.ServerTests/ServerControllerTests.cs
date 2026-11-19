using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Daemon.Workers;
using HCSL.Frontend;
using HCSL.Infrastructure.Server;

namespace HCSL.ServerTests;

/// <summary>
/// Controller tests run without a daemon: a scripted <see cref="IDaemonClient"/> stands in for the
/// pipe, so these assertions hold on any platform.
/// </summary>
public static class ServerControllerTests
{
    private static int passed;
    private static readonly WorkspacePaths Defaults = new("/settings", "/data", "/library");
    private static readonly Guid Instance = Guid.Parse("e0961218-1688-40d2-b29e-961c886f5ecb");
    private static readonly Guid Run = Guid.Parse("68fa2944-7d6b-4373-be01-368879164976");

    private static ServerSnapshot Snapshot(ServerState state, string code = "", bool active = false, Guid? run = null,
        string? map = null, params string[] lines) => new(
            run, run is null ? null : Instance, state, active, code, map is not null,
            active ? false : true, null, lines.Length + 1, false,
            lines.Select((text, index) => new ServerLogLine(index + 1, text)).ToArray(),
            Mode: "LoopbackLanValidation", EffectiveVac: false, Map: map);

    // A live run as the daemon reports it: the log page is what the paging tests vary.
    private static ServerSnapshot Running(long next, ServerLogLine[] lines, Guid? run = null) => new(
        run ?? Run, Instance, ServerState.Running, true, "HCSL-SERVER-RUNNING", true, false, null, next, false, lines,
        Mode: "LoopbackLanValidation", Map: "de_dust2");

    /// <summary>Refuses Start the way the daemon refuses a bad bind address: the response carries an error
    /// code instead of a snapshot, so the command never ran.</summary>
    private sealed class RefusingDaemon : IDaemonClient
    {
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            if (request.Kind == RequestKind.Hello)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: Defaults));
            var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!;
            if (command.Action == ServerAction.Start)
                throw new IOException("HCSL-SERVER-BIND: 192.168.1.99 is not an address of an active local interface.");
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                ServerSnapshotJson: JsonSerializer.Serialize(Snapshot(ServerState.Idle))));
        }
    }

    private sealed class StaticDaemon : IDaemonClient
    {
        private readonly Func<ServerCommand, ServerSnapshot> server;
        public readonly List<LocalRequest> Requests = [];
        public StaticDaemon(Func<ServerCommand, ServerSnapshot> server) => this.server = server;

        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            if (request.Kind == RequestKind.Hello)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: Defaults));
            var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!;
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                ServerSnapshotJson: JsonSerializer.Serialize(server(command))));
        }

        public ServerCommand[] Commands()
        {
            lock (Requests)
                return Requests.Where(r => r.Kind == RequestKind.Server)
                    .Select(r => JsonSerializer.Deserialize<ServerCommand>(r.ServerCommandJson!)!).ToArray();
        }

        public int ServerCalls() { lock (Requests) return Requests.Count(r => r.Kind == RequestKind.Server); }
    }

    // Plays one launch log line and stops, which is what the real runtime does before its status pages expire.
    private sealed class LoggingRuntime : IServerRuntimeService
    {
        public Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options,
            IProgress<ServerRuntimeEvent> events, CancellationToken stop)
        {
            events.Report(new(ServerState.Running, "HCSL-SERVER-RUNNING", "Created physics for de_dust2"));
            return Task.FromResult(new ServerOutcome("HCSL-SERVER-STOPPED", 0, true, true));
        }
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) => throw new NotSupportedException();
    }
    /// <summary>Preflight rejects the address the way the real runtime does once the provider validates it.</summary>
    private sealed class BindFailingRuntime : IServerRuntimeService
    {
        public Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options,
            IProgress<ServerRuntimeEvent> events, CancellationToken stop) =>
            throw new InstanceException("HCSL-INSTANCE-BIND", "Lan mode requires one specific address of a local interface; 0.0.0.0 belongs to the public mode.");
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) => throw new NotSupportedException();
    }

    private sealed class NoRuntime : IServerRuntimeService
    {
        public Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options,
            IProgress<ServerRuntimeEvent> events, CancellationToken stop) => throw new NotSupportedException();
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) => throw new NotSupportedException();
    }

    // Runs until the daemon stops it, after naming the map once. Used to check what a stopped run keeps.
    private sealed class StoppableRuntime : IServerRuntimeService
    {
        public Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options,
            IProgress<ServerRuntimeEvent> events, CancellationToken stop)
        {
            events.Report(new(ServerState.Running, "HCSL-SERVER-RUNNING", "Created physics for de_dust2"));
            try { Task.Delay(Timeout.Infinite, stop).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            return Task.FromResult(new ServerOutcome("HCSL-SERVER-STOPPED", 0, true, true));
        }
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) { }
    }

    // Records what the daemon asked the runtime for, which is where AutoDeploy must survive.
    private sealed class RecordingRuntime : IServerRuntimeService
    {
        public ServerStartOptions? Seen;
        public Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options,
            IProgress<ServerRuntimeEvent> events, CancellationToken stop)
        {
            Seen = options;
            return Task.FromResult(new ServerOutcome("HCSL-SERVER-STOPPED", 0, false, true));
        }
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) { }
    }

    private static async Task Test(string name, Func<Task> body)
    {
        try { await body(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); throw; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    public static async Task<int> RunAsync()
    {
        await Test("controller.connect.hello-then-status", async () =>
        {
            var daemon = new StaticDaemon(_ => Snapshot(ServerState.Running, "", true, Run, "de_dust2", "Created physics for de_dust2"));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            Check(controller.Connected, "controller must report connected");
            var kinds = daemon.Requests.Select(r => r.Kind).ToArray();
            Check(kinds[0] == RequestKind.Hello && kinds[1] == RequestKind.Server, "hello must precede status");
            Check(controller.State == ServerState.Running && controller.RunId == Run, "status must be applied");
            Check(controller.SummaryLine.Contains("de_dust2"), "summary must carry the concrete map name");
        });

        // A failed or finished run must not look like "the running instance": the header would then
        // keep naming the instance that just failed and the list would keep re-selecting it.
        await Test("controller.live-instance.excludes-finished-runs", async () =>
        {
            var failed = new StaticDaemon(_ => Snapshot(ServerState.Failed, "HCSL-SERVER-STALE", false, Run));
            using (var controller = new ServerController(failed, action => action()))
            {
                controller.Attach(Instance);
                await controller.ConnectAsync();
                Check(controller.State == ServerState.Failed, "failed state must be applied");
                Check(controller.LiveInstanceId is null, $"failed run must not claim liveness: {controller.LiveInstanceId}");
                Check(controller.DetailText.Contains("HCSL-SERVER-STALE"), "failure detail must stay visible without ErrorText");
            }

            var orphan = new StaticDaemon(_ => Snapshot(ServerState.RecoveryRequired, "HCSL-SERVER-RECOVERY", false, Run));
            using (var controller = new ServerController(orphan, action => action()))
            {
                controller.Attach(Instance);
                await controller.ConnectAsync();
                Check(controller.LiveInstanceId == Instance, "an orphaned session still owns its instance");
            }
        });

        await Test("controller.summary.stopped-run-keeps-map-without-ready-claim", async () =>
        {
            var daemon = new StaticDaemon(_ => Snapshot(ServerState.Stopped, "HCSL-SERVER-STOPPED", false, Run, "de_inferno"));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            controller.ConfiguredMap = "de_dust2";
            await controller.ConnectAsync();
            var summary = controller.SummaryLine;
            Check(summary.Contains("de_inferno"), $"the map of the finished run should stay visible: {summary}");
            Check(summary.Contains("上次运行"), $"a finished run's map is history and must be labelled: {summary}");
            Check(!summary.Contains("已就绪"), $"a stopped run must not claim the map is ready: {summary}");
        });

        // The map of a finished run is remembered per instance: after a stop, or after the daemon was
        // restarted and no longer knows the run, the header must not revert to "（预设）" as if nothing
        // had loaded. The remembered value is labelled instead of pretending to be live.
        await Test("controller.summary.remembers-last-map-after-stop", async () =>
        {
            var stopped = new StaticDaemon(_ => Snapshot(ServerState.Stopped, "HCSL-SERVER-STOPPED", false, Run));
            using var controller = new ServerController(stopped, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            controller.ObservedMap = "de_inferno";
            var summary = controller.SummaryLine;
            Check(summary.Contains("de_inferno"), $"remembered map missing: {summary}");
            Check(summary.Contains("上次运行"), $"remembered map must be labelled: {summary}");
            Check(!summary.Contains("已就绪"), $"remembered map must not look live: {summary}");
            controller.Attach(Guid.NewGuid());
            Check(controller.ObservedMap is null, "remembered map must not leak across instances");
            Check(!controller.SummaryLine.Contains("de_inferno"), "another instance must not inherit the map");
        });

        await Test("controller.start.carries-instance-and-timeout", async () =>
        {
            var daemon = new StaticDaemon(_ => Snapshot(ServerState.Idle));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            await controller.StartAsync(45, autoDeploy: true, allowLowMemory: true);
            var start = daemon.Commands().Last(c => c.Action == ServerAction.Start);
            Check(start.InstanceId == Instance, "start must carry the attached instance");
            Check(start.StartTimeoutSeconds == 45, "start must carry the requested timeout");
            Check(start.AutoDeploy, "explicit redeploy-and-start must reach the daemon");
            Check(start.AllowLowMemory, "explicit low-memory consent must reach the daemon");
            var request = daemon.Requests.Last();
            Check(request.Options?.Workspace == Defaults, "start must carry workspace defaults");
        });

        await Test("controller.stop.uses-observed-run", async () =>
        {
            var daemon = new StaticDaemon(_ => Snapshot(ServerState.Running, "", true, Run));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            await controller.StopAsync();
            var stop = daemon.Commands().Last(c => c.Action == ServerAction.Stop);
            Check(stop.RunId == Run && stop.InstanceId == Instance, "stop must target the run reported by the daemon");
        });

        await Test("controller.stop.without-run-stays-silent", async () =>
        {
            var daemon = new StaticDaemon(_ => Snapshot(ServerState.Idle));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            var before = daemon.Commands().Count(c => c.Action == ServerAction.Stop);
            await controller.StopAsync();
            Check(daemon.Commands().Count(c => c.Action == ServerAction.Stop) == before, "no run id means no stop command");
        });

        await Test("controller.failure.keeps-daemon-code", async () =>
        {
            var daemon = new StaticDaemon(command => command.Action == ServerAction.Start
                ? throw new IOException("HCSL-SERVER-DEPLOYMENT: no deployment for instance")
                : Snapshot(ServerState.Idle));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            await controller.StartAsync();
            Check(controller.ErrorText.StartsWith("HCSL-SERVER-DEPLOYMENT"), "the daemon code must survive");
            Check(controller.State == ServerState.Idle, "a refused start must not pretend to be starting");
        });

        await Test("controller.poll.applies-external-change", async () =>
        {
            var calls = 0;
            var daemon = new StaticDaemon(_ => Interlocked.Increment(ref calls) < 2
                ? Snapshot(ServerState.Idle)
                : Snapshot(ServerState.Running, "", true, Run, "de_mirage"));
            using var controller = new ServerController(daemon, action => action(),
                null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            Check(await WaitFor(() => controller.State == ServerState.Running), "polling must observe the daemon state");
        });

        await Test("controller.logs.pages-by-cursor", async () =>
        {
            var all = Enumerable.Range(1, 5).Select(i => new ServerLogLine(i, "line " + i)).ToArray();
            var cursors = new List<long>();
            var daemon = new StaticDaemon(command =>
            {
                if (command.Action != ServerAction.Logs)
                    return Running(6, []);
                lock (cursors) cursors.Add(command.AfterSequence);
                var page = all.Where(line => line.Sequence > command.AfterSequence).Take(2).ToArray();
                var next = page.Length == 0 ? command.AfterSequence + 1 : page[^1].Sequence + 1;
                return Running(next, page);
            });
            using var controller = new ServerController(daemon, action => action(),
                null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            controller.SetLogsVisible(true);
            Check(await WaitFor(() => controller.LogLineCount == 5), "log pages must accumulate every sequence exactly once");
            Check(controller.LogCursor == 5, $"cursor must follow the newest sequence, got {controller.LogCursor}");
            Check(controller.LogText.Replace(Environment.NewLine, "|") == "line 1|line 2|line 3|line 4|line 5",
                "log text must stay ordered and deduplicated");
            lock (cursors)
                Check(cursors.Count > 1 && cursors.Zip(cursors.Skip(1)).All(pair => pair.Second > pair.First),
                    "each page request must advance the cursor");
        });

        await Test("controller.logs.new-run-clears-the-ring", async () =>
        {
            var other = Guid.Parse("7a7c831a-5d0a-4311-8cec-87fab66facf0");
            var run = Run;
            var daemon = new StaticDaemon(command =>
            {
                var page = new[] { new ServerLogLine(1, run == Run ? "first run" : "second run") };
                if (command.Action != ServerAction.Logs) return Running(2, [], run);
                return command.AfterSequence >= 1 ? Running(2, [], run) : Running(2, page, run);
            });
            using var controller = new ServerController(daemon, action => action(),
                null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            controller.SetLogsVisible(true);
            Check(await WaitFor(() => controller.LogText.Contains("first run")), "the first run's line must arrive");
            run = other;
            Check(await WaitFor(() => controller.LogText.Contains("second run")), "a new run must be paged from its own cursor");
            Check(!controller.LogText.Contains("first run"), "a new run must not mix with the previous run's lines");
        });

        await Test("controller.logs.ring-is-bounded", async () =>
        {
            var all = Enumerable.Range(1, 2100).Select(i => new ServerLogLine(i, "line " + i)).ToArray();
            var daemon = new StaticDaemon(command =>
            {
                if (command.Action != ServerAction.Logs) return Running(2101, []);
                var page = all.Where(line => line.Sequence > command.AfterSequence).Take(300).ToArray();
                var next = page.Length == 0 ? command.AfterSequence + 1 : page[^1].Sequence + 1;
                return Running(next, page);
            });
            using var controller = new ServerController(daemon, action => action(),
                null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            controller.SetLogsVisible(true);
            Check(await WaitFor(() => controller.LogCursor == 2100, 6000), "the cursor must reach the newest line");
            Check(controller.LogLineCount <= 2000, "the local ring must never exceed its capacity");
            // The view starts at the tail, so older lines only arrive when asked for; filling the ring
            // is exactly what the local cap has to bound.
            // The poll loop shares the single in-flight slot, so an attempt can come back Busy; keep asking
            // until the view either fills or reports that nothing older exists.
            var deadline = Environment.TickCount64 + 15000;
            while (controller.CanLoadEarlier && Environment.TickCount64 < deadline)
            {
                await controller.LoadEarlierAsync();
                await Task.Delay(5);
            }
            Check(controller.LogLineCount == 2000, $"the ring must stop at its capacity, got {controller.LogLineCount}");
            Check(controller.LogFirst == 101, $"the cap keeps the newest lines, got {controller.LogFirst}");
            Check(controller.LogsTruncated, "dropping old lines must mark the log as truncated");
        });

        await Test("controller.logs.tail-first-then-earlier", async () =>
        {
            const int total = 1000, ringStart = 200, pageSize = 128;
            var cursors = new List<long>();
            var daemon = new StaticDaemon(command =>
            {
                if (command.Action != ServerAction.Logs) return Running(total, []);
                lock (cursors) cursors.Add(command.AfterSequence);
                var from = (int)Math.Max(ringStart, command.AfterSequence + 1);
                var truncated = from > command.AfterSequence + 1;
                var page = Enumerable.Range(from, Math.Max(0, Math.Min(pageSize, total - from + 1)))
                    .Select(s => new ServerLogLine(s, "line " + s)).ToArray();
                return Running(total, page) with { HistoryTruncated = truncated };
            });
            using var controller = new ServerController(daemon, action => action(),
                null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            controller.SetLogsVisible(true);
            Check(await WaitFor(() => controller.LogLineCount > 0), "the first page must arrive without polling");
            Check(controller.LogFirst == 745, $"the first request must start at the tail, got {controller.LogFirst}");
            Check(controller.LogCursor == 872, $"the forward cursor must follow the tail page, got {controller.LogCursor}");
            Check(controller.CanLoadEarlier, "earlier lines must still be reachable");

            await controller.LoadEarlierAsync();
            Check(controller.LogFirst == 490, $"loading earlier must move the first line back, got {controller.LogFirst}");
            var sequences = controller.LogLines.Select(line => line.Sequence).ToArray();
            Check(sequences.SequenceEqual(sequences.OrderBy(s => s)), "earlier lines must stay ordered");
            Check(sequences.Distinct().Count() == sequences.Length, "earlier lines must not be duplicated");
            Check(controller.LogCursor == 872, "reading older lines must not move the forward cursor");

            for (var step = 0; step < 10 && controller.CanLoadEarlier; step++) await controller.LoadEarlierAsync();
            Check(controller.LogFirst == ringStart, $"the view must stop at the ring start, got {controller.LogFirst}");
            Check(!controller.CanLoadEarlier, "an exhausted ring must not offer more history");
            Check(controller.LogLines.Select(line => line.Sequence).Distinct().Count() == controller.LogLines.Count,
                "repeated older reads must not duplicate lines");
            Check(cursors.All(cursor => cursor >= 0 && cursor <= total), "older reads must stay inside the run's sequence range");
        });

        await Test("controller.dispose.stops-polling", async () =>
        {
            var daemon = new StaticDaemon(_ => Snapshot(ServerState.Running, "", true, Run));
            var controller = new ServerController(daemon, action => action(),
                null, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            Check(await WaitFor(() => daemon.ServerCalls() > 1), "polling must be running before dispose");
            controller.Dispose();
            await Task.Delay(120);
            var after = daemon.ServerCalls();
            await Task.Delay(200);
            Check(daemon.ServerCalls() == after, "dispose must stop polling");
            await Task.CompletedTask;
        });

        await Test("controller.cleanup-unconfirmed-is-not-stopped", async () =>
        {
            var daemon = new StaticDaemon(_ => new ServerSnapshot(Run, Instance, ServerState.RecoveryRequired,
                false, "HCSL-SERVER-RECOVERY", false, false, null, 4, false,
                [new ServerLogLine(1, "HCSL-SERVER-STOPPED-FORCED")]));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            await controller.ConnectAsync();
            controller.English = true;
            Check(controller.DetailText.Contains("HCSL-SERVER-RECOVERY"), "recovery must surface its code");
            Check(controller.Severity == ServerSeverity.Bad, "recovery must be flagged as bad");
            Check(controller.CanStart == false, "recovery must block a plain start");
            Check(controller.CanRecover, "recovery must offer the recover action");
        });

        await Test("statusText.map-from-log-takes-last", () =>
        {
            var map = ServerStatusText.MapFromLog([
                new ServerLogLine(1, "Created physics for de_dust2"),
                new ServerLogLine(2, "Created physics for de_mirage"),
            ]);
            Check(map == "de_mirage", $"expected de_mirage, got {map}");
            Check(ServerStatusText.MapFromLog(null) is null, "no log means no map");
            return Task.CompletedTask;
        });

        await Test("statusText.summary-names-map-and-state", () =>
        {
            var ready = ServerStatusText.SummaryLine(false, ServerState.Running, Run, "de_dust2", "de_dust2", true, false, 412, null);
            Check(ready.Contains("运行中") && ready.Contains("de_dust2") && ready.Contains("已就绪"), ready);
            var preset = ServerStatusText.SummaryLine(true, ServerState.Idle, null, null, "de_mirage", false, false, 0, null);
            Check(preset.Contains("Idle") && preset.Contains("de_mirage") && preset.Contains("preset"), preset);
            var loading = ServerStatusText.SummaryLine(false, ServerState.LoadingMap, Run, "de_dust2", "de_dust2", false, false, 12, null);
            Check(loading.Contains("（加载中）") && !loading.Contains("已就绪") && !loading.Contains("预设"), loading);
            var named = ServerStatusText.SummaryLine(false, ServerState.Running, Run, "de_dust2", "de_dust2", true, false, 412, null);
            Check(named.Split("地图已就绪").Length == 1, "ready flag must not be repeated next to the map: " + named);
            return Task.CompletedTask;
        });

        await Test("daemon.status-carries-the-map-after-log-page-gone", async () =>
        {
            Check(ServerOperations.MapFrom("Created physics for de_dust2") == "de_dust2", "map marker not parsed");
            Check(ServerOperations.MapFrom("Created physics for ") is null, "empty map accepted");
            Check(ServerOperations.MapFrom("Created physics for ../etc") is null, "unsafe map accepted");
            Check(ServerOperations.MapFrom("SV: 64 player server started") is null, "unrelated line parsed");
            await using var servers = new ServerOperations(new LoggingRuntime(), null);
            servers.Start(new WorkspaceRequest("/tmp/s", "/tmp/d", "/tmp/l"), new ServerStartOptions(Guid.NewGuid()));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            ServerSnapshot snapshot;
            do { snapshot = servers.Snapshot(); if (snapshot.Map is not null) break; await Task.Delay(20); }
            while (DateTime.UtcNow < deadline);
            Check(snapshot.Map == "de_dust2", $"map lost from a plain status reply: {snapshot.Map}");
            Check(snapshot.Lines.Length == 0, "a plain status reply must stay page-free");
        });

        await Test("statusText.labels-cover-every-state", () =>
        {
            foreach (var state in Enum.GetValues<ServerState>())
            {
                var zh = ServerStatusText.StateLabel(state, false);
                var en = ServerStatusText.StateLabel(state, true);
                Check(zh.Length > 0 && en.Length > 0 && zh != en, $"labels missing for {state}");
            }
            Check(ServerStatusText.CodeHint("HCSL-SERVER-DEPLOYMENT", true) is { Length: > 0 }, "known codes need hints");
            Check(ServerStatusText.CodeHint(null, true) is null, "no code means no hint");
            return Task.CompletedTask;
        });

        await Test("daemon.start.carries-autodeploy-to-the-runtime", async () =>
        {
            var runtime = new RecordingRuntime();
            await using var servers = new ServerOperations(runtime);
            var workspace = new WorkspaceRequest("/settings", "/data", "/library");
            servers.Start(workspace, new(Instance, 60, AutoDeploy: true, AllowLowMemory: true));
            for (var i = 0; i < 200 && runtime.Seen is null; i++) await Task.Delay(25);
            Check(runtime.Seen is not null, "the runtime must be invoked");
            Check(runtime.Seen!.AutoDeploy, "redeploy-and-start must reach the runtime options");
            Check(runtime.Seen.AllowLowMemory, "low-memory consent must reach the runtime options");
            Check(runtime.Seen.StartTimeoutSeconds == 60, "timeout must reach the runtime options");
        });

        // Stopping ends the run, not the header: the map of the run that just ended must stay readable,
        // otherwise the header silently falls back to the preset's map and looks like it loaded nothing.
        await Test("daemon.stop-keeps-the-map-of-the-finished-run", async () =>
        {
            await using var servers = new ServerOperations(new StoppableRuntime());
            var workspace = new WorkspaceRequest("/settings", "/data", "/library");
            var started = servers.Start(workspace, new(Instance, 120));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            ServerSnapshot snapshot;
            do { await Task.Delay(25); snapshot = servers.Snapshot(); }
            while (snapshot.Map is null && DateTime.UtcNow < deadline);
            Check(snapshot.Map == "de_dust2", $"running snapshot lost the map: {snapshot.Map}");
            servers.Stop(started.RunId);
            deadline = DateTime.UtcNow.AddSeconds(10);
            do { await Task.Delay(25); snapshot = servers.Snapshot(); }
            while (snapshot.Active && DateTime.UtcNow < deadline);
            Check(!snapshot.Active && snapshot.State == ServerState.Stopped, $"expected stopped, got {snapshot.State}");
            Check(snapshot.Map == "de_dust2", $"stopped snapshot lost the map: {snapshot.Map}");
        });

        await Test("daemon.startup.orphan-session-marker-is-recovery", async () =>
        {
            var marker = Path.Combine(Path.GetTempPath(), "hcsl-orphan-" + Guid.NewGuid().ToString("N") + ".json");
            var orphanRun = Guid.NewGuid();
            var orphanInstance = Guid.NewGuid();
            File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(new
            { FormatVersion = 2, RunId = orphanRun, InstanceId = orphanInstance, OwnerPid = 4242, PidNamespace = "4026531836" }));
            try
            {
                await using var servers = new ServerOperations(new NoRuntime(), marker);
                var snapshot = servers.Snapshot();
                Check(snapshot.State == ServerState.RecoveryRequired, $"expected recovery, got {snapshot.State}");
                Check(snapshot.Code == "HCSL-SERVER-RECOVERY", "recovery code must survive");
                // A fresh frontend can only act on the orphan if the daemon surfaces its identity.
                Check(snapshot.InstanceId == orphanInstance, $"orphan instance not surfaced: {snapshot.InstanceId}");
                Check(snapshot.RunId == orphanRun, $"orphan run not surfaced: {snapshot.RunId}");
                Check(!snapshot.Active, "an orphaned session is not an active run");
                await using var clean = new ServerOperations(new NoRuntime());
                Check(clean.Snapshot().State == ServerState.Idle, "a daemon without a marker starts idle");
            }
            finally { File.Delete(marker); }
        });

        // The daemon dies when idle, so the map of a finished run only survives on disk. A fresh daemon
        // must answer that recorded map for the instance the frontend attaches to, labelled as history.
        await Test("daemon.fresh-daemon-reports-the-recorded-last-map", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "hcsl-lastrun-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, Instance.ToString("D")));
            try
            {
                ServerLastRunStore.Write(root, Instance, Run, "de_inferno");
                ServerLastRunStore.Write(root, Instance, Run, "../etc");
                Check(ServerLastRunStore.Read(root, Instance)?.Map == "de_inferno", "an unsafe map must not replace the record");
                Check(ServerLastRunStore.Read(root, Guid.NewGuid()) is null, "a record belongs to one instance");
                await using var servers = new ServerOperations(new NoRuntime(), null, root);
                var unattached = servers.Snapshot();
                Check(unattached.Map is null, $"no instance means no map: {unattached.Map}");
                var status = servers.Snapshot(hint: Instance);
                Check(status.Map == "de_inferno", $"recorded map missing: {status.Map}");
                Check(status.MapHistorical, "a recorded map is history, not a live observation");
                Check(!status.Active && status.State == ServerState.Idle, "reading history must not invent a run");
                Check(servers.Snapshot(hint: Guid.NewGuid()).Map is null, "another instance must not inherit the map");
            }
            finally { Directory.Delete(root, true); }
        });

        await Test("daemon.log-fallback-answers-runs-before-the-record", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "hcsl-lastlog-" + Guid.NewGuid().ToString("N"));
            var logs = Path.Combine(root, Instance.ToString("D"), "logs");
            Directory.CreateDirectory(logs);
            try
            {
                File.WriteAllLines(Path.Combine(logs, "server-1.log"),
                [
                    "CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)",
                    "Created physics for de_mirage",
                    "Created physics for ../../etc",
                ]);
                await using var servers = new ServerOperations(new NoRuntime(), null, root);
                var status = servers.Snapshot(hint: Instance);
                Check(status.Map == "de_mirage", $"the newest log should answer the last map: {status.Map}");
                Check(status.MapHistorical, "a log-derived map is history");
                ServerLastRunStore.Write(root, Instance, Run, "de_inferno");
                Check(servers.Snapshot(hint: Instance).Map == "de_inferno", "the record outranks the log scan");
            }
            finally { Directory.Delete(root, true); }
        });

        await Test("controller.historical-map-reaches-the-header", async () =>
        {
            var daemon = new StaticDaemon(_ => new ServerSnapshot(null, null, ServerState.Stopped, false, "HCSL-SERVER-STOPPED",
                false, true, 0, 0, false, [], Map: "de_inferno", MapHistorical: true));
            using var controller = new ServerController(daemon, action => action());
            controller.Attach(Instance);
            controller.ConfiguredMap = "de_dust2";
            await controller.ConnectAsync();
            var summary = controller.SummaryLine;
            Check(summary.Contains("de_inferno"), $"recorded map missing from the header: {summary}");
            Check(summary.Contains("上次运行"), $"recorded map must be labelled as history: {summary}");
            Check(!summary.Contains("已就绪"), $"history must not look live: {summary}");
            var status = daemon.Commands().Last(c => c.Action == ServerAction.Status);
            Check(status.InstanceId == Instance, "status must name the attached instance so the daemon can answer its history");
        });

        // S6.1: the header must say where players connect, and the daemon's address must survive the wire.
        await Test("controller.status.exposes-the-resolved-bind-address", async () =>
        {
            var daemon = new StaticDaemon(_ => new ServerSnapshot(null, Instance, ServerState.Idle, false, "HCSL-SERVER-IDLE",
                false, true, null, 0, false, [], BindAddress: "192.168.1.20", Access: NetworkAccessMode.Lan, Mode: "Lan"));
            using var controller = new ServerController(daemon, action => action(), null, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            Check(controller.BindAddress == "192.168.1.20" && controller.Access == NetworkAccessMode.Lan, "bind address lost on the wire");
            Check(controller.Mode == "Lan", "mode must follow the access mode");
        });

        await Test("status.endpoint-text-names-the-address-and-scope", () =>
        {
            Check(ServerStatusText.EndpointLine(false, "192.168.1.20", 27015, NetworkAccessMode.Lan) == "地址：192.168.1.20:27015（局域网）",
                "zh endpoint line");
            Check(ServerStatusText.EndpointLine(true, "127.0.0.1", 27015, NetworkAccessMode.Loopback) == "Address: 127.0.0.1:27015 (this host only)",
                "en endpoint line");
            Check(ServerStatusText.ConnectHint(false, "192.168.1.20", 27015) == "客户端：connect 192.168.1.20:27015", "connect hint");
            Check(ServerStatusText.ConnectHint(true, "127.0.0.1", 27015) == "Client: connect 127.0.0.1:27015", "english connect hint");
            Check(ServerStatusText.FirewallHint(false, 27015).Contains("27015/udp"), "firewall hint must name the port");
            Check(ServerStatusText.CodeHint("HCSL-SERVER-BIND", false)?.Contains("网卡") == true, "bind hint");
            Check(ServerStatusText.CodeHint("HCSL-INSTANCE-BIND", true)?.Contains("IPv4") == true, "instance bind hint");
            return Task.CompletedTask;
        });

        await Test("controller.start.refusal-is-not-reported-as-submitted", async () =>
        {
            using var controller = new ServerController(new RefusingDaemon(), action => action(), null,
                TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60));
            controller.Attach(Instance);
            await controller.ConnectAsync();
            Check(!await controller.StartAsync(), "a refused start must not look submitted");
            Check(controller.ErrorText.Contains("HCSL-SERVER-BIND"), $"refusal code lost: {controller.ErrorText}");
            Check(controller.State == ServerState.Idle, "a refusal must not invent a new state");
        });

        await Test("daemon.preflight.keeps-the-instance-bind-code", async () =>
        {
            await using var operations = new ServerOperations(new BindFailingRuntime());
            operations.Start(new WorkspaceRequest("/settings", "/data", "/library"), new ServerStartOptions(Instance));
            await WaitFor(() => operations.Snapshot().State is ServerState.Failed or ServerState.RecoveryRequired);
            var snapshot = operations.Snapshot();
            // The generic PREFLIGHT replacement hid which address was wrong, which is the one thing the
            // operator can fix without reading logs.
            Check(snapshot.Code == "HCSL-INSTANCE-BIND", $"instance bind code lost: {snapshot.Code}");
            Check(snapshot.Detail?.Contains("0.0.0.0") == true, "bind detail lost");
        });

        await Test("daemon.payload.stale-only-when-the-bundled-binary-is-newer", async () =>
        {
            var older = DateTime.UtcNow.AddMinutes(-5);
            var newer = DateTime.UtcNow;
            Check(DaemonPayload.IsStale(older, newer), "an older daemon must be replaced");
            Check(!DaemonPayload.IsStale(newer, older), "a newer daemon must be kept");
            Check(!DaemonPayload.IsStale(older, older), "equal stamps must be kept");
            Check(!DaemonPayload.IsStale(null, newer), "an unreported build must never be replaced");
            Check(!DaemonPayload.IsStale(older, null), "a missing payload must never replace anything");
            Check(DaemonPayload.Parse(older.ToString("O")) == older, "stamps round-trip");
            Check(DaemonPayload.Parse("not a stamp") is null, "garbage stamps are ignored");
            Check(DaemonPayload.Parse(null) is null, "missing stamps are ignored");
            await Task.CompletedTask;
        });

        Console.WriteLine($"PASS summary: {passed} controller and status-text tests");
        return 0;
    }
}
