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
            Directory.CreateDirectory(Path.Combine(package, "files", "csgo", "addons", "metamod", "bin", "linuxsteamrt64"));
            File.WriteAllText(Path.Combine(package, "files", "csgo", "addons", "metamod", "bin", "linuxsteamrt64", "metamod.2.cs2.so"), "binary");
            File.WriteAllText(Path.Combine(package, "files", "csgo", "gameinfo.gi"), "patched");
            var available = store.Execute(workspace, Read());
            Check(available.Available.Length == 1 && available.Available[0].Id == "metamod" && available.Available[0].Files == 2, "Package catalog.");
            var cachedHash = store.Execute(workspace, Read()).Available[0].Hash;
            Check(cachedHash == available.Available[0].Hash, "Repeated catalog read changed the package hash.");
            var binary = Path.Combine(package, "files", "csgo", "addons", "metamod", "bin", "linuxsteamrt64", "metamod.2.cs2.so");
            File.WriteAllText(binary, "binary2");
            File.SetLastWriteTimeUtc(binary, DateTime.UtcNow.AddSeconds(5));
            Check(store.Execute(workspace, Read()).Available[0].Hash != cachedHash, "Edited package was not re-hashed.");
            var broken = Path.Combine(workspace.LibraryRoot, "plugin-packages", "broken", "0.1.0");
            Directory.CreateDirectory(Path.Combine(broken, "files"));
            File.WriteAllText(Path.Combine(broken, "plugin.json"), "{\"FormatVersion\":1}");
            var withBroken = store.Execute(workspace, Read());
            Check(withBroken.Available.Length == 1 && withBroken.Rejected is { Length: 1 } rejected
                && rejected[0].Id == "broken" && rejected[0].Version == "0.1.0" && rejected[0].Code == "HCSL-PLUGIN-PACKAGE", "Invalid package was not reported.");
            Directory.Delete(Path.Combine(workspace.LibraryRoot, "plugin-packages", "broken"), true);
            // Listing is read-only; the explicit save path migrates legacy managed packages.
            var legacyManaged = Path.Combine(workspace.LibraryRoot, "plugin-packages", "joinsound", "1.0.0");
            Directory.CreateDirectory(Path.Combine(legacyManaged, "files", "csgo", "addons", "swiftlys2", "plugins", "JoinSound"));
            File.WriteAllText(Path.Combine(legacyManaged, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"joinsound\",\"Name\":\"JoinSound\",\"Version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(legacyManaged, "files", "csgo", "addons", "swiftlys2", "plugins", "JoinSound", "JoinSound.dll"), "managed");
            File.WriteAllText(Path.Combine(legacyManaged, "files", "csgo", "addons", "swiftlys2", "plugins", "JoinSound", "JoinSound.deps.json"),
                "{\"targets\":{\".NETCoreApp,Version=v9.0\":{\"JoinSound/1.0.0\":{\"dependencies\":{\"ModSharp.Sharp.Shared\":\"2.0.4\"}}}}}");
            var listed = store.Execute(workspace, Read());
            Check(listed.Available.Single(a => a.Id == "joinsound").Category == PluginCategory.SwiftlyS2 &&
                File.Exists(Path.Combine(legacyManaged, "files/csgo/addons/swiftlys2/plugins/JoinSound/JoinSound.dll")),
                "Read-only list changed or lost the legacy package.");
            var command = new PluginCommand(PluginAction.Save, d.Id, p.Id, d.Revision, empty.Hash,
                [new("metamod", "1.2.3", true)]);
            var saved = store.Execute(workspace, command);
            Check(File.Exists(Path.Combine(legacyManaged, "files", "sharp", "modules", "JoinSound", "JoinSound.dll")),
                "Legacy managed package was not migrated to its framework directory.");
            Check(!File.Exists(Path.Combine(legacyManaged, "files", "csgo", "addons", "swiftlys2", "plugins", "JoinSound", "JoinSound.dll")),
                "Legacy managed package stayed in the wrong framework directory.");
            Check(saved.Available.Single(a => a.Id == "joinsound").Category == PluginCategory.ModSharp,
                "Migrated package category was not refreshed.");
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
            // Framework gate: one scripting framework per preset, and its base/loader must be selected.
            void FrameworkPackage(string id, string version, string relative)
            {
                var dir = Path.Combine(workspace.LibraryRoot, "plugin-packages", id, version);
                Directory.CreateDirectory(Path.Combine(dir, "files", Path.GetDirectoryName(relative) ?? ""));
                File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""{"FormatVersion":1,"Id":"{{id}}","Name":"{{id}}","Version":"{{version}}"}""");
                File.WriteAllText(Path.Combine(dir, "files", relative), "binary");
            }
            FrameworkPackage("counterstrikesharp", "1.0.0", "csgo/addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so");
            FrameworkPackage("source2mod", "1.0.0", "csgo/addons/source2mod/bin/linuxsteamrt64/source2mod.so");
            FrameworkPackage("matchzy", "0.9.2", "csgo/addons/counterstrikesharp/plugins/MatchZy/MatchZy.dll");
            var gateRevision = instances.Execute(workspace, new(InstanceAction.List)).Instances.Single(x => x.Id == d.Id).Definition!.Revision;
            var gateBase = store.Execute(workspace, Read());
            var metamodSelection = gateBase.Selected.Where(s => s.Id == "metamod").Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
            Reject(() => store.Execute(workspace, new(PluginAction.Save, d.Id, p.Id, gateRevision, gateBase.Hash,
                [.. metamodSelection, new("counterstrikesharp", "1.0.0"), new("source2mod", "1.0.0")])), "HCSL-PLUGIN-FRAMEWORK-CONFLICT");
            Reject(() => store.Execute(workspace, new(PluginAction.Save, d.Id, p.Id, gateRevision, gateBase.Hash,
                [.. metamodSelection, new("matchzy", "0.9.2")])), "HCSL-PLUGIN-FRAMEWORK-MISSING");
            Reject(() => store.Execute(workspace, new(PluginAction.Save, d.Id, p.Id, gateRevision, gateBase.Hash,
                [new("source2mod", "1.0.0")])), "HCSL-PLUGIN-FRAMEWORK-MISSING");
            var cssSelection = store.Execute(workspace, new(PluginAction.Save, d.Id, p.Id, gateRevision, gateBase.Hash,
                [.. metamodSelection, new("counterstrikesharp", "1.0.0"), new("matchzy", "0.9.2")]));
            Check(cssSelection.Selected.Length == 3, "CSSharp selection with its plugin was rejected.");
            var s2mSelection = store.Execute(workspace, new(PluginAction.Save, d.Id, p.Id, gateRevision, cssSelection.Hash,
                [.. metamodSelection, new("source2mod", "1.0.0")]));
            Check(s2mSelection.Selected.Length == 2, "Source2Mod selection with Metamod was rejected.");
            var disabledConflict = store.Execute(workspace, new(PluginAction.Save, d.Id, p.Id, gateRevision, s2mSelection.Hash,
                [.. metamodSelection, new("source2mod", "1.0.0"), new("counterstrikesharp", "1.0.0", false)]));
            Check(disabledConflict.Selected.Count(s => s.Enabled) == 2, "A disabled conflicting framework was rejected.");
            // Plugin cfg sync: preview/apply/cleanup with provenance and no silent overwrite.
            var cfgRevision = instances.Execute(workspace, new(InstanceAction.List)).Instances.Single(x => x.Id == d.Id).Definition!.Revision;
            var cfgPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "cfgplug", "1.0.0");
            Directory.CreateDirectory(Path.Combine(cfgPackage, "files", "csgo", "addons", "source2mod"));
            Directory.CreateDirectory(Path.Combine(cfgPackage, "cfg", "pt", "map"));
            Directory.CreateDirectory(Path.Combine(cfgPackage, "cfg", "MatchZy"));
            File.WriteAllText(Path.Combine(cfgPackage, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"cfgplug\",\"Name\":\"cfgplug\",\"Version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(cfgPackage, "files", "csgo", "addons", "source2mod", "x.smx"), "binary");
            File.WriteAllText(Path.Combine(cfgPackage, "cfg", "pt", "map", "x.cfg"), "echo plugin");
            File.WriteAllText(Path.Combine(cfgPackage, "cfg", "MatchZy", "admins.json"), "{}");
            var sync = new LocalPluginCfgSync();
            var syncSelection = new[] { new PluginSelection("cfgplug", "1.0.0") };
            var cfgPreview = sync.Preview(workspace, new(PluginCfgAction.Preview, d.Id, p.Id, cfgRevision, syncSelection, null));
            Check(cfgPreview.Import.Length == 2 && cfgPreview.Conflicts.Length == 0, "Cfg preview did not list new files.");
            var cfgApplied = sync.Apply(workspace, new(PluginCfgAction.Apply, d.Id, p.Id, cfgRevision, syncSelection, null, []));
            Check(cfgApplied.Imported == 2, "Cfg apply did not import new files.");
            var presetCfg = Path.Combine(workspace.DataRoot, "instances", d.Id.ToString("D"), "presets", p.Id.ToString("D"), "cfg");
            var nestedCfg = Path.Combine(presetCfg, "pt", "map", "x.cfg");
            Check(File.ReadAllText(nestedCfg) == "echo plugin", "Nested cfg was not written.");
            File.WriteAllText(nestedCfg, "echo user");
            var cleanupPreview = sync.CleanupPreview(workspace, new(PluginCfgAction.CleanupPreview, d.Id, p.Id, cfgRevision, null, syncSelection));
            Check(cleanupPreview.Removable.Length == 1 && cleanupPreview.Modified.Length == 1, "Cleanup preview did not separate modified files.");
            var cleanup = sync.CleanupApply(workspace, new(PluginCfgAction.CleanupApply, d.Id, p.Id, cfgRevision, null, syncSelection, null, [cleanupPreview.Removable[0].Path]));
            Check(cleanup.Deleted == 1 && !File.Exists(Path.Combine(presetCfg, "MatchZy", "admins.json")) && File.Exists(nestedCfg), "Cleanup deleted the wrong cfg files.");
            var conflictPreview = sync.Preview(workspace, new(PluginCfgAction.Preview, d.Id, p.Id, cfgRevision, syncSelection, null));
            Check(conflictPreview.Conflicts.Length == 1 && conflictPreview.Conflicts[0].Path == "pt/map/x.cfg", "Existing modified cfg was not reported as a conflict.");
            var overwritten = sync.Apply(workspace, new(PluginCfgAction.Apply, d.Id, p.Id, cfgRevision, syncSelection, null, ["pt/map/x.cfg"]));
            Check(overwritten.Overwritten == 1 && File.ReadAllText(nestedCfg) == "echo plugin", "Confirmed cfg overwrite did not apply.");
            var oldPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "oldpkg", "9.9.9");
            Directory.CreateDirectory(Path.Combine(oldPackage, "files"));
            File.WriteAllText(Path.Combine(oldPackage, "plugin.json"), "{\"FormatVersion\":1,\"Id\":\"oldpkg\",\"Name\":\"oldpkg\",\"Version\":\"9.9.9\"}");
            store.Execute(workspace, new(PluginAction.RemovePackage, Guid.Empty, Guid.Empty, RemoveId: "oldpkg", RemoveVersion: "9.9.9"));
            Check(!Directory.Exists(oldPackage), "Unused package version was not removed.");
            Reject(() => store.Execute(workspace, new(PluginAction.RemovePackage, Guid.Empty, Guid.Empty,
                RemoveId: "counterstrikesharp", RemoveVersion: "1.0.0")), "HCSL-PLUGIN-IN-USE");
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
