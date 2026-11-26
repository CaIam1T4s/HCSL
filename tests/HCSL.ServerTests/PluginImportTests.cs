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

    private static void CheckReceipt(PluginImportResult result, string id, string name, string version, string framework, string relativeFile)
    {
        Check(result.Package is { FrameworkPayload: false } package && package.Id == id && package.Name == name &&
            package.Version == version && package.Framework == framework && !result.Enabled,
            $"Unexpected library-only receipt for {id}: {result.Package}");
        Check(File.Exists(Path.Combine(result.PackagePath, "files", relativeFile)), $"Missing imported file: {relativeFile}");
    }

    public static void Run()
    {
        string[] baseline = ["    Game_LowViolence csgo_lv", "    Game csgo", "    Game core"];
        const string metamodPath = "csgo/addons/metamod", swiftlyPath = "csgo/addons/swiftlys2";
        var combined = LocalPluginImporter.BuildGameInfoOverlay(baseline, [swiftlyPath, metamodPath, swiftlyPath]);
        Check(combined.SequenceEqual([baseline[0], "    Game\t" + metamodPath, "    Game\t" + swiftlyPath, .. baseline.Skip(1)]),
            "Framework search paths were not deduplicated and ordered after Game_LowViolence.");
        Check(LocalPluginImporter.BuildGameInfoOverlay(baseline, [metamodPath, swiftlyPath]).SequenceEqual(combined), "Selection order affected gameinfo.");
        Check(LocalPluginImporter.BuildGameInfoOverlay(baseline, [swiftlyPath]).SequenceEqual(combined.Where(line => line != "    Game\t" + metamodPath)),
            "Disabling Metamod changed unrelated gameinfo paths.");
        var root = Path.Combine(Path.GetTempPath(), "hcsl-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            var importer = new LocalPluginImporter();
            Check(FrameworkRules.CategoryFor("metamod", frameworkPayload: false) == PluginCategory.Metamod, "Metamod native plugin category wrong.");
            Check(FrameworkRules.CategoryFor("metamod", frameworkPayload: true) == PluginCategory.Framework, "Metamod framework must not enter the plugin category.");
            Check(FrameworkRules.ClassifyManagedDependencies(["ModSharp.Sharp.Shared"]) == ManagedPluginFramework.ModSharp, "ModSharp deps classification wrong.");
            Check(FrameworkRules.ClassifyManagedDependencies(["SwiftlyS2.CS2"]) == ManagedPluginFramework.SwiftlyS2, "Swiftly deps classification wrong.");
            Check(FrameworkRules.ClassifyManagedDependencies(["ModSharp.Sharp.Shared", "SwiftlyS2.CS2"]) == ManagedPluginFramework.Ambiguous, "Ambiguous deps classification wrong.");
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

            // 5b) A CounterStrikeSharp sub-plugin must not claim the framework id.
            var mz = Path.Combine(root, "matchzy-0.9.3");
            WriteFile(mz, "addons/counterstrikesharp/plugins/MatchZy/MatchZy.dll", "binary");
            WriteFile(mz, "addons/counterstrikesharp/gamedata/matchzy.json", "{}");
            var mzProbe = importer.Probe(workspace, new(mz));
            Check(mzProbe.Id == "matchzy" && mzProbe.Name == "MatchZy", "CSSharp sub-plugin was detected as the framework: " + mzProbe.Id + "/" + mzProbe.Name);
            Check(mzProbe.Version == "0.9.3" && mzProbe.Framework == "counterstrikesharp", "CSSharp probe lost its version or framework.");
            var mzImport = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, mz, mzProbe.Id, mzProbe.Name, mzProbe.Version));
            CheckReceipt(mzImport, "matchzy", "MatchZy", "0.9.3", "counterstrikesharp", "csgo/addons/counterstrikesharp/plugins/MatchZy/MatchZy.dll");
            var kz = Path.Combine(root, "cs2kz-linux-master-upgrade");
            WriteFile(kz, "addons/metamod/cs2kz.vdf", "\"Plugin\" { \"file\" \"addons/cs2kz/bin/cs2kz\" }");
            WriteFile(kz, "addons/cs2kz/bin/cs2kz.so", "binary");
            WriteFile(kz, "cfg/cs2kz.cfg", "// preset config");
            var kzProbe = importer.Probe(workspace, new(kz));
            Check(kzProbe.Id == "cs2kz" && kzProbe.Name == "CS2KZ" && kzProbe.Version == "" && kzProbe.Framework == "metamod",
                $"CS2KZ probe wrong: {kzProbe.Id}/{kzProbe.Name}/{kzProbe.Version}/{kzProbe.Framework}");
            var kzCommand = new PluginImportCommand(Guid.Empty, Guid.Empty, kz, kzProbe.Id, kzProbe.Name, "0.0.200");
            var kzImport = importer.Execute(workspace, kzCommand);
            CheckReceipt(kzImport, "cs2kz", "CS2KZ", "0.0.200", "metamod", "csgo/addons/cs2kz/bin/cs2kz.so");
            Check(!File.Exists(Path.Combine(kzImport.PackagePath, "files/csgo/cfg/cs2kz.cfg")), "Library-only import copied preset CFG into plugin payload.");
            Reject("HCSL-PLUGIN-PACKAGE", () => importer.Execute(workspace, kzCommand with { Name = "Overwrite attempt" }));
            Check(File.ReadAllText(Path.Combine(kzImport.PackagePath, "plugin.json")).Contains("CS2KZ", StringComparison.Ordinal),
                "Duplicate import changed the existing package manifest.");
            var cssFramework = Path.Combine(root, "css");
            WriteFile(cssFramework, "addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so", "binary");
            WriteFile(cssFramework, "addons/counterstrikesharp/api/CounterStrikeSharp.API.dll", "binary");
            Check(importer.Probe(workspace, new(cssFramework)).Id == "counterstrikesharp", "Real CSSharp framework was not detected.");

            // 5e) Raw SwiftlyS2 plugin directory: one DLL + deps + resources, no addons wrapper.
            var swiftlyPlugin = Path.Combine(root, "HanAirDropS2");
            WriteFile(swiftlyPlugin, "HanAirDropS2.dll", "managed");
            WriteFile(swiftlyPlugin, "HanAirDropS2.deps.json", "{\"targets\":{\".NETCoreApp,Version=v10.0\":{\"HanAirDropS2/1.0.0\":{\"dependencies\":{\"SwiftlyS2.CS2\":\"1.2.2\"}}}}}");
            WriteFile(swiftlyPlugin, "resources/translations/en.jsonc", "{}");
            var swiftlyPluginProbe = importer.Probe(workspace, new(swiftlyPlugin));
            Check(swiftlyPluginProbe.Id == "hanairdrops2" && swiftlyPluginProbe.Version == "1.0.0"
                && swiftlyPluginProbe.Framework == "swiftlys2" && swiftlyPluginProbe.TargetRoot == "csgo/addons/swiftlys2/plugins",
                "Swiftly plugin probe wrong: " + swiftlyPluginProbe.Id + "/" + swiftlyPluginProbe.Version + "/" + swiftlyPluginProbe.Framework);
            var swiftlyPluginImport = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, swiftlyPlugin, "hanairdrops2", "HanAirDropS2", "1.0.0"));
            CheckReceipt(swiftlyPluginImport, "hanairdrops2", "HanAirDropS2", "1.0.0", "swiftlys2", "csgo/addons/swiftlys2/plugins/HanAirDropS2/HanAirDropS2.dll");
            Check(swiftlyPluginImport.CurrentPlatformSupported, "Swiftly managed plugin was treated as unsupported.");
            Check(File.Exists(Path.Combine(swiftlyPluginImport.PackagePath, "files", "csgo", "addons", "swiftlys2", "plugins", "HanAirDropS2", "HanAirDropS2.dll")),
                "Swiftly plugin was not wrapped under plugins/<Id>/.");

            // 5f) Managed ModSharp plugin: same DLL+deps shape, classified from deps.json.
            var joinSound = Path.Combine(root, "JoinSound");
            WriteFile(joinSound, "JoinSound.dll", "managed");
            WriteFile(joinSound, "JoinSound.deps.json", "{\"targets\":{\".NETCoreApp,Version=v9.0\":{\"JoinSound/1.2\":{\"dependencies\":{\"ModSharp.Sharp.Shared\":\"2.0.4\"}}}}}");
            var joinProbe = importer.Probe(workspace, new(joinSound));
            Check(joinProbe.Id == "joinsound" && joinProbe.Framework == "modsharp" && joinProbe.TargetRoot == "sharp/modules",
                "ModSharp plugin probe wrong: " + joinProbe.Id + "/" + joinProbe.Framework + "/" + joinProbe.TargetRoot);
            var joinImport = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, joinSound, "joinsound", "JoinSound", "1.2"));
            CheckReceipt(joinImport, "joinsound", joinProbe.Name, joinProbe.Version, "modsharp", "sharp/modules/JoinSound/JoinSound.dll");
            Check(joinImport.Framework == "modsharp" && File.Exists(Path.Combine(joinImport.PackagePath, "files", "sharp", "modules", "JoinSound", "JoinSound.dll")),
                "ModSharp plugin was not wrapped under sharp/modules/<Id>/.");
            var unknownManaged = Path.Combine(root, "unknown-managed");
            WriteFile(unknownManaged, "Unknown.dll", "managed");
            WriteFile(unknownManaged, "Unknown.deps.json", "{}");
            var unknownProbe = importer.Probe(workspace, new(unknownManaged));
            Check(unknownProbe.ManagedFramework == ManagedPluginFramework.Unknown && unknownProbe.Framework is null, "Unknown managed plugin was classified without framework evidence.");
            Reject("HCSL-PLUGIN-MANAGED-FRAMEWORK-UNKNOWN", () => importer.Execute(workspace, new(Guid.Empty, Guid.Empty, unknownManaged, "unknown", "Unknown", "1.0.0")));
            var ambiguousManaged = Path.Combine(root, "ambiguous-managed");
            WriteFile(ambiguousManaged, "Ambiguous.dll", "managed");
            WriteFile(ambiguousManaged, "Ambiguous.deps.json", "{\"targets\":{\".NETCoreApp,Version=v10.0\":{\"Ambiguous/1.0.0\":{\"dependencies\":{\"SwiftlyS2.CS2\":\"1.2.2\",\"ModSharp.Sharp.Shared\":\"2.0.4\"}}}}}");
            var ambiguousProbe = importer.Probe(workspace, new(ambiguousManaged));
            Check(ambiguousProbe.ManagedFramework == ManagedPluginFramework.Ambiguous && ambiguousProbe.Framework is null,
                "Ambiguous managed plugin was not reported as ambiguous.");
            Reject("HCSL-PLUGIN-MANAGED-FRAMEWORK-AMBIGUOUS", () => importer.Execute(workspace, new(Guid.Empty, Guid.Empty, ambiguousManaged, "ambiguous", "Ambiguous", "1.0.0")));
            var ambiguousExplicit = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, ambiguousManaged, "ambiguous", "Ambiguous", "1.0.0", Framework: "modsharp"));
            Check(ambiguousExplicit.Framework == "modsharp" && File.Exists(Path.Combine(ambiguousExplicit.PackagePath, "files", "sharp", "modules", "Ambiguous", "Ambiguous.dll")),
                "Explicit framework did not resolve an ambiguous managed plugin.");

            // 5d) Wrapper directory (SwiftlyS2) and game-root sharp/ (ModSharp) layouts.
            var swiftly = Path.Combine(root, "swiftly");
            WriteFile(swiftly, "swiftly-wrapper/addons/swiftlys2/bin/linuxsteamrt64/swiftlys2.so", "binary");
            var swiftlyProbe = importer.Probe(workspace, new(Path.Combine(swiftly, "swiftly-wrapper")));
            Check(swiftlyProbe.Id == "swiftlys2", "SwiftlyS2 wrapper directory was not recognized: " + swiftlyProbe.Id);
            var swiftlyImport = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, swiftly, "swiftlys2", "SwiftlyS2", "1.5.2-beta.13"));
            Check(File.Exists(Path.Combine(swiftlyImport.PackagePath, "files", "csgo", "addons", "swiftlys2", "bin", "linuxsteamrt64", "swiftlys2.so")),
                "SwiftlyS2 wrapper was not flattened.");
            Check(File.ReadAllText(Path.Combine(swiftlyImport.PackagePath, "files", "csgo", "gameinfo.gi")).Contains("Game\tcsgo/addons/swiftlys2", StringComparison.Ordinal),
                "SwiftlyS2 gameinfo overlay missing.");
            var modsharp = Path.Combine(root, "modsharp");
            WriteFile(modsharp, "sharp/bin/libmodsharp.so", "binary");
            WriteFile(modsharp, "sharp/configs/core.json", "{}");
            var modsharpImport = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, modsharp, "modsharp", "ModSharp", "git224"));
            Check(File.Exists(Path.Combine(modsharpImport.PackagePath, "files", "sharp", "bin", "libmodsharp.so")), "ModSharp sharp/ root was not kept.");
            Check(File.ReadAllText(Path.Combine(modsharpImport.PackagePath, "files", "csgo", "gameinfo.gi")).Contains("Game\tsharp", StringComparison.Ordinal),
                "ModSharp gameinfo overlay missing.");

            // 5c) A bare .smx becomes an independent Source2Mod sub-plugin package.
            var smx = Path.Combine(root, "sp_sample.smx");
            File.WriteAllText(smx, "SourcePawn binary");
            var smxProbe = importer.Probe(workspace, new(smx));
            Check(smxProbe.Kind == "file" && smxProbe.Id == "sp_sample" && smxProbe.Framework == "source2mod", ".smx probe did not produce a file package.");
            var smxImport = importer.Execute(workspace, new(Guid.Empty, Guid.Empty, smx, "sp_sample", "sp_sample", "1.0.0"));
            CheckReceipt(smxImport, smxProbe.Id, smxProbe.Name, smxProbe.Version, "source2mod", "csgo/addons/source2mod/plugins/sp_sample.smx");
            Check(smxImport.CurrentPlatformSupported && File.Exists(Path.Combine(smxImport.PackagePath, "files", "csgo", "addons", "source2mod", "plugins", "sp_sample.smx")),
                ".smx was not stored under source2mod/plugins.");

            // 6) Enabling uses the preset revision/hash path and appends at the end.
            var instances = new LocalInstanceStore();
            var entry = instances.Execute(workspace, new(InstanceAction.Create, Name: "Import")).Instances.Single();
            var preset = entry.Presets.Single();
            var enabled = importer.Execute(workspace, new(entry.Id, preset.Id, sm, "sourcemod", "SourceMod", "1.12.0",
                Enable: true, Force: true, InstanceRevision: entry.Definition!.Revision));
            Check(enabled.Enabled, "Enable after import failed: " + string.Join(";", enabled.Warnings));
            var selected = new LocalPluginStore().Execute(workspace, new(PluginAction.List, entry.Id, preset.Id)).Selected;
            Check(selected.Single().Id == "sourcemod" && selected.Single().Enabled, "Imported package not selected.");

            // 6b) Opt-in cfg copy keeps nested paths and records provenance.
            var cfgAdd = Path.Combine(root, "cfgadd");
            WriteFile(cfgAdd, "addons/source2mod/plugins/cfgadd.smx", "binary");
            WriteFile(cfgAdd, "cfg/pt/map/x.cfg", "echo plugin");
            WriteFile(cfgAdd, "cfg/MatchZy/admins.json", "{}");
            var cfgImported = importer.Execute(workspace, new(entry.Id, preset.Id, cfgAdd, "cfgadd", "Cfg Add", "1.0.0",
                InstanceRevision: entry.Definition!.Revision, CfgIntoPreset: true));
            Check(cfgImported.ImportedCfg == 2, "Opt-in cfg import did not copy nested files: " + cfgImported.ImportedCfg + " / " + string.Join(" | ", cfgImported.Warnings));
            var cfgRoot = Path.Combine(workspace.DataRoot, "instances", entry.Id.ToString("D"), "presets", preset.Id.ToString("D"), "cfg");
            Check(File.ReadAllText(Path.Combine(cfgRoot, "pt", "map", "x.cfg")) == "echo plugin", "Nested cfg not imported.");
            Check(File.Exists(Path.Combine(workspace.DataRoot, "instances", entry.Id.ToString("D"), "presets", preset.Id.ToString("D"), ".hcsl-cfg-ledger.json")), "Cfg provenance missing.");
            File.WriteAllText(Path.Combine(cfgRoot, "pt", "map", "x.cfg"), "echo user");
            var cfgAgain = importer.Execute(workspace, new(entry.Id, preset.Id, cfgAdd, "cfgadd", "Cfg Add", "1.0.0",
                Force: true, InstanceRevision: entry.Definition!.Revision, CfgIntoPreset: true));
            Check(cfgAgain.ImportedCfg == 0 && File.ReadAllText(Path.Combine(cfgRoot, "pt", "map", "x.cfg")) == "echo user", "Re-import overwrote user cfg.");

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

            // 7c) Compiler/runtime version strings must not win over the plugin's own version.
            var metamodTar = Path.Combine(root, "mm.tar.gz");
            using (var file = File.Create(metamodTar))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new TarWriter(gzip))
            {
                var binary = System.Text.Encoding.ASCII.GetBytes(
                    "Debian clang version 22.1.8 (1~deb11u1)\0google8protobuf8internalContainer\0 2.0.0-dev+1490\0"
                    + "Plugin uses old SourceHook Metamod build, probably 1.12.x or an early 2.0 version (%d < %d).\0");
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so") { DataStream = new MemoryStream(binary) });
            }
            var metamodProbe = importer.Probe(workspace, new(metamodTar));
            Check(metamodProbe.Id == "metamod" && metamodProbe.Version == "2.0.0-dev+1490",
                $"compiler version won over the plugin version: {metamodProbe.Version}");

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
