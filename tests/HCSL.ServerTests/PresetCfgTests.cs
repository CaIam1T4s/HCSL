using System.Text;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

public static class PresetCfgTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hcsl-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var daemon = new CfgDaemon(root);
            var item = daemon.Store.Execute(daemon.Workspace, new(InstanceAction.Create, Name: "CFG fixture")).Instances.Single();
            var instance = item.Definition!; var preset = item.Presets.Single();
            var store = new LocalPresetCfgStore();
            PresetCfgCommand C(PresetCfgAction action, string? name = null, string? text = null, string? hash = null, string? rename = null)
                => new(action, instance.Id, preset.Id, instance.Revision, name, text, hash, rename);
            PresetCfgResult Do(PresetCfgCommand c) => store.Execute(daemon.Workspace, c);
            void Reject(Action action, string code)
            {
                try { action(); } catch (InstanceException e) when (e.Code == code) { return; }
                throw new Exception("Expected " + code);
            }
            foreach (var invalid in new[] { "../escape.cfg", "/tmp/escape.cfg", "sub/test.cfg", "x;quit.cfg", "x.cfg\n", "hcsl-instance-password.cfg", "HCSL-OTHER.cfg", "server-password.cfg", "CON.cfg" })
                Reject(() => Do(C(PresetCfgAction.Save, invalid, "status")), "HCSL-CFG-NAME");
            var initial = Do(C(PresetCfgAction.Save, "practice.cfg", "mp_freezetime 0\n// 中文\n"));
            Check(initial.Hash is not null && Do(C(PresetCfgAction.Read, "practice.cfg")).Content == initial.Content, "UTF-8 round trip.");
            Reject(() => Do(C(PresetCfgAction.Save, "practice.cfg", "overwrite")), "HCSL-CFG-CONFLICT");
            Reject(() => Do(C(PresetCfgAction.Save, "practice.cfg", "overwrite", "stale")), "HCSL-CFG-CONFLICT");
            Reject(() => Do(C(PresetCfgAction.Save, "other.cfg", "status") with { InstanceRevision = instance.Revision - 1 }), "HCSL-INSTANCE-STALE");
            Reject(() => Do(C(PresetCfgAction.Save, "too-big.cfg", new string('中', PresetCfgRules.MaxBytes))), "HCSL-CFG-CONTENT");
            Reject(() => Do(C(PresetCfgAction.Save, "bad.cfg", "\0")), "HCSL-CFG-CONTENT");
            Reject(() => Do(C(PresetCfgAction.Save, "bad.cfg", "\ud800")), "HCSL-CFG-CONTENT");
            var cfg = Path.Combine(root, "data", "instances", instance.Id.ToString("D"), "presets", preset.Id.ToString("D"), "cfg");
            File.WriteAllText(Path.Combine(cfg, "practice.cfg"), "external editor\n");
            Reject(() => Do(C(PresetCfgAction.Delete, "practice.cfg", hash: initial.Hash)), "HCSL-CFG-CONFLICT");
            var current = Do(C(PresetCfgAction.Read, "practice.cfg"));
            Do(C(PresetCfgAction.Save, "existing.cfg", "status"));
            Reject(() => Do(C(PresetCfgAction.Rename, "practice.cfg", hash: current.Hash, rename: "existing.cfg")), "HCSL-CFG-EXISTS");
            Do(C(PresetCfgAction.Rename, "practice.cfg", hash: current.Hash, rename: "renamed.cfg"));
            Check(!File.Exists(Path.Combine(cfg, "practice.cfg")) && Do(C(PresetCfgAction.Read, "renamed.cfg")).Hash == current.Hash, "Rename content/version changed.");
            using (new LocalWorkspaceManager().Acquire(daemon.Workspace))
            {
                Check(Do(C(PresetCfgAction.List)).Files.Any(f => f.Name == "renamed.cfg"), "Listing blocked by live lease.");
                Check(Do(C(PresetCfgAction.Read, "renamed.cfg")).Hash == current.Hash, "Reading blocked by live lease.");
                try { Do(C(PresetCfgAction.Save, "blocked.cfg", "status")); throw new Exception("Lease bypass."); }
                catch (WorkspaceException) { }
            }
            if (OperatingSystem.IsLinux())
            {
                var outside = Path.Combine(root, "outside.cfg"); File.WriteAllText(outside, "private");
                File.CreateSymbolicLink(Path.Combine(cfg, "linked.cfg"), outside);
                Check(!Do(C(PresetCfgAction.List)).Files.Any(f => f.Name == "linked.cfg"), "Linked CFG listed.");
                try { Do(C(PresetCfgAction.Read, "linked.cfg")); throw new Exception("Linked CFG read."); } catch (WorkspaceException) { }
                File.Delete(Path.Combine(cfg, "linked.cfg"));
                var originalCfg = cfg + "-real";
                Directory.Move(cfg, originalCfg); Directory.CreateSymbolicLink(cfg, originalCfg);
                try
                {
                    try { Do(C(PresetCfgAction.Save, "ancestor.cfg", "status")); throw new Exception("Linked ancestor accepted."); }
                    catch (WorkspaceException) { }
                }
                finally { Directory.Delete(cfg); Directory.Move(originalCfg, cfg); }
            }
            File.WriteAllBytes(Path.Combine(cfg, "oversized.cfg"), new byte[PresetCfgRules.MaxBytes + 1]);
            Reject(() => Do(C(PresetCfgAction.Read, "oversized.cfg")), "HCSL-CFG-LIMIT");
            File.Delete(Path.Combine(cfg, "oversized.cfg"));
            var max = Do(C(PresetCfgAction.Save, "max.cfg", new string('\t', PresetCfgRules.MaxBytes)));
            var response = new LocalResponse(LocalProtocol.Version, Guid.NewGuid(), true, PresetCfgResultJson: JsonSerializer.Serialize(max));
            Check(JsonSerializer.SerializeToUtf8Bytes(response).Length < LocalProtocol.MaxFrameBytes, "Nested JSON exceeded IPC frame.");
            Do(C(PresetCfgAction.Delete, "max.cfg", hash: max.Hash));
            using var server = new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
            using var host = new HostServerController(server, new InstanceClient(daemon));
            await host.InitializeAsync();
            await host.CfgAsync(C(PresetCfgAction.List));
            Check(daemon.Console.Count == 0, "Listing sent console commands.");
            var run = Guid.NewGuid();
            daemon.Status = daemon.Status with { RunId = run, InstanceId = instance.Id, State = ServerState.Running, Active = true,
                Preset = new(preset.Id, preset.Revision, preset.Name, preset.Map) };
            await server.RefreshAsync();
            Check(host.CanExecCfg(instance.Id, preset.Id), "Running identity unavailable.");
            try { await host.ExecCfgAsync(instance.Id, Guid.NewGuid(), run, "renamed.cfg", current.Hash!); throw new Exception("Preset mismatch accepted."); }
            catch (IOException) { }
            try { await host.ExecCfgAsync(instance.Id, preset.Id, Guid.NewGuid(), "renamed.cfg", current.Hash!); throw new Exception("Run mismatch accepted."); }
            catch (IOException) { }
            try { await host.CfgAsync(C(PresetCfgAction.Save, "blocked.cfg", "status")); throw new Exception("Frontend live mutation accepted."); }
            catch (IOException) { }
            try { await host.ExecCfgAsync(instance.Id, preset.Id, run, "renamed.cfg", "stale"); throw new Exception("Changed exec file accepted."); }
            catch (IOException) { }
            Check(daemon.Console.Count == 0, "Rejected CFG operation sent command.");
            await host.ExecCfgAsync(instance.Id, preset.Id, run, "renamed.cfg", current.Hash!);
            Check(daemon.Console.Single().RunId == run && daemon.Console.Single().Text == "exec renamed.cfg", "Wrong exec target.");
            daemon.Delivery = ConsoleDelivery.Unknown;
            var uncertain = await host.ExecCfgAsync(instance.Id, preset.Id, run, "renamed.cfg", current.Hash!);
            Check(uncertain.Contains("未知") && daemon.Console.Count == 2, "Unknown exec was misreported or retried.");
            daemon.Status = daemon.Status with { Active = false, State = ServerState.Stopped }; await server.RefreshAsync();
            Do(C(PresetCfgAction.Delete, "renamed.cfg", hash: current.Hash));
            Check(!File.Exists(Path.Combine(cfg, "renamed.cfg")) && Directory.GetFiles(Path.Combine(Path.GetDirectoryName(cfg)!, ".cfg-trash")).Length >= 1, "Delete was not recoverable.");
            Check(!C(PresetCfgAction.Save, "a.cfg", "secretfixture").ToString().Contains("secretfixture"), "Command ToString leaked content.");
            Console.WriteLine("PASS cfg.real-storage-utf8-crud-conflicts-reserved-paths-leases-bounds-ipc-and-run-bound-exec");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class CfgDaemon(string root) : IDaemonClient
    {
        public readonly LocalInstanceStore Store = new();
        public readonly WorkspaceRequest Workspace = new(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
        public ServerSnapshot Status = new(null, null, ServerState.Idle, false, "idle", false, true, null, 0, false, []);
        public readonly List<ServerConsoleCommand> Console = [];
        public ConsoleDelivery Delivery = ConsoleDelivery.Written;
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            var response = request.Kind switch
            {
                RequestKind.Hello => new(LocalProtocol.Version, request.Id, true, Defaults: new(Workspace.SettingsRoot, Workspace.DataRoot, Workspace.LibraryRoot)),
                RequestKind.Instances => new(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(Store.Execute(Workspace, JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!))),
                RequestKind.PresetCfg => new(LocalProtocol.Version, request.Id, true, PresetCfgResultJson: JsonSerializer.Serialize(new LocalPresetCfgStore().Execute(Workspace, JsonSerializer.Deserialize<PresetCfgCommand>(request.PresetCfgJson!)!))),
                RequestKind.ServerConsole => SendConsole(request),
                _ => new LocalResponse(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(Status))
            };
            return Task.FromResult(response);
        }
        private LocalResponse SendConsole(LocalRequest request)
        {
            var command = JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson!)!; Console.Add(command);
            return new(LocalProtocol.Version, request.Id, true, ConsoleReceiptJson: JsonSerializer.Serialize(new ServerConsoleReceipt(command.RunId, command.CommandId, Delivery, "fixture", DisplayText: command.Text)));
        }
    }
}
