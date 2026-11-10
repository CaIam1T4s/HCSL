using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;

namespace HCSL.ServerTests;

/// <summary>Importer contract: game-root layouts, generated overlays, platform warnings, limits and enabling.</summary>
public static class PluginImportTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (InstanceException e) when (e.Code == code) { }
        catch (WorkspaceException e) when (e.Code == code) { }
    }
    private static void WriteFile(string root, string relative, string content = "x")
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "hcsl-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            var importer = new LocalPluginImporter();
            // Create the workspace markers first; a nonempty unmarked library is refused by design.
            using (new LocalWorkspaceManager().Acquire(workspace)) { }
            // Base installation so a Metamod import can generate the gameinfo overlay.
            WriteFile(workspace.LibraryRoot, "server/game/csgo/gameinfo.gi", "\"GameInfo\"\n{\n\tSearchPaths\n\t{\n\t\tGame\tcsgo\n\t}\n}\n");

            // 1) Metamod release root (addons/...): overlay generated, no metaplugins line by default.
            var mm = Path.Combine(root, "mm");
            WriteFile(mm, "addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so");
            WriteFile(mm, "addons/metamod/metaplugins.ini", "");
            var metamod = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, mm, "metamod", "Metamod:Source", "2.0.0"));
            Check(metamod.CurrentPlatformSupported && metamod.Files >= 3, "Metamod import not supported/complete.");
            var mmPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "metamod", "2.0.0");
            Check(File.Exists(Path.Combine(mmPackage, "plugin.json")), "plugin.json missing.");
            Check(File.Exists(Path.Combine(mmPackage, "files/csgo/addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so")), "addons tree not copied.");
            Check(File.ReadAllText(Path.Combine(mmPackage, "files/csgo/gameinfo.gi")).Contains("csgo/addons/metamod", StringComparison.Ordinal), "gameinfo overlay missing.");
            Check(metamod.Warnings.Any(w => w.Contains("gameinfo", StringComparison.Ordinal)), "gameinfo warning missing.");

            // 2) SourceMod release: metaplugins.ini generated and current-platform binary recognized.
            var sm = Path.Combine(root, "sm");
            WriteFile(sm, "addons/sourcemod/bin/sourcemod_mm");
            WriteFile(sm, "addons/sourcemod/translations/common.phrases");
            var sourcemod = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, sm, "sourcemod", "SourceMod", "1.12.0"));
            var smPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "sourcemod", "1.12.0");
            var meta = Path.Combine(smPackage, "files/csgo/addons/metamod/metaplugins.ini");
            Check(File.Exists(meta) && File.ReadAllText(meta).Contains("addons/sourcemod/bin/sourcemod_mm", StringComparison.Ordinal), "metaplugins.ini not generated.");
            Check(sourcemod.CurrentPlatformSupported, "SourceMod binary must count as a Linux binary.");

            // 3) Windows-only package imports but warns on Linux.
            var win = Path.Combine(root, "win");
            WriteFile(win, "addons/sourcemod/bin/sourcemod_mm.dll");
            var windows = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, win, "sourcemod", "SourceMod", "9.9.9"));
            Check(!windows.CurrentPlatformSupported && windows.Warnings.Any(w => w.Contains("platform", StringComparison.OrdinalIgnoreCase)),
                "Windows-only package must warn on Linux.");
            Reject("HCSL-PLUGIN-PACKAGE", () => importer.Execute(workspace, new(Guid.Empty, Guid.Empty, win, "sourcemod", "SourceMod", "9.9.9")));
            importer.Execute(workspace, new(Guid.Empty, Guid.Empty, win, "sourcemod", "SourceMod", "9.9.9", Force: true));

            // 4) Reserved mapped paths are rejected before anything is written.
            var bad = Path.Combine(root, "bad");
            WriteFile(bad, "files/csgo/cfg/server.cfg");
            Reject("HCSL-PLUGIN-PATH", () => importer.Execute(workspace, new(Guid.Empty, Guid.Empty, bad, "bad", "Bad", "1.0.0")));

            // 5) Enabling uses the preset revision/hash path and appends at the end.
            var instances = new LocalInstanceStore();
            var entry = instances.Execute(workspace, new(InstanceAction.Create, Name: "Import")).Instances.Single();
            var preset = entry.Presets.Single();
            var enabled = importer.Execute(workspace, new(entry.Id, preset.Id, sm, "sourcemod", "SourceMod", "1.12.0",
                Enable: true, Force: true, InstanceRevision: entry.Definition!.Revision));
            Check(enabled.Enabled, "Enable after import failed: " + string.Join(";", enabled.Warnings));
            var selected = new LocalPluginStore().Execute(workspace, new(PluginAction.List, entry.Id, preset.Id)).Selected;
            Check(selected.Single().Id == "sourcemod" && selected.Single().Enabled, "Imported package not selected.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS plugins.import-layouts-overlays-platform-warnings-limits-and-enable");
    }
}
