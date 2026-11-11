using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Storage;

namespace HCSL.ServerTests;

public static class HostInstanceCrudTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hcsl-host-crud-" + Guid.NewGuid().ToString("N"));
        var daemon = new StorageDaemon(root);
        try
        {
            using var host = new HostServerController(new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)), new InstanceClient(daemon));
            await host.InitializeAsync();
            Check(host.CanCreate && host.Instances.Count == 0, "Empty catalog must permit creation.");
            var lan = new HostInstanceDraft("LAN", 27015, new(NetworkAccessMode.Lan, "192.168.1.20"));
            Check(await host.CreateInstanceAsync(lan), host.Error);
            var original = host.Selected!.Entry.Definition!;
            Check(original.Network?.BindAddress == lan.Network!.BindAddress, "Create lost network settings.");
            Check(await host.CreateInstanceAsync(new("Second", 27016, new())), host.Error);
            var second = host.Selected!.Entry.Id;
            Check(second != original.Id, "New row must be selected.");
            Check(await host.UpdateInstanceAsync(original.Id, original.Revision, lan with { Name = "Renamed" }), host.Error);
            Check(host.Selected?.Entry.Id == second && host.Instances.Single(x => x.Entry.Id == original.Id).Entry.Definition!.Revision == 2, "Edit must target captured identity and preserve selection.");
            Check(!await host.UpdateInstanceAsync(original.Id, original.Revision, lan), "Stale revision accepted.");
            Check(host.Error.Contains("HCSL-"), "Backend error code lost.");
            Check(!await host.CreateInstanceAsync(lan), "Duplicate port accepted.");
            var calls = daemon.Mutations;
            Check(!await host.CreateInstanceAsync(lan with { GamePort = 0 }), "Invalid port accepted.");
            Check(!await host.CreateInstanceAsync(lan with { Network = new(NetworkAccessMode.Lan, "0.0.0.0") }), "Invalid LAN address accepted.");
            Check(daemon.Mutations == calls, "Invalid form must not reach IPC.");
            daemon.State = ServerState.Running;
            await host.Server.RefreshAsync();
            Check(!host.CanCreate && !host.CanMutate && !await host.DeleteInstanceAsync(original.Id, 2), "Active mutation admitted.");
            daemon.State = ServerState.RecoveryRequired;
            await host.Server.RefreshAsync();
            Check(!await host.CreateInstanceAsync(lan), "Recovery must block creation.");
            Check(daemon.Mutations == calls, "Blocked state sent a mutation.");
            daemon.State = ServerState.Idle;
            await host.Server.RefreshAsync();
            Check(await host.DeleteInstanceAsync(original.Id, 2), host.Error);
            Check(host.Instances.Count == 1 && host.Selected?.Entry.Id == second, "Delete changed another row.");
            Check(Directory.GetDirectories(Path.Combine(root, "data", "instances", ".trash")).Length == 1, "Delete bypassed storage recycle semantics.");
            Check(await host.DeleteInstanceAsync(second, 1), host.Error);
            Check(host.Selected is null && host.Instances.Count == 0 && host.CanCreate, "Last delete left a stale selection.");
            Console.WriteLine("PASS host.instances.crud-real-storage-revisions-selection-validation-and-runtime-guards");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static async Task RunMigrationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hcsl-host-layout-" + Guid.NewGuid().ToString("N"));
        var daemon = new StorageDaemon(root);
        try
        {
            using var host = new HostServerController(new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)), new InstanceClient(daemon));
            await host.InitializeAsync();
            Check(await host.CreateInstanceAsync(new("Layout", 27100)), host.Error);
            var d = host.Selected!.Entry.Definition!;
            var instance = Path.Combine(root, "data", "instances", d.Id.ToString());
            var preset = Path.Combine(instance, "presets", d.SelectedPresetId.ToString());
            Directory.CreateDirectory(Path.Combine(preset, "home")); Directory.CreateDirectory(Path.Combine(preset, "userdata"));
            File.WriteAllText(Path.Combine(preset, "home", "marker"), "home");
            File.WriteAllText(Path.Combine(preset, "userdata", "marker"), "userdata");
            Directory.Delete(Path.Combine(instance, "home"), true); Directory.Delete(Path.Combine(instance, "userdata"), true);
            var metadata = Path.Combine(instance, "instance.json");
            File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("\"LayoutVersion\": 2", "\"LayoutVersion\": 1"));
            await host.RefreshInstancesAsync();
            Check(host.CanMigrateLayout, "Old instance did not expose migration.");
            Check(await host.MigrateLayoutAsync(), host.Error);
            Check(host.Selected?.Entry.Definition is { LayoutVersion: 2 } && !host.CanMigrateLayout, "Migration did not update the selected instance.");
            Check(File.Exists(Path.Combine(instance, "home", "marker")) && File.Exists(Path.Combine(instance, "userdata", "marker")), "Migration did not publish instance data.");
            Console.WriteLine("PASS host.instances.layout-migration-explicit-and-idempotent");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class StorageDaemon(string root) : IDaemonClient
    {
        public int Mutations;
        public ServerState State;
        private readonly LocalInstanceStore store = new();
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            if (request.Kind == RequestKind.Hello)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: new(workspace.SettingsRoot, workspace.DataRoot, workspace.LibraryRoot)));
            if (request.Kind == RequestKind.Instances)
            {
                var command = JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!;
                if (command.Action != InstanceAction.List) Mutations++;
                try { return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(store.Execute(workspace, command)))); }
                catch (InstanceException e) { return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, e.Code, e.Message)); }
            }
            var snapshot = new ServerSnapshot(null, null, State, State == ServerState.Running, "", false, State == ServerState.Idle, null, 0, false, []);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
        }
    }
}
