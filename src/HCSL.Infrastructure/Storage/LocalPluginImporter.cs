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
    private static readonly Regex VersionPattern = new(@"(?<!\d)(\d+\.\d+(?:\.\d+)?(?:-git\d+)?(?:-(?:dev|beta|stable))?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        try
        {
            var (root, archive, hint) = ResolveSource(command.SourceDirectory, command.ArchivePath, out cleanup);
            var analysis = Analyze(root, hint, name, command.Id, command.Version, skipped, warnings);
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
                    if (NeedsGameInfo(files))
                    {
                        var baseGameInfo = Path.Combine(lease.Layout.LibraryRoot, "server", "game", "csgo", "gameinfo.gi");
                        if (File.Exists(baseGameInfo))
                        {
                            var overlay = Path.Combine(filesDir, "csgo", "gameinfo.gi");
                            Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
                            WriteGameInfoOverlay(baseGameInfo, overlay);
                            generated.Add(new(overlay, "csgo/gameinfo.gi", new FileInfo(overlay).Length));
                            warnings.Add("Generated gameinfo.gi overlay from the base installation (required for Metamod).");
                        }
                        else warnings.Add("No gameinfo.gi overlay was generated: install CS2 first, or the package cannot load Metamod.");
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
        return new(Path.Combine(workspace.LibraryRoot, "plugin-packages", command.Id, command.Version),
            command.Id, command.Version, allFiles.Count, allFiles.Sum(f => f.Bytes), supported, [.. warnings], enabled);
    }

    public PluginImportPreview Probe(WorkspaceRequest workspace, PluginImportProbeCommand command)
    {
        var skipped = new List<string>();
        var warnings = new List<string>();
        string? cleanup = null;
        try
        {
            var (root, archive, hint) = ResolveSource(command.SourcePath, IsArchive(command.SourcePath) ? command.SourcePath : null, out cleanup);
            var analysis = Analyze(root, hint, null, null, null, skipped, warnings);
            return new(command.SourcePath, archive ? "archive" : "directory", analysis.Id, analysis.Name, analysis.Version,
                analysis.MetaPlugin, analysis.Files.Count, analysis.Files.Sum(f => f.Bytes), [.. warnings]);
        }
        finally { Cleanup(cleanup); }
    }

    private sealed record SourceFile(string Source, string RelativePath, long Bytes);
    private sealed record PackageManifest(int FormatVersion, string? Id, string? Name, string? Version, string? FilesRoot);
    private sealed record Analysis(List<SourceFile> Files, string Id, string Name, string Version, string? MetaPlugin);

    private static Analysis Analyze(string root, string hint, string? fallbackName, string? forcedId, string? forcedVersion, List<string> skipped, List<string> warnings)
    {
        var (filesRoot, prefix) = Layout(root);
        var files = Enumerate(filesRoot, prefix, skipped);
        if (files.Count == 0) throw new InstanceException("HCSL-PLUGIN-PACKAGE", "No plugin files were found in the source.");
        if (files.Count > PluginRules.MaxPackageFiles) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Package exceeds the 4096-file limit.");
        if (files.Sum(f => f.Bytes) > PluginRules.MaxPackageBytes) throw new InstanceException("HCSL-PLUGIN-LIMIT", "Package exceeds the 512 MiB limit.");
        if (skipped.Count > 0)
            warnings.Add("Skipped " + skipped.Count + " file(s) under mapped directories (csgo/cfg, csgo/logs, csgo/recordings): "
                + string.Join(", ", skipped.Take(3)) + (skipped.Count > 3 ? ", …" : "") + "; copy them into the preset manually if needed.");

        var manifest = ReadPackagedManifest(root);
        var id = forcedId ?? manifest?.Id ?? DetectId(files, hint);
        var name = fallbackName ?? manifest?.Name ?? DetectName(id, hint);
        var version = forcedVersion ?? manifest?.Version ?? DetectVersion(hint);
        var metaPlugin = DetectMetaPlugin(files);
        WarnWrongSourceModBuild(files, warnings);
        WarnMissingMetaBinary(files, metaPlugin, warnings);
        return new(files, id, name, version, metaPlugin);
    }

    private static (string Root, string Prefix) Layout(string source)
    {
        if (Directory.Exists(Path.Combine(source, "files", "csgo"))) return (Path.Combine(source, "files"), "");
        if (Directory.Exists(Path.Combine(source, "csgo"))) return (source, "");
        if (Directory.Exists(Path.Combine(source, "addons"))) return (source, "csgo/");
        if (Directory.Exists(Path.Combine(source, "core")) || Directory.Exists(Path.Combine(source, "csgo_lv"))) return (source, "");
        throw new InstanceException("HCSL-PLUGIN-PACKAGE",
            "Source must contain addons/, csgo/, core/ or files/csgo/ (the release root, not a single addon folder).");
    }

    private static List<SourceFile> Enumerate(string root, string prefix, List<string> skipped)
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
            if (PluginRules.IsReservedDeploymentPath(relative)) { skipped.Add(relative); return; }
            try { PluginRules.RelativePath(relative); }
            catch (InstanceException e) { throw new InstanceException("HCSL-PLUGIN-PATH", e.Message + " (" + relative + ")"); }
            var info = new FileInfo(path);
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
        foreach (var (marker, id) in new[]
        {
            ("csgo/addons/source2mod/", "source2mod"),
            ("csgo/addons/sourcemod/", "sourcemod"),
            ("csgo/addons/metamod/", "metamod")
        })
            if (files.Any(f => f.RelativePath.StartsWith(marker, StringComparison.OrdinalIgnoreCase))) return id;
        var addons = files.Select(f => f.RelativePath)
            .Where(p => p.StartsWith("csgo/addons/", StringComparison.OrdinalIgnoreCase))
            .Select(p => p["csgo/addons/".Length..].Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidate = addons.Length == 1 ? addons[0] : hint;
        return Sanitize(candidate);
    }

    private static string DetectName(string id, string hint) => id switch
    {
        "source2mod" => "Source2Mod",
        "sourcemod" => "SourceMod",
        "metamod" => "Metamod:Source",
        _ => hint is { Length: > 0 } ? hint : "Plugin"
    };

    private static string DetectVersion(string hint)
    {
        var match = VersionPattern.Match(hint);
        return match.Success ? match.Groups[1].Value : "1.0.0";
    }

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

    private static bool NeedsGameInfo(List<SourceFile> files) =>
        files.Any(f => f.RelativePath.StartsWith("csgo/addons/metamod/", StringComparison.OrdinalIgnoreCase))
        && !files.Any(f => f.RelativePath.Equals("csgo/gameinfo.gi", StringComparison.OrdinalIgnoreCase));

    private static void WriteGameInfoOverlay(string baseGameInfo, string overlay)
    {
        var lines = File.ReadAllLines(baseGameInfo).ToList();
        if (!lines.Any(line => line.Contains("csgo/addons/metamod", StringComparison.OrdinalIgnoreCase)))
        {
            var at = lines.FindIndex(line => line.TrimStart().StartsWith("Game", StringComparison.Ordinal)
                && line.TrimEnd().EndsWith("csgo", StringComparison.Ordinal));
            if (at >= 0) lines.Insert(at, lines[at][..(lines[at].Length - lines[at].TrimStart().Length)] + "Game\tcsgo/addons/metamod");
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
