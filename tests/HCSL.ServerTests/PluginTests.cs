using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

public static class PluginTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (InstanceException e) when (e.Code == code) { }
            catch (WorkspaceException e) when (e.Code == code) { }
        }
        var root = Path.Combine(Path.GetTempPath(), "hcsl-plugins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            var instances = new LocalInstanceStore(); var store = new LocalPluginStore();
            var entry = instances.Execute(workspace, new(InstanceAction.Create, Name: "Plugins")).Instances.Single();
            var d = entry.Definition!; var p = entry.Presets.Single();
            PluginCommand Read() => new(PluginAction.List, d.Id, p.Id);
            var empty = store.Execute(workspace, Read());
            Check(empty.Selected.Length == 0 && empty.Revision == 0 && empty.Hash.Length == 64 && empty.Available.Length == 0, "Empty manifest.");
            var package = Path.Combine(workspace.LibraryRoot, "plugin-packages", "metamod", "1.2.3");
            Directory.CreateDirectory(Path.Combine(package, "files", "csgo", "addons", "metamod"));
            File.WriteAllText(Path.Combine(package, "plugin.json"), """{"FormatVersion":1,"Id":"metamod","Name":"Metamod:Source","Version":"1.2.3"}""");
            File.WriteAllText(Path.Combine(package, "files", "csgo", "addons", "metamod", "server.so"), "binary");
            File.WriteAllText(Path.Combine(package, "files", "csgo", "gameinfo.gi"), "patched");
            var available = store.Execute(workspace, Read());
            Check(available.Available.Length == 1 && available.Available[0].Id == "metamod" && available.Available[0].Files == 2, "Package catalog.");
            var broken = Path.Combine(workspace.LibraryRoot, "plugin-packages", "broken", "0.1.0");
            Directory.CreateDirectory(Path.Combine(broken, "files"));
            File.WriteAllText(Path.Combine(broken, "plugin.json"), "{\"FormatVersion\":1}");
            var withBroken = store.Execute(workspace, Read());
            Check(withBroken.Available.Length == 1 && withBroken.Rejected is { Length: 1 } rejected
                && rejected[0].Id == "broken" && rejected[0].Version == "0.1.0" && rejected[0].Code == "HCSL-PLUGIN-PACKAGE", "Invalid package was not reported.");
            Directory.Delete(Path.Combine(workspace.LibraryRoot, "plugin-packages", "broken"), true);
            var command = new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, empty.Hash,
                [new("metamod", "1.2.3", true)]);
            var saved = store.Execute(workspace, command);
            Check(saved.Selected.Length == 1 && saved.Selected[0].Available && saved.Selected[0].Enabled && saved.Revision == 1, "Save selection.");
            Check(JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(workspace.DataRoot, "instances", d.Id.ToString("D"), "presets", p.Id.ToString("D"), "plugins.json")))!.Plugins.Length == 1, "Manifest not persisted.");
            Reject(() => store.Execute(workspace, command), "HCSL-PLUGIN-CONFLICT");
            Reject(() => store.Execute(workspace, new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, saved.Hash, [new("metamod", "1.2.3"), new("metamod", "1.2.3")])), "HCSL-PLUGIN-DUPLICATE");
            Reject(() => store.Execute(workspace, new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, saved.Hash, [new("metamod", "9.9.9")])), "HCSL-PLUGIN-PACKAGE");
            var disabledMissing = store.Execute(workspace, new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, saved.Hash, [new("metamod", "9.9.9", false)]));
            Check(disabledMissing.Selected[0].Available == false && disabledMissing.Selected[0].Error == "HCSL-PLUGIN-PACKAGE", "Disabled missing package.");
            var restored = store.Execute(workspace, new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, disabledMissing.Hash, [new("metamod", "1.2.3")]));
            Check(restored.Selected[0].Available, "Restore selection.");
            using (new LocalWorkspaceManager().Acquire(workspace))
            {
                Check(store.Execute(workspace, Read()).Selected.Length == 1, "Read took management lock.");
                Reject(() => store.Execute(workspace, new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, restored.Hash, [])), "HCSL-LOCK");
            }
            var copied = instances.Execute(workspace, new(InstanceAction.CopyPreset, d.Id, p.Id, d.Revision, "Copy")).Instances.Single();
            var other = copied.Presets.Single(x => x.Id != p.Id);
            Check(store.Execute(workspace, new(PluginAction.List, d.Id, other.Id)).Selected.Single().Id == "metamod", "Copy lost plugins.");
            var manifest = Path.Combine(workspace.DataRoot, "instances", d.Id.ToString("D"), "presets", p.Id.ToString("D"), "plugins.json");
            var validManifest = File.ReadAllText(manifest);
            File.WriteAllText(manifest, "{\"FormatVersion\":1,\"Plugins\":[{}]}");
            Reject(() => store.Execute(workspace, Read()), "HCSL-PLUGIN-ID");
            File.WriteAllText(manifest, validManifest);
            var frame = new LocalResponse(LocalProtocol.Version, Guid.NewGuid(), true, PluginResultJson: JsonSerializer.Serialize(new PluginResult(
                Enumerable.Range(0, 40).Select(i => new PluginPackageInfo("plugin-" + i, new string('n', 100), "1.0.0", new string('a', 64), 4096, long.MaxValue)).ToArray(),
                Enumerable.Range(0, 32).Select(i => new PluginSelectionInfo("plugin-" + i, new string('n', 100), "1.0.0", true, true)).ToArray(), 1, new string('a', 64))));
            Check(JsonSerializer.SerializeToUtf8Bytes(frame).Length < LocalProtocol.MaxFrameBytes - 1024, "Worst-case plugin frame too large.");
            if (OperatingSystem.IsLinux())
            {
                var linked = Path.Combine(workspace.LibraryRoot, "plugin-packages", "bad", "1.0.0");
                Directory.CreateDirectory(linked); File.CreateSymbolicLink(Path.Combine(linked, "plugin.json"), Path.Combine(package, "plugin.json"));
                var bad = store.Execute(workspace, Read());
                Check(bad.Available.All(x => x.Id != "bad"), "Linked package was listed.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS plugins.catalog-selection-conflict-disabled-copy-lock-links-frame");
    }
}
