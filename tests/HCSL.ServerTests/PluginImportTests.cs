using System.Formats.Tar;
using System.IO.Compression;
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
            WriteFile(sm, "cfg/sourcemod/sourcemod.cfg", "sm_test 1");
            var sourcemod = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, sm, "sourcemod", "SourceMod", "1.12.0"));
            var smPackage = Path.Combine(workspace.LibraryRoot, "plugin-packages", "sourcemod", "1.12.0");
            var meta = Path.Combine(smPackage, "files/csgo/addons/metamod/metaplugins.ini");
            Check(File.Exists(meta) && File.ReadAllText(meta).Contains("addons/sourcemod/bin/sourcemod_mm", StringComparison.Ordinal), "metaplugins.ini not generated.");
            Check(sourcemod.CurrentPlatformSupported, "SourceMod binary must count as a Linux binary.");
            Check(sourcemod.Warnings.Any(w => w.Contains("Skipped", StringComparison.Ordinal) && w.Contains("sourcemod.cfg", StringComparison.Ordinal)),
                "Mapped cfg files must be skipped with a warning.");
            Check(!Directory.Exists(Path.Combine(smPackage, "files/csgo/cfg")), "Skipped cfg files must not be written.");

            // 3) Windows-only package imports but warns on Linux.
            var win = Path.Combine(root, "win");
            WriteFile(win, "addons/sourcemod/bin/sourcemod_mm.dll");
            var windows = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, win, "sourcemod", "SourceMod", "9.9.9"));
            Check(!windows.CurrentPlatformSupported && windows.Warnings.Any(w => w.Contains("platform", StringComparison.OrdinalIgnoreCase)),
                "Windows-only package must warn on Linux.");
            Reject("HCSL-PLUGIN-PACKAGE", () => importer.Execute(workspace, new(Guid.Empty, Guid.Empty, win, "sourcemod", "SourceMod", "9.9.9")));
            importer.Execute(workspace, new(Guid.Empty, Guid.Empty, win, "sourcemod", "SourceMod", "9.9.9", Force: true));

            // 4) Mapped cfg files are skipped (not written); replacing the addons root is still rejected.
            var bad = Path.Combine(root, "bad");
            WriteFile(bad, "files/csgo/cfg/server.cfg");
            WriteFile(bad, "files/csgo/addons/demo/demo.txt");
            var skipped = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, bad, "bad", "Bad", "1.0.0"));
            Check(skipped.Warnings.Any(w => w.Contains("Skipped", StringComparison.Ordinal)), "Reserved cfg must be skipped with a warning.");
            Check(!File.Exists(Path.Combine(workspace.LibraryRoot, "plugin-packages", "bad", "1.0.0", "files/csgo/cfg/server.cfg")),
                "Skipped cfg must not be written.");
            var addonsRoot = Path.Combine(root, "addons-root");
            WriteFile(addonsRoot, "files/csgo/addons");
            Reject("HCSL-PLUGIN-PATH", () => importer.Execute(workspace, new(Guid.Empty, Guid.Empty, addonsRoot, "addonsroot", "Bad", "1.0.0")));

            // 5) CS:GO-era SourceMod (32-bit, no cs2 engine) imports but is flagged as unusable in CS2.
            var oldSm = Path.Combine(root, "sm-csgo");
            var logic = Path.Combine(oldSm, "addons/sourcemod/bin/sourcemod.logic.so");
            Directory.CreateDirectory(Path.GetDirectoryName(logic)!);
            File.WriteAllBytes(logic, [0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0]);
            WriteFile(oldSm, "addons/sourcemod/bin/sourcemod_mm.x64.so");
            var csgo = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, oldSm, "sourcemod", "SourceMod", "1.11.0"));
            Check(csgo.Warnings.Any(w => w.Contains("sourcemod.2.cs2.so", StringComparison.Ordinal)), "Missing CS2 engine warning.");
            Check(csgo.Warnings.Any(w => w.Contains("32-bit", StringComparison.Ordinal)), "32-bit binary warning missing.");

            // 6) Enabling uses the preset revision/hash path and appends at the end.
            var instances = new LocalInstanceStore();
            var entry = instances.Execute(workspace, new(InstanceAction.Create, Name: "Import")).Instances.Single();
            var preset = entry.Presets.Single();
            var enabled = importer.Execute(workspace, new(entry.Id, preset.Id, sm, "sourcemod", "SourceMod", "1.12.0",
                Enable: true, Force: true, InstanceRevision: entry.Definition!.Revision));
            Check(enabled.Enabled, "Enable after import failed: " + string.Join(";", enabled.Warnings));
            var selected = new LocalPluginStore().Execute(workspace, new(PluginAction.List, entry.Id, preset.Id)).Selected;
            Check(selected.Single().Id == "sourcemod" && selected.Single().Enabled, "Imported package not selected.");

            // 7) Archive import (.zip) with version detection from the archive name.
            var zip = Path.Combine(root, "source2mod-1.0.46-linux.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                void Entry(string name, string text)
                {
                    var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text); writer.Dispose();
                }
                Entry("addons/source2mod/bin/linuxsteamrt64/source2mod.so", "ELF");
                Entry("addons/metamod/source2mod.vdf", "\"Plugin\"{}");
                Entry("addons/source2mod/configs/core.example.json", "{}");
            }
            var probe = importer.Probe(workspace, new(zip));
            Check(probe.Kind == "archive" && probe.Id == "source2mod" && probe.Version == "1.0.46",
                $"archive probe wrong: {probe.Id}/{probe.Version}/{probe.Kind}");
            var fromZip = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, "", probe.Id, probe.Name, probe.Version,
                probe.MetaPlugin, ArchivePath: zip));
            Check(fromZip.Files >= 3 && Directory.Exists(Path.Combine(workspace.LibraryRoot, "plugin-packages", "source2mod", "1.0.46")),
                "archive import did not write the package.");

            // 7b) Version fallback from the binary when the archive name has none ("sm.tar.gz").
            var unnamed = Path.Combine(root, "sm.tar.gz");
            using (var file = File.Create(unnamed))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
            {
                var binary = System.Text.Encoding.ASCII.GetBytes("pad\0Source2Mod v1.0.46 @ 0613502\0pad");
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "addons/source2mod/bin/linuxsteamrt64/source2mod.so") { DataStream = new MemoryStream(binary) });
            }
            var unnamedProbe = importer.Probe(workspace, new(unnamed));
            Check(unnamedProbe.Id == "source2mod" && unnamedProbe.Version == "1.0.46",
                $"binary version fallback failed: {unnamedProbe.Id}/{unnamedProbe.Version}");

            // 8) .tar.gz import and version detection from a directory name with a git suffix.
            var tarPath = Path.Combine(root, "mmsource-2.0.0-git1234-linux.tar.gz");
            using (var file = File.Create(tarPath))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
            {
                void Add(string name, string text)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });
                }
                Add("addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so", "ELF");
                Add("addons/metamod/metaplugins.ini", "");
            }
            var tarProbe = importer.Probe(workspace, new(tarPath));
            Check(tarProbe.Kind == "archive" && tarProbe.Id == "metamod" && tarProbe.Version == "2.0.0-git1234",
                $"tar probe wrong: {tarProbe.Id}/{tarProbe.Version}");
            importer.Execute(workspace, new(Guid.Empty, Guid.Empty, "", tarProbe.Id, tarProbe.Name, tarProbe.Version, ArchivePath: tarPath));

            // 9) Traversal and link entries are refused.
            var evil = Path.Combine(root, "evil.zip");
            using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("x");
            Reject("HCSL-PLUGIN-PATH", () => importer.Probe(workspace, new(evil)));
            var linkTar = Path.Combine(root, "link.tar.gz");
            using (var file = File.Create(linkTar))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "addons/link") { LinkName = "/etc/passwd" });
            Reject("HCSL-PLUGIN-PACKAGE", () => importer.Probe(workspace, new(linkTar)));

            // 10) A packaged plugin.json supplies identity when present.
            var packaged = Path.Combine(root, "mmsource-2.0.0-linux");
            WriteFile(packaged, "plugin.json", "{\"FormatVersion\":1,\"Id\":\"metamod\",\"Name\":\"Metamod:Source\",\"Version\":\"2.0.0\"}");
            WriteFile(packaged, "files/csgo/addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so");
            var packagedProbe = importer.Probe(workspace, new(packaged));
            Check(packagedProbe.Id == "metamod" && packagedProbe.Version == "2.0.0", "packaged manifest identity not used.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS plugins.import-layouts-archives-probe-traversal-and-enable");
    }
}
