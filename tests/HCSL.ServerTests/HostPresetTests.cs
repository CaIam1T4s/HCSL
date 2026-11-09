using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Daemon.Workers;
using HCSL.Frontend;
using HCSL.Infrastructure.Storage;

namespace HCSL.ServerTests;

public static class HostPresetTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hcsl-host-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var daemon = new StorageDaemon(root);
            var created = daemon.Store.Execute(daemon.Workspace, new(InstanceAction.Create, Name: "Presets fixture"));
            var first = created.Instances.Single();
            var opener = new RecordingOpener();
            using var server = new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
            using var model = new HostServerController(server, new InstanceClient(daemon), opener);
            await model.InitializeAsync();
            var instance = model.Selected!.Entry.Definition!;
            var original = model.Selected.Entry.Presets.Single();
            var draft = new PresetDefinition(1, Guid.Empty, 1, "Practice", "de_mirage", 0, 1, false);
            Check(await model.CreatePresetAsync(instance.Id, instance.Revision, draft), model.Error);
            var added = model.Selected!.Entry.Presets.Single(p => p.Id != original.Id);
            Check(added.Map == "de_mirage" && !added.VacPreferred && added.Revision == 1, "Create lost initial settings.");
            Check(model.Selected.Entry.Definition!.SelectedPresetId == original.Id, "Create changed active selection.");
            var beforeInvalid = model.Selected.Entry.Definition.Revision;
            Check(!await model.CreatePresetAsync(instance.Id, beforeInvalid, draft with { Map = "workshop/123/map" }), "Invalid initial map was accepted.");
            Check(daemon.Store.Execute(daemon.Workspace, new(InstanceAction.List)).Instances.Single().Definition!.Revision == beforeInvalid, "Rejected create consumed instance revision.");
            instance = model.Selected.Entry.Definition;
            Check(!await model.UpdatePresetAsync(instance.Id, instance.Revision - 1, added with { Map = "de_inferno" }), "Stale instance revision was accepted.");
            Check(await model.UpdatePresetAsync(instance.Id, instance.Revision, added with { Map = "de_inferno", GameMode = 2 }), model.Error);
            added = model.Selected.Entry.Presets.Single(p => p.Id == added.Id);
            Check(added.Map == "de_inferno" && added.GameMode == 2 && !added.VacPreferred && added.Revision == 2, "Edit lost fields / VAC preference / revision.");
            var cfg = await new InstanceClient(daemon).PresetCfgDirectoryAsync(instance.Id, added.Id);
            File.WriteAllText(Path.Combine(cfg, "practice.cfg"), "mp_freezetime 0\n");
            instance = model.Selected.Entry.Definition!;
            Check(await model.CopyPresetAsync(instance.Id, instance.Revision, added.Id, "Copy"), model.Error);
            var copy = model.Selected.Entry.Presets.Single(p => p.Name == "Copy");
            var copyCfg = await new InstanceClient(daemon).PresetCfgDirectoryAsync(instance.Id, copy.Id);
            Check(copy.Id != added.Id && File.ReadAllText(Path.Combine(copyCfg, "practice.cfg")) == "mp_freezetime 0\n", "Copy did not isolate and copy CFG.");
            Check(await model.OpenPresetCfgAsync(instance.Id, copy.Id) && opener.Path == copyCfg, "CFG opener targeted another preset.");
            Check(!await model.OpenPresetCfgAsync(instance.Id, Guid.NewGuid()), "Unknown preset could be opened.");
            instance = model.Selected.Entry.Definition!;
            Check(await model.SelectPresetAsync(instance.Id, instance.Revision, added.Id), model.Error);
            instance = model.Selected.Entry.Definition!;
            Check(!await model.DeletePresetAsync(instance.Id, instance.Revision, added.Id), "Selected preset could be deleted.");
            Check(await model.DeletePresetAsync(instance.Id, instance.Revision, copy.Id), model.Error);
            Check(model.Selected.Entry.Presets.All(p => p.Id != copy.Id), "Delete left copied preset visible.");
            var before = daemon.Mutations;
            daemon.Status = daemon.Status with { State = ServerState.Running, Active = true, InstanceId = instance.Id,
                Preset = new(original.Id, original.Revision, "Original runtime", original.Map) };
            await server.RefreshAsync();
            Check(model.PresetStatusText.Contains("Original runtime") && model.PresetStatusText.Contains("Practice"), "Selected and running preset identities were conflated.");
            Check(!await model.CreatePresetAsync(instance.Id, instance.Revision, draft) && daemon.Mutations == before, "Active run mutation reached storage.");
            daemon.Status = daemon.Status with { State = ServerState.RecoveryRequired, Active = false };
            await server.RefreshAsync();
            Check(!await model.CopyPresetAsync(instance.Id, instance.Revision, original.Id, "Forbidden"), "Recovery mutation was allowed.");
            Check(!daemon.ServerCommands.Any(c => c.Action == ServerAction.Start), "Preset operations started the server.");
            Console.WriteLine("PASS host.presets.real-storage-crud-revision-cfg-copy-selection-live-identity-and-guards");

            var identity = new ServerPresetIdentity(Guid.NewGuid(), 7, "Prepared preset", "de_nuke");
            await using var operations = new ServerOperations(new PresetRuntime(identity));
            operations.Start(daemon.Workspace, new(instance.Id));
            for (var i = 0; i < 100 && operations.Snapshot().Preset is null; i++) await Task.Delay(10);
            var snapshot = operations.Snapshot();
            Check(snapshot.Preset == identity && snapshot.Lines.Length == 0, "Runtime preset was lost from status-only snapshot.");
            Check(JsonSerializer.Deserialize<ServerSnapshot>(JsonSerializer.Serialize(snapshot))!.Preset == identity, "Preset identity lost across JSON.");
            Console.WriteLine("PASS daemon.presets.prepared-identity-survives-status-json-without-log-page");
            var realOpener = new LocalDirectoryOpener();
            async Task RejectDirectory(string path)
            {
                try { await realOpener.OpenAsync(path); }
                catch (IOException e) when (e.Message.StartsWith("HCSL-PRESET-CFG:")) { return; }
                throw new Exception("Unsafe/missing directory reached file manager.");
            }
            await RejectDirectory("relative-cfg");
            await RejectDirectory(Path.Combine(root, "missing-cfg"));
            if (OperatingSystem.IsLinux())
            {
                var target = Path.Combine(root, "folder-target");
                Directory.CreateDirectory(Path.Combine(target, "cfg"));
                var link = Path.Combine(root, "folder-link");
                Directory.CreateSymbolicLink(link, target);
                await RejectDirectory(Path.Combine(link, "cfg"));
            }
            Console.WriteLine("PASS presets.cfg-opener-rejects-relative-missing-and-linked-ancestors-without-launching");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class RecordingOpener : ILocalDirectoryOpener
    {
        public string? Path;
        public Task OpenAsync(string directory, CancellationToken cancellationToken = default) { Path = directory; return Task.CompletedTask; }
    }
    private sealed class PresetRuntime(ServerPresetIdentity identity) : IServerRuntimeService
    {
        public Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop)
        {
            events.Report(new(ServerState.Starting, "prepared", Preset: identity));
            return Task.FromResult(new ServerOutcome("HCSL-SERVER-STOPPED", 0, false, true));
        }
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId = null) => throw new NotSupportedException();
    }
    private sealed class StorageDaemon(string root) : IDaemonClient
    {
        public readonly LocalInstanceStore Store = new();
        public readonly WorkspaceRequest Workspace = new(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
        public ServerSnapshot Status = new(null, null, ServerState.Idle, false, "idle", false, true, null, 0, false, []);
        public int Mutations;
        public List<ServerCommand> ServerCommands = [];
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            LocalResponse response;
            if (request.Kind == RequestKind.Hello)
                response = new(LocalProtocol.Version, request.Id, true, Defaults: new(Workspace.SettingsRoot!, Workspace.DataRoot!, Workspace.LibraryRoot!));
            else if (request.Kind == RequestKind.Instances)
            {
                var command = JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!;
                if (command.Action != InstanceAction.List) Mutations++;
                try { response = new(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(Store.Execute(Workspace, command))); }
                catch (InstanceException e) { response = new(LocalProtocol.Version, request.Id, false, e.Code, e.Message); }
            }
            else
            {
                ServerCommands.Add(JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!);
                response = new(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(Status));
            }
            return Task.FromResult(response);
        }
    }
}
