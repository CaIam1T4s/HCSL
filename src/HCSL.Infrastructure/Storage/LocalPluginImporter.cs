using System.Text;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Copies an already-extracted plugin release (Metamod:Source, SourceMod or a plain addons tree)
/// into &lt;library&gt;/plugin-packages/&lt;id&gt;/&lt;version&gt;. Platform-neutral: the same layout is
/// accepted on Linux and Windows; missing binaries for the current OS are warnings, not errors.
/// </summary>
public sealed class LocalPluginImporter : IPluginImporter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };

    public PluginImportResult Execute(WorkspaceRequest workspace, PluginImportCommand command)
    {
        PluginRules.Id(command.Id); PluginRules.Version(command.Version);
        var name = (command.Name ?? "").Trim();
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
            throw new InstanceException("HCSL-PLUGIN-PACKAGE", "Plugin name must be 1-100 visible characters.");
        var source = Path.GetFullPath(command.SourceDirectory ?? "");
        if (!Directory.Exists(source)) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "The source directory does not exist.");
        LocalPaths.RejectLink(source);

        var warnings = new List<string>();
        var (filesRoot, prefix) = Layout(source);
        var skipped = new List<string>();
        var files = Enumerate(source, filesRoot, prefix, warnings, skipped);
        if (skipped.Count > 0)
            warnings.Add("Skipped " + skipped.Count + " file(s) under mapped directories (csgo/cfg, csgo/logs, csgo/recordings): "
                + string.Join(", ", skipped.Take(3)) + (skipped.Count > 3 ? ", …" : "") + "; copy them into the preset manually if needed.");
        if (files.Count == 0) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "No plugin files were found in the source directory.");
        if (files.Count > PluginRules.MaxPackageFiles) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Package exceeds the 4096-file limit.");
        var total = files.Sum(f => f.Bytes);
        if (total > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Package exceeds the 512 MiB limit.");

        var generateGameInfo = files.Any(f => f.RelativePath.StartsWith("csgo/addons/metamod/", StringComparison.OrdinalIgnoreCase))
            && !files.Any(f => f.RelativePath.Equals("csgo/gameinfo.gi", StringComparison.OrdinalIgnoreCase));
        var metaPlugin = string.IsNullOrWhiteSpace(command.MetaPlugin) ? DetectMetaPlugin(files) : command.MetaPlugin.Trim();
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
            }
            catch
            {
                try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch (IOException) { }
                throw;
            }
            if (generateGameInfo)
            {
                var baseGameInfo = Path.Combine(lease.Layout.LibraryRoot, "server", "game", "csgo", "gameinfo.gi");
                if (File.Exists(baseGameInfo))
                {
                    var overlay = Path.Combine(filesDir, "csgo", "gameinfo.gi");
                    Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
                    var lines = File.ReadAllLines(baseGameInfo).ToList();
                    if (!lines.Any(line => line.Contains("csgo/addons/metamod", StringComparison.OrdinalIgnoreCase)))
                    {
                        var at = lines.FindIndex(line => line.TrimStart().StartsWith("Game", StringComparison.Ordinal)
                            && line.TrimEnd().EndsWith("csgo", StringComparison.Ordinal));
                        if (at >= 0) lines.Insert(at, lines[at][..(lines[at].Length - lines[at].TrimStart().Length)] + "Game\tcsgo/addons/metamod");
                    }
                    File.WriteAllLines(overlay, lines);
                    files.Add(new(overlay, "csgo/gameinfo.gi", new FileInfo(overlay).Length));
                    warnings.Add("Generated gameinfo.gi overlay from the base installation (required for Metamod).");
                }
                else warnings.Add("No gameinfo.gi overlay was generated: install CS2 first, or the package cannot load Metamod.");
            }
            if (generateMetaPlugins)
            {
                var meta = Path.Combine(filesDir, "csgo", "addons", "metamod", "metaplugins.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
                File.WriteAllText(meta, "; Generated by HCSL plugin import.\n" + metaPlugin + "\n", new UTF8Encoding(false));
                files.Add(new(meta, "csgo/addons/metamod/metaplugins.ini", new FileInfo(meta).Length));
                warnings.Add("Generated addons/metamod/metaplugins.ini; place this package after Metamod:Source in the manifest order.");
            }
            WriteJson(Path.Combine(destination, "plugin.json"), new PackageManifest(1, command.Id, name, command.Version, "files"));
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

        var supported = CurrentPlatform(files);
        if (!supported) warnings.Insert(0, "The package has no binary for this platform (" + (OperatingSystem.IsWindows() ? "Windows" : "Linux") + ").");
        return new(Path.Combine(workspace.LibraryRoot, "plugin-packages", command.Id, command.Version),
            command.Id, command.Version, files.Count, files.Sum(f => f.Bytes), supported, [.. warnings], enabled);
    }

    private sealed record SourceFile(string Source, string RelativePath, long Bytes);
    private sealed record PackageManifest(int FormatVersion, string Id, string Name, string Version, string FilesRoot);

    /// <summary>Return the directory that maps to the game root and the prefix its files need.</summary>
    private static (string Root, string Prefix) Layout(string source)
    {
        if (Directory.Exists(Path.Combine(source, "files", "csgo"))) return (Path.Combine(source, "files"), "");
        if (Directory.Exists(Path.Combine(source, "csgo"))) return (source, "");
        if (Directory.Exists(Path.Combine(source, "addons"))) return (source, "csgo/");
        if (Directory.Exists(Path.Combine(source, "core")) || Directory.Exists(Path.Combine(source, "csgo_lv"))) return (source, "");
        throw new InstanceException("HCSL-PLUGIN-PACKAGE",
            "Source must contain addons/, csgo/, core/ or files/csgo/ (the release root, not a single addon folder).");
    }

    private static List<SourceFile> Enumerate(string source, string root, string prefix, List<string> warnings, List<string> skipped)
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
            // Mapped persistent directories (cfg/logs/recordings) belong to the preset, not to a package.
            // Import skips them with a warning instead of failing the whole release.
            if (PluginRules.IsReservedDeploymentPath(relative)) { skipped.Add(relative); return; }
            try { PluginRules.RelativePath(relative); }
            catch (InstanceException e) { throw new InstanceException("HCSL-PLUGIN-PATH", e.Message + " (" + relative + ")"); }
            var info = new FileInfo(path);
            files.Add(new(path, relative, info.Length));
        }
        Walk(root);
        return files;
    }

    private static string? DetectMetaPlugin(List<SourceFile> files)
    {
        var binary = files.FirstOrDefault(f => f.RelativePath.StartsWith("csgo/addons/sourcemod/bin/sourcemod_mm", StringComparison.OrdinalIgnoreCase));
        return binary is null ? null : "addons/sourcemod/bin/sourcemod_mm";
    }

    private static void ValidateMetaPlugin(string metaPlugin)
    {
        if (metaPlugin.Contains('\\') || metaPlugin.StartsWith('/') || metaPlugin.Split('/').Any(p => p is "" or "." or ".."))
            throw new InstanceException("HCSL-PLUGIN-PATH", "MetaPlugin must be a game-relative addons path.");
        if (!metaPlugin.StartsWith("addons/", StringComparison.Ordinal))
            throw new InstanceException("HCSL-PLUGIN-PATH", "MetaPlugin must start with addons/.");
    }

    private static bool CurrentPlatform(List<SourceFile> files)
    {
        static bool Linux(SourceFile f) => f.RelativePath.EndsWith("/bin/sourcemod_mm", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/linuxsteamrt64/", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/linux64/", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.Contains("/bin/linux", StringComparison.OrdinalIgnoreCase)
            || f.RelativePath.EndsWith(".so", StringComparison.OrdinalIgnoreCase);
        static bool Windows(SourceFile f) => f.RelativePath.Contains("/win64/", StringComparison.OrdinalIgnoreCase)
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
}
