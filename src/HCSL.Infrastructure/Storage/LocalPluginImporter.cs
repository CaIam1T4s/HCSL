using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Copies a plugin release (extracted directory, .zip, .tar, .tar.gz) into
/// &lt;library&gt;/plugin-packages/&lt;id&gt;/&lt;version&gt;. Platform-neutral: the same layout is accepted on
/// Linux and Windows; missing binaries for the current OS are warnings, not errors.
/// </summary>
public sealed class LocalPluginImporter : IPluginImporter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };
    private static readonly Regex VersionPattern = new(@"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:-git\d+)?(?:-(?:dev|beta|stable|rc\d*))?(?:\+[A-Za-z0-9.]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EmbeddedToken = new(@"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:-(?:dev|beta|stable|rc\d*))?(?:\+[A-Za-z0-9.]+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CompilerToken = new(@"(?i)clang|gcc|compiler|debian|ubuntu|glibc|openssl|libressl|curl|zlib|protobuf|python|cmake|boost");
    private static readonly Regex ProseToken = new(@"(?i)probably|requires|required|older|newer|\buses\b|failed|cannot|early|plugin uses|plugin requires|interface version|build\b");

    public PluginImportResult Execute(WorkspaceRequest workspace, PluginImportCommand command)
    {
        PluginRules.Id(command.Id); PluginRules.Version(command.Version);
        var name = (command.Name ?? "").Trim();
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
            throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin name must be 1-100 visible characters.");

        var generated = new List<SourceFile>();
        var allFiles = new List<SourceFile>();
        var warnings = new List<string>();
        var skipped = new List<string>();
        var cleanup = (string?)null;
        string? framework = null;
        var cfgFiles = new List<SourceFile>();
        try
        {
            Analysis analysis;
            if (IsSmxFile(command.SourceDirectory))
                analysis = AnalyzeSmx(command.SourceDirectory, name, command.Id, command.Version, warnings);
            else
            {
                var (root, _, hint) = ResolveSource(command.SourceDirectory, command.ArchivePath, out cleanup);
                analysis = Analyze(root, hint, name, command.Id, command.Version, skipped, warnings, command.Framework);
            }
            cfgFiles = analysis.CfgFiles;
            framework = analysis.Framework;
            var files = analysis.Files;
            allFiles.AddRange(files);

            var metaPlugin = string.IsNullOrWhiteSpace(command.MetaPlugin) ? analysis.MetaPlugin : command.MetaPlugin.Trim();
            var generateMetaPlugins = metaPlugin is not null
                && !files.Any(f => f.RelativePath.Equals("csgo/addons/metamod/metaplugins.ini", StringComparison.Ordinal));
            if (metaPlugin is not null && !generateMetaPlugins)
                warnings.Add("Package already ships addons/metamod/metaplugins.ini; keep an entry for " + metaPlugin + " or Metamod will not load it.");
            if (generateMetaPlugins) ValidateMetaPlugin(metaPlugin!);

            using (var lease = new LocalWorkspaceManager().Acquire(workspace))
            {
                var packagesRoot = Path.Combine(lease.Layout.LibraryRoot, "plugin-packages");
                var destination = SafeChild(packagesRoot, command.Id, command.Version);
                if (Directory.Exists(destination))
                {
                    if (!command.Force) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "This plugin version already exists; pass force/overwrite to replace it.");
                    Directory.Delete(destination, true);
                }
                var filesDir = Path.Combine(destination, "files");
                try
                {
                    Directory.CreateDirectory(filesDir);
                    LocalPaths.RejectLink(filesDir);
                    foreach (var file in files)
                    {
                        var target = Path.Combine(filesDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        if (!IsUnder(filesDir, target)) throw new InstanceException("HCSL-PLUGIN-PATH", "Import path escapes the package root (" + file.RelativePath + ").");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file.Source, target, false);
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file.Source));
                    }
                    foreach (var cfg in cfgFiles)
                    {
                        var relative = cfg.RelativePath["csgo/cfg/".Length..];
                        var safeRelative = PresetCfgRules.Path(relative, requireCfgFile: false);
                        var target = Path.Combine(filesDir, "..", "cfg", safeRelative.Replace('/', Path.DirectorySeparatorChar));
                        target = Path.GetFullPath(target);
                        var cfgRoot = Path.GetFullPath(Path.Combine(filesDir, "..", "cfg"));
                        if (!target.StartsWith(cfgRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            throw new InstanceException("HCSL-PLUGIN-PATH", "Package cfg path escapes its destination (" + relative + ").");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(cfg.Source, target, false);
                    }
                    var gameInfoLines = GameInfoLines(files);
                    if (gameInfoLines.Length > 0)
                    {
                        var baseGameInfo = Path.Combine(lease.Layout.LibraryRoot, "server", "game", "csgo", "gameinfo.gi");
                        if (File.Exists(baseGameInfo))
                        {
                            var overlay = Path.Combine(filesDir, "csgo", "gameinfo.gi");
                            Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
                            WriteGameInfoOverlay(baseGameInfo, overlay, gameInfoLines);
                            generated.Add(new(overlay, "csgo/gameinfo.gi", new FileInfo(overlay).Length));
                            warnings.Add("Generated gameinfo.gi overlay from the base installation (required by this package).");
                        }
                        else warnings.Add("No gameinfo.gi overlay was generated: install CS2 first, or the package cannot load.");
                    }
                    if (generateMetaPlugins)
                    {
                        var meta = Path.Combine(filesDir, "csgo", "addons", "metamod", "metaplugins.ini");
                        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
                        File.WriteAllText(meta, "; Generated by HCSL plugin import.\n" + metaPlugin + "\n", new UTF8Encoding(false));
                        generated.Add(new(meta, "csgo/addons/metamod/metaplugins.ini", new FileInfo(meta).Length));
                        warnings.Add("Generated addons/metamod/metaplugins.ini; place this package after Metamod:Source in the manifest order.");
                    }
                    WriteJson(Path.Combine(destination, "plugin.json"), new PackageManifest(1, command.Id, name, command.Version, "files"));
                }
                catch
                {
                    try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch (IOException) { }
                    throw;
                }
            }
        }
        finally { Cleanup(cleanup); }

        var importedCfg = 0; var keptCfg = 0;
        if (command.CfgIntoPreset)
        {
            if (cfgFiles.Count == 0) warnings.Add("The package contains no csgo/cfg files to import.");
            else if (command.InstanceId == Guid.Empty || command.PresetId == Guid.Empty)
                warnings.Add("Package cfg files were not imported because no instance/preset was supplied.");
            else
            {
                try
                {
                    var sync = new LocalPluginCfgSync();
                    var selection = new[] { new PluginSelection(command.Id, command.Version) };
                    var preview = sync.Preview(workspace, new(PluginCfgAction.Preview, command.InstanceId, command.PresetId,
                        command.InstanceRevision, selection, null));
                    var applied = sync.Apply(workspace, new(PluginCfgAction.Apply, command.InstanceId, command.PresetId,
                        command.InstanceRevision, selection, null, []));
                    importedCfg = applied.Imported;
                    keptCfg = preview.Conflicts.Length + preview.Identical.Length;
                    if (preview.Truncated) warnings.Add("Too many cfg files; only the preview limit was imported.");
                    if (preview.Conflicts.Length > 0)
                        warnings.Add("Kept " + preview.Conflicts.Length + " existing cfg file(s); overwrite them from the plugin page if needed.");
                }
                catch (Exception e) when (e is InstanceException or WorkspaceException or IOException)
                {
                    warnings.Add("Package imported but cfg files were not copied: " + e.Message);
                }
            }
        }

        var enabled = false;
        if (command.Enable)
        {
            if (command.InstanceId == Guid.Empty || command.PresetId == Guid.Empty)
                warnings.Add("Nothing was enabled because no instance/preset was supplied.");
            else
            {
                try
                {
                    var store = new LocalPluginStore();
                    var current = store.Execute(workspace, new(PluginAction.List, command.InstanceId, command.PresetId));
                    var selection = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled))
                        .Where(s => s.Id != command.Id).Append(new(command.Id, command.Version, true)).ToArray();
                    store.Execute(workspace, new(PluginAction.Save, command.InstanceId, command.PresetId,
                        command.InstanceRevision, current.Hash, selection));
                    enabled = true;
                }
                catch (Exception e) when (e is InstanceException or WorkspaceException or IOException)
                {
                    warnings.Add("Package imported but enabling failed: " + e.Message);
                }
            }
        }

        allFiles.AddRange(generated);
        var supported = CurrentPlatform(allFiles);
        if (!supported) warnings.Insert(0, "The package has no binary for this platform (" + (OperatingSystem.IsWindows() ? "Windows" : "Linux") + ").");
        var bytes = allFiles.Sum(f => f.Bytes);
        var paths = allFiles.Select(f => f.RelativePath).ToArray();
        var packageFramework = FrameworkRules.Resolve(paths);
        var frameworkPayload = FrameworkRules.IsFrameworkPayload(packageFramework, paths);
        var package = new PluginPackageInfo(command.Id, name, command.Version, "", allFiles.Count, bytes,
            packageFramework, frameworkPayload, FrameworkRules.CategoryFor(packageFramework, frameworkPayload));
        return new(Path.Combine(workspace.LibraryRoot, "plugin-packages", command.Id, command.Version),
            command.Id, command.Version, allFiles.Count, bytes, supported, [.. warnings], enabled, importedCfg, keptCfg, framework, package);
    }

    public PluginImportPreview Probe(WorkspaceRequest workspace, PluginImportProbeCommand command)
    {
        var skipped = new List<string>();
        var warnings = new List<string>();
        string? cleanup = null;
        try
        {
            if (IsSmxFile(command.SourcePath))
            {
                var smx = AnalyzeSmx(command.SourcePath, null, null, null, warnings);
                return new(command.SourcePath, "file", smx.Id, smx.Name, smx.Version,
                    smx.MetaPlugin, smx.Files.Count, smx.Files.Sum(f => f.Bytes), [.. warnings], Framework: "source2mod");
            }
            var (root, archive, hint) = ResolveSource(command.SourcePath, IsArchive(command.SourcePath) ? command.SourcePath : null, out cleanup);
            var analysis = Analyze(root, hint, null, null, null, skipped, warnings, allowUnknown: true);
            return new(command.SourcePath, archive ? "archive" : "directory", analysis.Id, analysis.Name, analysis.Version,
                analysis.MetaPlugin, analysis.Files.Count, analysis.Files.Sum(f => f.Bytes), [.. warnings],
                analysis.Framework, analysis.TargetRoot, analysis.ManagedFramework);
        }
        finally { Cleanup(cleanup); }
    }

    private sealed record SourceFile(string Source, string RelativePath, long Bytes);
    private sealed record PackageManifest(int FormatVersion, string? Id, string? Name, string? Version, string? FilesRoot);
    private sealed record Analysis(List<SourceFile> Files, string Id, string Name, string Version, string? MetaPlugin, List<SourceFile> CfgFiles, string? Framework = null, string? TargetRoot = null, ManagedPluginFramework ManagedFramework = ManagedPluginFramework.Unknown);

    private static bool IsSmxFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".smx", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>A bare SourcePawn plugin becomes an independent package under source2mod/plugins/.</summary>
    private static Analysis AnalyzeSmx(string path, string? fallbackName, string? forcedId, string? forcedVersion, List<string> warnings)
    {
        LocalPaths.RejectLink(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "The .smx file does not exist.");
        if (info.Length > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "The .smx file is too large.");
        var hint = Path.GetFileNameWithoutExtension(path);
        var id = forcedId ?? Sanitize(hint);
        var name = fallbackName ?? hint;
        var version = forcedVersion ?? DetectVersion(hint) ?? "1.0.0";
        warnings.Add("This is a Source2Mod sub-plugin; the preset must enable source2mod and metamod.");
        return new([new(path, "csgo/addons/source2mod/plugins/" + Path.GetFileName(path), info.Length)], id, name, version, null, []);
    }


    /// <summary>A managed plugin zip/directory has one DLL and one deps.json at the root.</summary>
    private static string? ManagedPluginRoot(string root)
    {
        var current = root;
        for (var depth = 0; depth < 3; depth++)
        {
            if (!Directory.Exists(current)) return null;
            var entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            var hasDll = entries.Any(path => File.Exists(path) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            var hasDeps = entries.Any(path => File.Exists(path) && path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase));
            if (hasDll && hasDeps) return current;
            var directories = entries.Where(Directory.Exists).ToArray();
            if (entries.Length != 1 || directories.Length != 1) return null;
            current = directories[0];
        }
        return null;
    }

    private static ManagedPluginFramework ClassifyManagedPlugin(string root)
    {
        var deps = Directory.EnumerateFiles(root, "*.deps.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => new FileInfo(path).Length).FirstOrDefault();
        return deps is null ? ManagedPluginFramework.Unknown : ClassifyManagedDependencyFile(deps);
    }

    internal static ManagedPluginFramework ClassifyManagedDependencyFile(string deps)
    {
        if (!File.Exists(deps) || new FileInfo(deps).Length > 1024 * 1024) return ManagedPluginFramework.Unknown;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(deps));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddName(string value)
            {
                var slash = value.IndexOf('/');
                names.Add(slash < 0 ? value : value[..slash]);
            }
            if (json.RootElement.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Object)
                foreach (var target in targets.EnumerateObject())
                {
                    AddName(target.Name);
                    if (target.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var package in target.Value.EnumerateObject())
                    {
                        if (package.Value.ValueKind != JsonValueKind.Object) continue;
                        if (package.Value.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                            foreach (var dependency in dependencies.EnumerateObject()) AddName(dependency.Name);
                    }
                }
            if (json.RootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
                foreach (var library in libraries.EnumerateObject()) AddName(library.Name);
            return FrameworkRules.ClassifyManagedDependencies(names);
        }
        catch (JsonException) { return ManagedPluginFramework.Unknown; }
    }

    private static string? ManagedPluginVersion(string root, string folder)
    {
        var deps = Directory.EnumerateFiles(root, "*.deps.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => new FileInfo(path).Length).FirstOrDefault();
        if (deps is null || new FileInfo(deps).Length > 1024 * 1024) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(deps));
            if (!json.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object) return null;
            foreach (var target in targets.EnumerateObject())
            {
                if (target.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var package in target.Value.EnumerateObject())
                {
                    var parts = package.Name.Split('/');
                    if (parts.Length < 2 || !parts[0].Equals(folder, StringComparison.OrdinalIgnoreCase)) continue;
                    try { return PluginRules.Version(parts[1]); } catch (InstanceException) { return null; }
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static Analysis AnalyzeManagedPlugin(string root, string hint, string? fallbackName, string? forcedId, string? forcedVersion,
        string? requestedFramework, bool allowUnknown, List<string> warnings)
    {
        var detected = ClassifyManagedPlugin(root);
        var requested = requestedFramework?.Trim().ToLowerInvariant();
        ManagedPluginFramework selected;
        if (requested is "swiftlys2")
        {
            if (detected is not (ManagedPluginFramework.Unknown or ManagedPluginFramework.Ambiguous or ManagedPluginFramework.SwiftlyS2))
                throw new InstanceException("HCSL-PLUGIN-MANAGED-FRAMEWORK-MISMATCH", "deps.json identifies this as " + detected + ", not SwiftlyS2.");
            selected = ManagedPluginFramework.SwiftlyS2;
        }
        else if (requested is "modsharp")
        {
            if (detected is not (ManagedPluginFramework.Unknown or ManagedPluginFramework.Ambiguous or ManagedPluginFramework.ModSharp))
                throw new InstanceException("HCSL-PLUGIN-MANAGED-FRAMEWORK-MISMATCH", "deps.json identifies this as " + detected + ", not ModSharp.");
            selected = ManagedPluginFramework.ModSharp;
        }
        else
        {
            if (detected == ManagedPluginFramework.Ambiguous && !allowUnknown)
                throw new InstanceException("HCSL-PLUGIN-MANAGED-FRAMEWORK-AMBIGUOUS", "deps.json contains both SwiftlyS2 and ModSharp dependencies.");
            if (detected == ManagedPluginFramework.Unknown && !allowUnknown)
                throw new InstanceException("HCSL-PLUGIN-MANAGED-FRAMEWORK-UNKNOWN", "Cannot determine SwiftlyS2 or ModSharp from deps.json; choose a framework explicitly.");
            selected = detected;
        }
        var framework = selected.FrameworkId();
        var targetRoot = FrameworkRules.PluginRootFor(framework);
        if (selected == ManagedPluginFramework.Ambiguous)
            warnings.Add("deps.json contains both SwiftlyS2 and ModSharp dependencies; choose the target framework before importing.");
        else if (selected == ManagedPluginFramework.Unknown)
            warnings.Add("This managed plugin has no recognized framework dependency; choose SwiftlyS2 or ModSharp before importing.");
        if (framework is null || targetRoot is null)
            targetRoot = "unknown";
        var mainDll = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => new FileInfo(path).Length).First();
        var folder = Path.GetFileNameWithoutExtension(mainDll);
        var id = forcedId ?? Sanitize(folder);
        var name = fallbackName ?? folder;
        var version = forcedVersion ?? DetectVersion(hint) ?? ManagedPluginVersion(root, folder) ?? "1.0.0";
        var files = new List<SourceFile>();
        void Walk(string directory)
        {
            LocalPaths.RejectLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                LocalPaths.RejectLink(entry);
                if (Directory.Exists(entry)) { Walk(entry); continue; }
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var target = targetRoot + "/" + folder + "/" + relative;
                var info = new FileInfo(entry);
                if (info.Length > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Managed plugin file is too large.");
                files.Add(new(entry, target, info.Length));
            }
        }
        Walk(root);
        if (files.Count == 0) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "No files were found in the managed plugin.");
        if (files.Count > PluginRules.MaxPackageFiles) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Managed plugin exceeds the 4096-file limit.");
        if (files.Sum(f => f.Bytes) > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Managed plugin exceeds the 512 MiB limit.");
        if (framework is not null)
        {
            warnings.Add("This is a " + (framework == "swiftlys2" ? "SwiftlyS2" : "ModSharp") + " sub-plugin; the preset must enable " + framework + ".");
            warnings.Add("The plugin will be deployed to " + FrameworkRules.PluginDataRootFor(framework) + "/" + folder + ".");
        }
        return new(files, id, name, version, null, [], framework, targetRoot, selected);
    }

    private static Analysis Analyze(string root, string hint, string? fallbackName, string? forcedId, string? forcedVersion,
        List<string> skipped, List<string> warnings, string? framework = null, bool allowUnknown = false)
    {
        if (ManagedPluginRoot(root) is { } managedRoot)
            return AnalyzeManagedPlugin(managedRoot, hint, fallbackName, forcedId, forcedVersion, framework, allowUnknown, warnings);
        var (filesRoot, prefix) = Layout(root);
        var cfgFiles = new List<SourceFile>();
        var files = Enumerate(filesRoot, prefix, skipped, cfgFiles);
        if (files.Count == 0) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "No plugin files were found in the source.");
        if (files.Count > PluginRules.MaxPackageFiles) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Package exceeds the 4096-file limit.");
        if (files.Sum(f => f.Bytes) > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Package exceeds the 512 MiB limit.");
        if (skipped.Count > 0)
            warnings.Add("Skipped " + skipped.Count + " file(s) under mapped directories (csgo/cfg, csgo/logs, csgo/recordings): "
                + string.Join(", ", skipped.Take(3)) + (skipped.Count > 3 ? ", …" : "") + "; copy them into the preset manually if needed.");

        var manifest = ReadPackagedManifest(root);
        var id = forcedId ?? manifest?.Id ?? DetectId(files, hint);
        var name = fallbackName ?? manifest?.Name ?? DetectName(id, files, hint);
        var version = forcedVersion ?? manifest?.Version ?? DetectVersion(hint) ?? DetectVersionFromBinaries(files) ?? (allowUnknown ? "" : "1.0.0");
        var metaPlugin = DetectMetaPlugin(files);
        if (FrameworkRules.Find(id) is not null && !FrameworkRules.IsFrameworkPayload(id, files.Select(f => f.RelativePath)))
            warnings.Add("This package is a sub-plugin, not the " + id + " framework loader; importing it as '" + id + "' would replace the framework selection.");
        WarnWrongSourceModBuild(files, warnings);
        WarnMissingMetaBinary(files, metaPlugin, warnings);
        var detected = FrameworkRules.Known.Where(rule => rule.PluginRoot is { } pluginRoot &&
            files.Any(file => file.RelativePath.StartsWith(pluginRoot + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
        var requiredFramework = detected.Length == 1 && !FrameworkRules.IsFrameworkPayload(detected[0].Id,
            files.Select(file => file.RelativePath)) ? detected[0].Id : null;
        if (requiredFramework is null && FrameworkRules.Find(id) is null && metaPlugin is not null)
            requiredFramework = "metamod";
        return new(files, id, name, version, metaPlugin, cfgFiles, requiredFramework);
    }

    private static (string Root, string Prefix) Layout(string source)
    {
        if (Directory.Exists(Path.Combine(source, "files", "csgo"))) return (Path.Combine(source, "files"), "");
        if (Directory.Exists(Path.Combine(source, "csgo"))) return (source, "");
        if (Directory.Exists(Path.Combine(source, "addons"))) return (source, "csgo/");
        if (Directory.Exists(Path.Combine(source, "core")) || Directory.Exists(Path.Combine(source, "csgo_lv"))) return (source, "");
        if (Directory.Exists(Path.Combine(source, "sharp"))) return (source, "");
        // Some releases wrap the real root in one directory (SwiftlyS2).
        var entries = Directory.EnumerateFileSystemEntries(source).ToArray();
        if (entries.Length == 1 && Directory.Exists(entries[0])) return Layout(entries[0]);
        throw new InstanceException("HCSL-PLUGIN-PACKAGE",
            "Source must contain addons/, csgo/, core/, sharp/ or files/csgo/ (the release root, not a single addon folder).");
    }

    private static List<SourceFile> Enumerate(string root, string prefix, List<string> skipped, List<SourceFile> cfgFiles)
    {
        var files = new List<SourceFile>();
        void Walk(string directory)
        {
            LocalPaths.RejectLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                LocalPaths.RejectLink(entry);
                if (Directory.Exists(entry)) { Walk(entry); continue; }
                Add(entry);
            }
        }
        void Add(string path)
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (prefix.Length > 0 && !relative.StartsWith("csgo/", StringComparison.Ordinal)) relative = prefix + relative;
            if (PluginRules.IsAddonsRoot(relative))
                throw new InstanceException("HCSL-PLUGIN-PATH", "A package file may not replace the addons directory (" + relative + ").");
            var info = new FileInfo(path);
            if (PluginRules.IsReservedDeploymentPath(relative))
            {
                if (PluginRules.IsPathUnder(relative, "csgo/cfg")) cfgFiles.Add(new(path, relative, info.Length));
                skipped.Add(relative);
                return;
            }
            try { PluginRules.RelativePath(relative); }
            catch (InstanceException e) { throw new InstanceException("HCSL-PLUGIN-PATH", e.Message + " (" + relative + ")"); }
            files.Add(new(path, relative, info.Length));
        }
        Walk(root);
        return files;
    }

    private static string? DetectMetaPlugin(List<SourceFile> files) =>
        files.Any(f => f.RelativePath.StartsWith("csgo/addons/sourcemod/bin/sourcemod_mm", StringComparison.OrdinalIgnoreCase))
            ? "addons/sourcemod/bin/sourcemod_mm" : null;

    private static string DetectId(List<SourceFile> files, string hint)
    {
        var paths = files.Select(f => f.RelativePath).ToArray();
        foreach (var id in new[] { "source2mod", "sourcemod", "metamod", "swiftlys2" })
            if (FrameworkRules.IsFrameworkPayload(id, paths)) return id;
        if (paths.Any(p => p.StartsWith("csgo/addons/cs2kz/", StringComparison.OrdinalIgnoreCase)) ||
            hint.StartsWith("cs2kz-", StringComparison.OrdinalIgnoreCase)) return "cs2kz";
        if (paths.Any(p => p.StartsWith("sharp/", StringComparison.OrdinalIgnoreCase))) return "modsharp";
        if (paths.Any(p => p.StartsWith("csgo/addons/counterstrikesharp/", StringComparison.OrdinalIgnoreCase)))
        {
            if (FrameworkRules.IsFrameworkPayload("counterstrikesharp", paths)) return "counterstrikesharp";
            var folder = paths.Select(p => p.StartsWith("csgo/addons/counterstrikesharp/plugins/", StringComparison.OrdinalIgnoreCase)
                    ? p["csgo/addons/counterstrikesharp/plugins/".Length..].Split('/')[0] : null)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (folder is not null) return Sanitize(folder);
        }
        if (paths.Any(p => p.StartsWith("csgo/addons/source2mod/", StringComparison.OrdinalIgnoreCase)))
        {
            if (FrameworkRules.IsFrameworkPayload("source2mod", paths)) return "source2mod";
            var smx = paths.FirstOrDefault(p => p.StartsWith("csgo/addons/source2mod/plugins/", StringComparison.OrdinalIgnoreCase)
                && p.EndsWith(".smx", StringComparison.OrdinalIgnoreCase));
            if (smx is not null) return Sanitize(Path.GetFileNameWithoutExtension(smx));
        }
        var addons = paths
            .Where(p => p.StartsWith("csgo/addons/", StringComparison.OrdinalIgnoreCase))
            .Select(p => p["csgo/addons/".Length..].Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidate = addons.Length == 1 && FrameworkRules.Find(addons[0]) is null && addons[0] != "sourcemod" ? addons[0] : hint;
        return Sanitize(candidate);
    }

    private static string DetectName(string id, List<SourceFile> files, string hint)
    {
        if (id == "cs2kz") return "CS2KZ";
        if (id == "swiftlys2") return "SwiftlyS2";
        if (id is "source2mod" or "sourcemod" or "metamod")
            return id switch { "source2mod" => "Source2Mod", "sourcemod" => "SourceMod", _ => "Metamod:Source" };
        var plugin = SubPluginName(files, id);
        if (plugin is not null) return plugin;
        return hint is { Length: > 0 } ? hint : "Plugin";
    }

    /// <summary>Original display name of a sub-plugin folder/file (MatchZy, sp_sample).</summary>
    private static string? SubPluginName(List<SourceFile> files, string id)
    {
        foreach (var file in files)
        {
            var path = file.RelativePath;
            var marker = "csgo/addons/counterstrikesharp/plugins/";
            if (path.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var rest = path[marker.Length..];
                var name = rest.Split('/')[0];
                if (Sanitize(name) == id) return name;
            }
            var s2m = "csgo/addons/source2mod/plugins/";
            if (path.StartsWith(s2m, StringComparison.OrdinalIgnoreCase) && path.EndsWith(".smx", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (Sanitize(name) == id) return name;
            }
        }
        return null;
    }

    private static string? DetectVersion(string hint)
    {
        var match = VersionPattern.Match(hint);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Archives named like "sm.tar.gz" carry no version hint. Read the main plugin binary's
    /// embedded ASCII strings and prefer a value next to "version" (e.g. "Source2Mod v1.0.46").
    /// </summary>
    private static string? DetectVersionFromBinaries(List<SourceFile> files)
    {
        var candidates = files
            .Where(f => (f.RelativePath.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || f.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                && (f.RelativePath.Contains("source2mod", StringComparison.OrdinalIgnoreCase)
                    || f.RelativePath.Contains("sourcemod", StringComparison.OrdinalIgnoreCase)
                    || f.RelativePath.Contains("metamod", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Bytes).Take(4);
        foreach (var file in candidates)
        {
            try
            {
                using var stream = File.OpenRead(file.Source);
                var length = (int)Math.Min(stream.Length, 32L * 1024 * 1024);
                var buffer = new byte[length];
                stream.ReadExactly(buffer);
                var text = Encoding.ASCII.GetString(buffer);
                if (ScanEmbeddedVersion(text) is { } version) return version;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>
    /// A binary embeds its own version and its toolchain version (e.g. "Debian clang version 22.1.8").
    /// Prefer brand lines and pre-release/build metadata; never trust compiler/lib versions.
    /// </summary>
    private static string? ScanEmbeddedVersion(string text)
    {
        // Strip ANSI colour codes so "Source2Mod version" and "v1.0.46 @ 0613502" stay near each other.
        text = Regex.Replace(text, "\u001B\\[[0-9;]*[A-Za-z]", "");
        string? best = null;
        var bestScore = 0;
        foreach (Match match in EmbeddedToken.Matches(text))
        {
            var value = match.Groups[1].Value;
            // Compiler/lib markers belong to the version's own token; unrelated neighbours must not hide it.
            var tokenStart = match.Index;
            while (tokenStart > 0 && IsPrintable(text[tokenStart - 1]) && match.Index - tokenStart < 400) tokenStart--;
            var tokenEnd = match.Index + match.Length;
            while (tokenEnd < text.Length && IsPrintable(text[tokenEnd]) && tokenEnd - tokenStart < 400) tokenEnd++;
            var token = text[tokenStart..tokenEnd];
            if (CompilerToken.IsMatch(token)) continue;
            var from = Math.Max(0, match.Index - 120);
            var window = text[from..Math.Min(text.Length, match.Index + value.Length + 120)];
            var score = 0;
            if (window.Contains("metamod", StringComparison.OrdinalIgnoreCase)
                || window.Contains("source2mod", StringComparison.OrdinalIgnoreCase)
                || window.Contains("sourcemod", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (window.Contains("version", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (value.Contains('-') || value.Contains('+')) score += 4;
            if (token.Trim() == value) score += 3;
            if (match.Length < 40) score += 1;
            if (ProseToken.IsMatch(token)) score -= 6;
            if (score > bestScore) { bestScore = score; best = value; }
        }
        return bestScore >= 6 ? best : null;
    }

    private static bool IsPrintable(char value) => value is >= ' ' and <= '~';

    private static string Sanitize(string value)
    {
        var text = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('-', '.');
        return text.Length == 0 ? "plugin" : text[..Math.Min(text.Length, 64)];
    }

    private static PackageManifest? ReadPackagedManifest(string root)
    {
        var path = Path.Combine(root, "plugin.json");
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), Json);
            if (manifest is null || manifest.FormatVersion != 1) return null;
            var id = manifest.Id is null ? null : Sanitize(manifest.Id);
            var version = manifest.Version is null ? null : VersionPattern.Match(manifest.Version) is { Success: true } m ? m.Groups[1].Value : null;
            return manifest with { Id = id, Name = string.IsNullOrWhiteSpace(manifest.Name) ? null : manifest.Name, Version = version };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Only the Metamod loader itself needs the gameinfo overlay; a plugin that merely ships a .vdf does not.</summary>
    private static string[] GameInfoLines(List<SourceFile> files)
    {
        if (files.Any(f => f.RelativePath.Equals("csgo/gameinfo.gi", StringComparison.OrdinalIgnoreCase))) return [];
        var lines = new List<string>();
        if (files.Any(f => f.RelativePath.StartsWith("csgo/addons/metamod/bin/", StringComparison.OrdinalIgnoreCase)))
            lines.Add("csgo/addons/metamod");
        if (files.Any(f => f.RelativePath.StartsWith("csgo/addons/swiftlys2/bin/", StringComparison.OrdinalIgnoreCase)))
            lines.Add("csgo/addons/swiftlys2");
        if (files.Any(f => f.RelativePath.StartsWith("sharp/bin/", StringComparison.OrdinalIgnoreCase)))
            lines.Add("sharp");
        return [.. lines];
    }

    private static void WriteGameInfoOverlay(string baseGameInfo, string overlay, string[] additions)
    {
        var lines = File.ReadAllLines(baseGameInfo).ToList();
        foreach (var addition in additions)
        {
            if (lines.Any(line => line.Contains(addition, StringComparison.OrdinalIgnoreCase))) continue;
            var at = lines.FindIndex(line => line.TrimStart().StartsWith("Game", StringComparison.Ordinal)
                && line.TrimEnd().EndsWith("csgo", StringComparison.Ordinal));
            if (at >= 0) lines.Insert(at, lines[at][..(lines[at].Length - lines[at].TrimStart().Length)] + "Game\t" + addition);
        }
        File.WriteAllLines(overlay, lines);
    }

    private static void ValidateMetaPlugin(string metaPlugin)
    {
        if (metaPlugin.Contains('\\') || metaPlugin.StartsWith('/') || metaPlugin.Split('/').Any(p => p is "" or "." or ".."))
            throw new InstanceException("HCSL-PLUGIN-PATH", "MetaPlugin must be a game-relative addons path.");
        if (!metaPlugin.StartsWith("addons/", StringComparison.Ordinal))
            throw new InstanceException("HCSL-PLUGIN-PATH", "MetaPlugin must start with addons/.");
    }

    private static void WarnWrongSourceModBuild(List<SourceFile> files, List<string> warnings)
    {
        if (!files.Any(f => f.RelativePath.StartsWith("csgo/addons/sourcemod/", StringComparison.OrdinalIgnoreCase))) return;
        var hasCs2 = files.Any(f => f.RelativePath.Contains("sourcemod.2.cs2.so", StringComparison.OrdinalIgnoreCase));
        if (!hasCs2)
            warnings.Add("No sourcemod.2.cs2.so found: this looks like a CS:GO/Source 1 SourceMod build and will not load in CS2.");
        var engine = files.FirstOrDefault(f => f.RelativePath.Equals("csgo/addons/sourcemod/bin/sourcemod.logic.so", StringComparison.OrdinalIgnoreCase));
        if (engine is not null && Is32BitElf(engine.Source))
            warnings.Add("sourcemod.logic.so is a 32-bit binary; the CS2 server is 64-bit and will report <ERROR>.");
    }

    private static void WarnMissingMetaBinary(List<SourceFile> files, string? metaPlugin, List<string> warnings)
    {
        if (metaPlugin is null) return;
        var binary = "csgo/" + metaPlugin;
        var found = files.Any(f => f.RelativePath.Equals(binary, StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.StartsWith(binary + ".", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.StartsWith(binary + "_", StringComparison.OrdinalIgnoreCase));
        if (!found) warnings.Add("metaplugins.ini points at " + metaPlugin + " but no matching binary was found in the package.");
    }

    private static bool Is32BitElf(string path)
    {
        try
        {
            var header = new byte[5];
            using var stream = File.OpenRead(path);
            return stream.Read(header, 0, 5) == 5 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 1;
        }
        catch (IOException) { return false; }
    }

    private static bool CurrentPlatform(List<SourceFile> files)
    {
        static bool ManagedPlugin(SourceFile f) => f.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && (f.RelativePath.StartsWith("csgo/addons/swiftlys2/plugins/", StringComparison.OrdinalIgnoreCase)
                || f.RelativePath.StartsWith("sharp/modules/", StringComparison.OrdinalIgnoreCase));
        if (files.Any(ManagedPlugin)) return true;
        static bool Linux(SourceFile f) => f.RelativePath.EndsWith(".smx", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.EndsWith("/bin/sourcemod_mm", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/linuxsteamrt64/", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/linux64/", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/bin/linux", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.EndsWith(".so", StringComparison.OrdinalIgnoreCase);
        static bool Windows(SourceFile f) => f.RelativePath.EndsWith(".smx", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/win64/", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/bin/windows", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        return OperatingSystem.IsWindows() ? files.Any(Windows) : files.Any(Linux);
    }

    private static string SafeChild(string root, params string[] parts)
    {
        var path = root;
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.Contains('/') || part.Contains('\\'))
                throw new InstanceException("HCSL-PLUGIN-PATH", "Invalid package id or version.");
            path = Path.Combine(path, part);
        }
        if (!IsUnder(root, path)) throw new InstanceException("HCSL-PLUGIN-PATH", "Package path escapes the library.");
        return path;
    }

    private static bool IsUnder(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(full, comparison) || candidate.StartsWith(full + Path.DirectorySeparatorChar, comparison);
    }

    private static void WriteJson(string path, PackageManifest manifest)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, manifest, Json);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsArchive(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    private static (string Root, bool Archive, string Hint) ResolveSource(string? directory, string? archivePath, out string? cleanup)
    {
        cleanup = null;
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            if (!File.Exists(archivePath)) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "The archive does not exist.");
            LocalPaths.RejectLink(archivePath);
            var temp = Path.Combine(Path.GetTempPath(), "hcsl-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            cleanup = temp;
            Extract(archivePath, temp);
            var hint = Path.GetFileName(archivePath);
            foreach (var suffix in new[] { ".tar.gz", ".tgz", ".tar", ".zip" })
                if (hint.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { hint = hint[..^suffix.Length]; break; }
            return (temp, true, hint);
        }
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InstanceException("HCSL-PLUGIN-PACKAGE", "The source directory does not exist.");
        LocalPaths.RejectLink(directory);
        var root = Path.GetFullPath(directory);
        return (root, false, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private static void Cleanup(string? directory)
    {
        if (directory is null) return;
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
    }

    /// <summary>Extract with traversal, link and size limits; archives are untrusted input.</summary>
    private static void Extract(string archivePath, string target)
    {
        long bytes = 0; var count = 0;
        var maxFiles = PluginRules.MaxPackageFiles * 4;
        var maxBytes = PluginRules.MaxPackageBytes * 2;
        void Add(string name, Stream? data, long length)
        {
            if (++count > maxFiles) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Archive expands to too many files.");
            bytes += length;
            if (bytes > maxBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Archive expands beyond the size limit.");
            if (data is null) return;
            var destination = SafeEntry(target, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = LocalPaths.CreateFile(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            data.CopyTo(output);
        }

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.Length == 0) continue; // directory entry
                var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixMode == 0xA000) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Archive contains a symbolic link (" + entry.FullName + ").");
                using var stream = entry.Open();
                Add(entry.FullName, stream, entry.Length);
            }
            return;
        }
        using var file = File.OpenRead(archivePath);
        using Stream input = archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
            ? file : new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(input);
        TarEntry? tarEntry;
        while ((tarEntry = reader.GetNextEntry()) is not null)
        {
            if (tarEntry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink or TarEntryType.BlockDevice
                or TarEntryType.CharacterDevice or TarEntryType.Fifo)
                throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Archive contains a link or device entry (" + tarEntry.Name + ").");
            if (tarEntry.DataStream is null) continue;
            Add(tarEntry.Name, tarEntry.DataStream, tarEntry.Length);
        }
    }

    private static string SafeEntry(string root, string name)
    {
        var normalized = name.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains(':') || normalized.Split('/').Any(part => part is ".." or ""))
            throw new InstanceException("HCSL-PLUGIN-PATH", "Archive entry escapes the extraction root (" + name + ").");
        var destination = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!IsUnder(root, destination)) throw new InstanceException("HCSL-PLUGIN-PATH", "Archive entry escapes the extraction root (" + name + ").");
        return destination;
    }
}
