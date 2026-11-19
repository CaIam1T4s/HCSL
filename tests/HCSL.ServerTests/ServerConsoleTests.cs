using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Server;
using System.Text.Json;
namespace HCSL.ServerTests;
public static class ServerConsoleTests
{
    public static async Task RunAsync()
    {
        foreach (var command in new[] { "version", "quit", "exit", "exec test.cfg", "status; quit", "sv_password secret", "changelevel de_dust2", "mp_roundtime NaN", "alias practice \"mp_roundtime 60; mp_freezetime 0\"", "  MixedCase  \"space ; text\"  ", new string('a',4096), new string('中',4096) })
            if (ServerConsoleRules.Validate(command) != command || ServerConsoleRules.Parse(command).IsMutation) throw new Exception("General console modified or rejected text.");
        foreach (var invalid in new[] { "", " ", "status\nquit", "status\rquit", "status\0", "echo\tbad", "echo\u001bbad", "echo\u007f", "echo\u2028bad", "echo\u2029bad", "echo\ud800", "echo\udc00", new string('a',4097) })
        {
            try { ServerConsoleRules.Validate(invalid); throw new Exception("Bad transport input accepted."); }
            catch (ServerException) { }
        }
        if (JsonSerializer.Deserialize<ServerConsoleCommand>("{\"Text\":\"status\"}")!.ConfirmMutation) throw new Exception("Legacy flag default changed.");
        var echoAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var echo = new ConsoleEchoWindow();
        echo.Record("ciallo", echoAt);
        if (!echo.TryConsume("ciallo", echoAt.AddMilliseconds(200), out var echoed) || echoed != "ciallo")
            throw new Exception("The engine echo of a written command was not recognized.");
        if (echo.TryConsume("ciallo", echoAt.AddMilliseconds(300), out _))
            throw new Exception("One written command marked two log lines.");
        echo.Record("status", echoAt);
        if (echo.TryConsume("status", echoAt + ConsoleEcho.Window + TimeSpan.FromSeconds(1), out _))
            throw new Exception("An echo outside the window was recognized.");
        if (!ConsoleEcho.Matches("] mp_roundtime 60  ", "mp_roundtime 60"))
            throw new Exception("Engine prompt characters defeated the echo match.");
        if (ConsoleEcho.Matches("mp_roundtime 6", "mp_roundtime 60"))
            throw new Exception("A different command was mistaken for an echo.");
        var unknown = await new ServerClient(new BrokenClient()).SendConsoleAsync(new(Guid.NewGuid(), Guid.NewGuid(), "status"));
        if (unknown.Delivery != ConsoleDelivery.Unknown) throw new Exception("Connection loss must be unknown.");
        var fake = new FakeClient();
        using var controller = new ServerController(fake, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
        await controller.ConnectAsync();
        if (!controller.ConsoleHistoryEnabled) throw new Exception("History must default on.");
        await controller.SendConsoleAsync(Guid.NewGuid(), "status");
        if (fake.Sends != 0) throw new Exception("Wrong RunId sent.");
        var loadingFake = new FakeClient { State = ServerState.LoadingMap };
        using (var loading = new ServerController(loadingFake, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)))
        {
            await loading.ConnectAsync();
            if (!loading.CanInputConsole || !loading.CanSendConsole) throw new Exception("Console was locked while the PTY was active but the map was not ready.");
            await loading.SendConsoleAsync(loadingFake.Run, "host_workshop_collection 3570801975");
            if (loadingFake.Sends != 1 || loadingFake.Last!.Text != "host_workshop_collection 3570801975") throw new Exception("Loading-state console command was not sent.");
        }
        await controller.SendConsoleAsync(fake.Run, "sv_password secret");
        if (fake.Sends != 1 || controller.ConsoleHistory.Count != 0 || controller.ConsoleRows.Any(r => r.Text.Contains("secret"))) throw new Exception("Sensitive command display/history leaked.");
        await controller.SendConsoleAsync(fake.Run, "mp_roundtime 60; exec practice.cfg");
        if (fake.Sends != 2 || controller.ConsoleHistory.Count != 1 || fake.Last!.ConfirmMutation || controller.ConsoleRows.Any(r => r.Text.Contains("mp_roundtime 60"))) throw new Exception("General input/history not supported, or the host echoed the input into the log.");
        controller.ConsoleHistoryEnabled = false;
        await controller.SendConsoleAsync(fake.Run, "status");
        if (controller.ConsoleHistory.Count != 0) throw new Exception("Disabled history recorded text.");
        controller.ConsoleHistoryEnabled = true;
        for (var i=0;i<55;i++) await controller.SendConsoleAsync(fake.Run,"echo " + i);
        if (controller.ConsoleHistory.Count != 50) throw new Exception("Unbounded history.");
        controller.ClearConsoleHistory();
        if (controller.ConsoleHistory.Count != 0) throw new Exception("Clear failed.");
        fake.Run=Guid.NewGuid(); await controller.RefreshAsync();
        if (controller.ConsoleRows.Count != 0 || controller.ConsoleStatus.Length != 0) throw new Exception("Run change retained input.");
        await controller.SendConsoleAsync(fake.Run, "status");
        controller.Dispose();
        if (controller.ConsoleHistory.Count != 0 || controller.ConsoleRows.Any(r => r.Id.StartsWith("input:") || r.Id.StartsWith("feedback:")))
            throw new Exception("Dispose retained command history or host-generated console rows.");
        // Status may be newer than the UI snapshot while dispatch is queued. Log paging must use
        // that returned status identity, not the old UI RunId/cursor.
        var queued = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var pollingFake = new FakeClient();
        using (var polling = new ServerController(pollingFake, action => queued.Enqueue(action),
            activeInterval: TimeSpan.FromMilliseconds(10), idleInterval: TimeSpan.FromMilliseconds(10)))
        {
            await polling.ConnectAsync();
            while (queued.TryDequeue(out var action)) action();
            polling.SetLogsVisible(true);
            pollingFake.Run = Guid.NewGuid();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!pollingFake.LogRuns.Contains(pollingFake.Run) && DateTime.UtcNow < deadline) await Task.Delay(10);
            if (!pollingFake.LogRuns.Contains(pollingFake.Run)) throw new Exception("Log paging reused the stale UI RunId while dispatch was queued.");
        }
        var viewport = new ConsoleLogViewport();
        ConsoleLogRow[] rows = [new("1","same"),new("2","same"),new("3","last")];
        viewport.Capture(rows,1);
        if (viewport.Restore(rows[1..]) != 0 || viewport.ReadingPositionLost) throw new Exception("Stable anchor lost on append/shift.");
        if (viewport.Restore(rows[2..]) != 0 || !viewport.ReadingPositionLost) throw new Exception("Eviction must be explicit.");
        viewport.Reset(); if(viewport.ReadingPositionLost) throw new Exception("Latest did not clear notice.");
        var maps = new ServerMapTracker();
        maps.Observe("Created physics for de_dust2"); maps.Observe("ss_loading -> ss_active"); maps.Observe("SV: 64 player server started");
        if (!maps.Complete || maps.Map != "de_dust2") throw new Exception("Initial readiness failed.");
        maps.Observe("ss_active -> ss_dead");
        if (maps.Complete || maps.Map != null) throw new Exception("Old readiness survived map change.");
        maps.Observe("ss_dead -> ss_loading"); maps.Observe("ss_loading -> ss_active"); maps.Observe("Created physics for de_mirage");
        if (maps.Complete) throw new Exception("Map must require all evidence.");
        maps.Observe("SV: 64 player server started");
        if (!maps.Complete || maps.Map != "de_mirage") throw new Exception("New map readiness failed.");
        Console.WriteLine("PASS console.general-transport-history-markers-privacy-run-reset-scroll-anchors-map-cycle");
    }
    private sealed class BrokenClient : IDaemonClient
    {
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct=default) => throw new IOException("transport lost");
    }
    private sealed class FakeClient : IDaemonClient
    {
        public Guid Run=Guid.NewGuid(); public int Sends; public ServerConsoleCommand? Last; public ServerState State=ServerState.Running;
        public System.Collections.Concurrent.ConcurrentQueue<Guid> LogRuns = new();
        public Task<LocalResponse> SendAsync(LocalRequest request,CancellationToken ct=default)
        {
            if (request.Kind==RequestKind.ServerConsole)
            {
                Sends++; var c=JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson!)!; Last=c;
                return Task.FromResult(new LocalResponse(LocalProtocol.Version,request.Id,true,ConsoleReceiptJson:JsonSerializer.Serialize(new ServerConsoleReceipt(c.RunId,c.CommandId,ConsoleDelivery.Written,"HCSL-CONSOLE-WRITTEN",ServerConsoleRules.Display(c.Text)))));
            }
            if (request.ServerCommandJson is { } json && JsonSerializer.Deserialize<ServerCommand>(json) is { Action: ServerAction.Logs, RunId: { } logRun })
            {
                LogRuns.Enqueue(logRun);
                if (logRun != Run) throw new IOException("HCSL-SERVER-STALE: Log cursor belongs to another run.");
            }
            var snapshot=new ServerSnapshot(Run,null,State,true,"",true,false,null,0,false,[]);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version,request.Id,true,Defaults:new("/settings","/data","/library"),ServerSnapshotJson:JsonSerializer.Serialize(snapshot)));
        }
    }
}
