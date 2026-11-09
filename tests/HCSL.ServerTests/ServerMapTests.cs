using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Server;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

public static class ServerMapTests
{
    public static async Task RunAsync()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var target in new[] { new ServerMapTarget(ServerMapKind.Standard, "de_mirage"), new(ServerMapKind.Workshop, "3070244462"), new(ServerMapKind.WorkshopCollection, "3070244462") }) ServerMapRules.Validate(target);
        foreach (var target in new[] { new ServerMapTarget(ServerMapKind.Standard, "../bad"), new(ServerMapKind.Standard, "de_dust2;quit"), new(ServerMapKind.Standard, "123"), new(ServerMapKind.Workshop, "https://example.com/?id=123"), new(ServerMapKind.Workshop, "0"), new(ServerMapKind.Workshop, "00123"), new(ServerMapKind.Workshop, "18446744073709551616"), new((ServerMapKind)55, "de_mirage") })
        {
            try { ServerMapRules.Validate(target); throw new Exception("Unsafe target accepted."); } catch (InstanceException) { }
        }
        Check(ServerMapRules.StartupArguments(new(ServerMapKind.Workshop, "12345")).SequenceEqual(new[] { "+host_workshop_map", "12345" }), "Workshop startup arguments.");
        Check(ServerMapRules.StartupArguments(new(ServerMapKind.WorkshopCollection, "12345")).SequenceEqual(new[] { "+map", ServerMapRules.CollectionSeedMap, "+host_workshop_collection", "12345" }), "Workshop collection startup arguments must seed a built-in map first.");
        Check(ServerMapRules.StartupArguments(new(ServerMapKind.WorkshopCollection, "12345"))[1] == "de_dust2", "Collection seed map changed without an operator-visible policy.");
        Check(SystemdServerRuntime.IsWorkshopMap("workshop/12345/de_test") && SystemdServerRuntime.IsWorkshopMap("maps/workshop/12345/de_test") && !SystemdServerRuntime.IsWorkshopMap("de_dust2"), "Workshop readiness path classification is wrong.");
        Check(SystemdServerRuntime.IsSeedOnly(true, false, "de_dust2") && SystemdServerRuntime.IsSeedOnly(true, false, "aim_map"), "Collection seed map should be suppressed before the workshop context is active.");
        Check(!SystemdServerRuntime.IsSeedOnly(true, true, "aim_map") && !SystemdServerRuntime.IsSeedOnly(true, false, "workshop/12345/aim_map") && !SystemdServerRuntime.IsSeedOnly(false, false, "aim_map"), "A workshop map must not be suppressed as the collection seed.");
        Check(ServerMapRules.ChangeCommand(new(ServerMapKind.WorkshopCollection, "12345")) == "host_workshop_collection 12345", "Workshop collection change command.");
        Check(ServerMapRules.Label(new(ServerMapKind.WorkshopCollection, "12345")) == "Collection #12345", "Workshop collection label.");
        Check(MapPoolRules.Parse("collection 12345").Kind == ServerMapKind.WorkshopCollection, "Workshop collection pool parse.");
        var groupName = "hcsl_" + Guid.NewGuid().ToString("N")[..16];
        MapPoolRules.Validate(new(1, 0, [new(ServerMapKind.WorkshopCollection, "12345")]));
        try { MapPoolRules.Validate(new(1, 0, [new(ServerMapKind.WorkshopCollection, "12345"), new(ServerMapKind.Standard, "de_dust2")])); throw new Exception("Mixed collection pool accepted."); }
        catch (InstanceException e) when (e.Code == "HCSL-MAPPOOL-COLLECTION") { }
        try { ServerMapRules.StartupArguments(new(ServerMapKind.WorkshopCollection, "12345"), groupName); throw new Exception("Collection accepted a native map group."); }
        catch (InstanceException e) when (e.Code == "HCSL-MAPGROUP-NAME") { }
        Check(ServerMapRules.StartupArguments(new(ServerMapKind.Standard, "de_mirage")).SequenceEqual(new[] { "+map", "de_mirage" }), "Standard startup arguments.");
        Check(ServerMapRules.StartupArguments(new(ServerMapKind.Standard, "de_mirage"), groupName).SequenceEqual(new[] { "+mapgroup", groupName, "+map", "de_mirage" }), "Map group startup arguments.");
        try { ServerMapRules.StartupArguments(new(ServerMapKind.Workshop, "12345"), groupName); throw new Exception("Workshop accepted a map group."); } catch (InstanceException e) when (e.Code == "HCSL-MAPGROUP-NAME") { }
        try { ServerMapRules.StartupArguments(new(ServerMapKind.Standard, "de_mirage"), "+map de_dust2"); throw new Exception("Arbitrary map group accepted."); } catch (InstanceException e) when (e.Code == "HCSL-MAPGROUP-NAME") { }
        var presetId = Guid.NewGuid();
        Check(ServerMapGroupRules.GroupName(presetId) == "hcsl_" + presetId.ToString("N")[..16], "Map group name is not stable.");
        Check(ServerMapGroupRules.Create(presetId, "de_dust2", new(1, 0, [])) is null, "Empty pool produced a map group.");
        Check(ServerMapGroupRules.Create(presetId, "de_dust2", new(1, 0, [new(ServerMapKind.Workshop, "12345")])) is null, "Workshop pool produced a native map group.");
        Check(ServerMapGroupRules.Create(presetId, "de_dust2", new(1, 0, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Workshop, "12345")])) is null, "Mixed pool produced a native map group.");
        try { ServerMapGroupRules.Create(presetId, "de_mirage", new(1, 0, [new(ServerMapKind.Standard, "de_dust2")])); throw new Exception("Startup map outside the pool accepted."); } catch (InstanceException e) when (e.Code == "HCSL-MAPGROUP-STARTUP") { }
        try { ServerMapGroupRules.Create(presetId, "de_dust2", new(1, 0, [new(ServerMapKind.WorkshopCollection, "12345"), new(ServerMapKind.Standard, "de_dust2")])); throw new Exception("Mixed collection produced a map group."); } catch (InstanceException e) when (e.Code == "HCSL-MAPPOOL-COLLECTION") { }
        var group = ServerMapGroupRules.Create(presetId, "de_dust2", new(1, 0, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage")]))!;
        Check(group.Content.Contains("\"de_dust2\"\t\t\"\"", StringComparison.Ordinal) && group.Content.Contains("\"de_mirage\"\t\t\"\"", StringComparison.Ordinal) && group.Content.IndexOf("de_dust2", StringComparison.Ordinal) < group.Content.IndexOf("de_mirage", StringComparison.Ordinal), "Map group order/content missing.");
        Check(group.Content.Contains("\"gameTypes\"", StringComparison.Ordinal) && group.Content.Contains("\"competitive\"", StringComparison.Ordinal) && group.Content.Contains("\"mapgroupsMP\"", StringComparison.Ordinal) && group.Content.Contains("\"" + group.Name + "\"\t\t\"\"", StringComparison.Ordinal), "Map group is not registered for the active game mode.");
        Check(group.Hash == ServerMapGroupRules.Create(presetId, "de_dust2", new(1, 9, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage")]))!.Hash, "Map group hash depends on pool revision instead of content.");
        var old = JsonSerializer.Deserialize<PresetDefinition>("{\"FormatVersion\":1,\"Id\":\"" + Guid.NewGuid() + "\",\"Revision\":1,\"Name\":\"old\",\"Map\":\"de_dust2\"}")!;
        Check(old.MapKind == ServerMapKind.Standard, "Old JSON map kind default changed."); InstanceRules.Preset(old);
        try { InstanceRules.Preset(old with { MapKind = ServerMapKind.WorkshopCollection, Map = "12345" }); throw new Exception("Collection accepted as a preset startup map."); }
        catch (InstanceException e) when (e.Code == "HCSL-PRESET-INVALID") { }
        var root = Path.Combine(Path.GetTempPath(), "hcsl-map-tests-" + Guid.NewGuid());
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            var store = new LocalInstanceStore(); var entry = store.Execute(workspace, new(InstanceAction.Create, Name: "Maps")).Instances.Single();
            var p = entry.Presets.Single(); var d = entry.Definition!;
            var edited = store.Execute(workspace, new(InstanceAction.UpdatePreset, d.Id, p.Id, d.Revision, Preset: p with { MapKind = ServerMapKind.Workshop, Map = "3070244462" })).Instances.Single();
            Check(edited.Presets.Single().MapKind == ServerMapKind.Workshop && edited.Presets.Single().Map == "3070244462", "Workshop metadata did not persist.");
            var copy = store.Execute(workspace, new(InstanceAction.CopyPreset, d.Id, p.Id, edited.Definition!.Revision, "Copy")).Instances.Single();
            Check(copy.Presets.Single(x => x.Id != p.Id).MapKind == ServerMapKind.Workshop, "Copy lost kind.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        var tracker = new ServerMapTracker();
        tracker.Observe("CNetworkGameServerBase::SetServerState (ss_active -> ss_dead)");
        tracker.Observe("[UGC] Downloading workshop item 3070244462");
        Check(!tracker.Complete && tracker.Progress.Phase == ServerMapPhase.DownloadObserved, "Download claimed readiness.");
        tracker.Observe("[UGC] Failed to download workshop item 3070244462");
        Check(!tracker.Complete && tracker.Progress.Phase == ServerMapPhase.DownloadFailed, "Download failure not visible.");
        tracker.Observe("CNetworkGameServerBase::SetServerState (ss_waiting -> ss_loading)");
        tracker.Observe("Created physics for workshop/3070244462/test_map");
        tracker.Observe("CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)");
        Check(!tracker.Complete, "Missing player-start evidence accepted.");
        tracker.Observe("SV:  64 player server started");
        Check(tracker.Complete && tracker.Map == "workshop/3070244462/test_map", "Workshop actual map not recognized.");
        tracker.Observe("[UGC] Downloading workshop item 9999");
        Check(tracker.Progress.Phase == ServerMapPhase.DownloadObserved, "Old ready swallowed new download.");
        tracker.Observe("CNetworkGameServerBase::SetServerState (ss_active -> ss_loading)");
        Check(!tracker.Complete && tracker.Map is null, "Old map remained ready while loading.");
        tracker.Unconfirmed(); Check(tracker.Progress.Phase == ServerMapPhase.Unconfirmed, "Timeout advisory missing.");
        var transition = new ServerMapTracker();
        Check(transition.ObserveEmptyChangeLevel("CHANGE LEVEL: de_dust2") is null && transition.PendingChangeLevel == "de_dust2", "Intended change map was not retained.");
        Check(transition.ObserveEmptyChangeLevel("CHostStateMgr::QueueNewRequest( Changelevel (), 5 )") == "de_dust2", "Empty changelevel was not detected.");
        Check(transition.ObserveEmptyChangeLevel("CHostStateMgr::QueueNewRequest( Changelevel (de_dust2), 6 )") is null, "Valid request re-reported as empty.");
        Check(transition.ObserveEmptyChangeLevel("CHANGE LEVEL: de_nuke") is null && transition.ObserveEmptyChangeLevel("Host activate: Changelevel (de_nuke)") is null, "Valid transition not cleared.");
        Check(ServerMapRecovery.Command("de_dust2", ServerMapKind.Standard) == "changelevel de_dust2", "Standard recovery command missing.");
        Check(ServerMapRecovery.Command("3070244462", ServerMapKind.Workshop) == "host_workshop_map 3070244462", "Workshop recovery command missing.");
        Check(ServerMapRecovery.Command("de_mirage", ServerMapKind.WorkshopCollection) == "ds_workshop_changelevel de_mirage", "Collection recovery command missing.");
        Check(ServerMapRecovery.Command("3070244462", ServerMapKind.WorkshopCollection) is null, "Collection recovery accepted a numeric map target.");
        Check(ServerMapRecovery.Command("de_dust2;quit", ServerMapKind.Standard) is null && ServerMapRecovery.Command("3070244462", ServerMapKind.Standard) is null, "Unsafe recovery target accepted.");
        Check(ServerStatusText.CodeHint("HCSL-SERVER-MAP-EMPTY", false) is { Length: > 0 } && ServerStatusText.CodeHint("HCSL-SERVER-MAP-RECOVERY-FAILED", true) is { Length: > 0 }, "Empty-transition diagnostics missing.");
        Check(ServerLastRunStore.MapFrom("Created physics for ../../escape") is null, "Unsafe observed path accepted.");
        var fake = new MapDaemon();
        using var server = new ServerController(fake, action => action(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
        await server.ConnectAsync(); var client = new ServerMapClient(server); var snapshot = fake.Status;
        var receipt = await client.ChangeAsync(snapshot.InstanceId!.Value, snapshot.RunId!.Value, new(ServerMapKind.Workshop, "12345"));
        Check(receipt?.Delivery == ConsoleDelivery.Written && fake.Commands.Single().Text == "host_workshop_map 12345", "Map request not sent to console.");
        Check(server.State == ServerState.Running && server.Map == "de_dust2", "Submission optimistically changed map.");
        fake.Status = fake.Status with { RunId = Guid.NewGuid(), State = ServerState.LoadingMap, Map = null, MapLoaded = false };
        await server.RefreshAsync(); server.ObservedMap = "de_dust2";
        Check(server.Map is null, "Loading restored previous observed map.");
        try { await client.ChangeAsync(snapshot.InstanceId.Value, snapshot.RunId.Value, new(ServerMapKind.Standard, "de_mirage")); throw new Exception("Stale change accepted."); }
        catch (IOException) { }
        Check(fake.Commands.Count == 1, "Stale change reached IPC.");
        fake.Status = fake.Status with { State = ServerState.Running, Map = "de_dust2", MapLoaded = true };
        fake.Delivery = ConsoleDelivery.Unknown; await server.RefreshAsync();
        await client.ChangeAsync(fake.Status.InstanceId!.Value, fake.Status.RunId!.Value, new(ServerMapKind.Standard, "de_inferno"));
        Check(fake.Commands.Count == 2, "Unknown receipt was retried.");
        var runtime = new ProgressRuntime();
        await using (var operations = new HCSL.Daemon.Workers.ServerOperations(runtime))
        {
            operations.Start(new(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library")), new(Guid.NewGuid()));
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            runtime.Events!.Report(new(ServerState.LoadingMap, "HCSL-SERVER-MAP-LOADING"));
            runtime.Events.Report(new(ServerState.Starting, "HCSL-SERVER-LOG", "[UGC] Failed to download", MapProgress: new(ServerMapPhase.DownloadFailed)));
            var failed = operations.Snapshot();
            Check(failed.State == ServerState.LoadingMap && failed.MapProgress?.Phase == ServerMapPhase.DownloadFailed && !failed.MapLoaded && failed.Lines.Length == 0, "Status lost progress or log event changed lifecycle.");
            runtime.Events.Report(new(ServerState.Running, "HCSL-SERVER-MAP-READY", Map: "workshop/12345/arena", MapProgress: new(ServerMapPhase.Ready)));
            var ready = JsonSerializer.Deserialize<ServerSnapshot>(JsonSerializer.Serialize(operations.Snapshot()))!;
            Check(ready.MapLoaded && ready.Map == "workshop/12345/arena" && ready.MapProgress?.Phase == ServerMapPhase.Ready, "Actual map/progress lost across daemon status/JSON.");
        }
        Console.WriteLine("PASS maps.rules-legacy-json-persistence-copy-arguments-download-evidence-map-transition-run-binding-no-optimism-no-retry");
    }
    private sealed class ProgressRuntime : IServerRuntimeService
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<ServerRuntimeEvent>? Events;
        public async Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop)
        {
            Events = events; Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
            return new("HCSL-SERVER-STOPPED", 0, true, true);
        }
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) { }
    }
    private sealed class MapDaemon : IDaemonClient
    {
        public ServerSnapshot Status = new(Guid.NewGuid(), Guid.NewGuid(), ServerState.Running, true, "HCSL-SERVER-MAP-READY", true, false, null, 0, false, [], Map: "de_dust2");
        public List<ServerConsoleCommand> Commands = [];
        public ConsoleDelivery Delivery = ConsoleDelivery.Written;
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken cancellationToken = default)
        {
            var response = request.Kind switch
            {
                RequestKind.Hello => new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: new("/settings", "/data", "/library")),
                RequestKind.Server => new LocalResponse(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(Status)),
                RequestKind.ServerConsole => Console(request),
                _ => throw new Exception("Unexpected request.")
            };
            return Task.FromResult(response);
        }
        private LocalResponse Console(LocalRequest request)
        {
            var command = JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson!)!; Commands.Add(command);
            return new(LocalProtocol.Version, request.Id, true, ConsoleReceiptJson: JsonSerializer.Serialize(new ServerConsoleReceipt(command.RunId, command.CommandId, Delivery, "HCSL-CONSOLE-TEST", command.Text)));
        }
    }
}
