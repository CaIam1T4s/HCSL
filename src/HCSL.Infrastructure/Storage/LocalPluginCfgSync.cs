using System.Security.Cryptography;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Server;
namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Copies package-owned csgo/cfg files into the preset cfg tree with an explicit preview.
/// Provenance lives beside the preset (never inside cfg) so cleanup only removes untouched imports.
/// </summary>
public sealed class LocalPluginCfgSync : IPluginCfgSync
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = false };
    private sealed record Ledger(int FormatVersion, LedgerEntry[] Files);
    private sealed record LedgerEntry(string PluginId, string Version, string Path, string Sha256);

    public PluginCfgPreview Preview(WorkspaceRequest workspace, PluginCfgCommand command)
    {
        var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        return Preview(Layout(paths), command);
    }

    public PluginCfgPreview CleanupPreview(WorkspaceRequest workspace, PluginCfgCommand command)
    {
        var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        return Cleanup(Layout(paths), command);
    }

    public PluginCfgResult Apply(WorkspaceRequest workspace, PluginCfgCommand command)
    {
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
            throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before importing cfg files.");
        return Apply(lease.Layout, command);
    }

    public PluginCfgResult CleanupApply(WorkspaceRequest workspace, PluginCfgCommand command)
    {
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
            throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before deleting cfg files.");
        return CleanupApply(lease.Layout, command);
    }

    private PluginCfgPreview Preview(WorkspaceLayout layout, PluginCfgCommand command)
    {
        var (preset, cfgDir) = PresetCfg(layout, command);
        var import = new List<PluginCfgEntry>();
        var conflicts = new List<PluginCfgEntry>();
        var identical = new List<PluginCfgEntry>();
        var truncated = false;
        foreach (var selection in command.Enable ?? [])
        {
            var package = Package(layout, selection.Id, selection.Version);
            foreach (var (relative, _, sha, bytes) in CfgSources(package))
            {
                var destination = Destination(cfgDir, relative);
                if (import.Count + conflicts.Count + identical.Count >= PluginCfgRules.MaxPreviewEntries) { truncated = true; break; }
                if (!File.Exists(destination))
                    import.Add(new(selection.Id, selection.Version, relative, bytes, sha, null));
                else
                {
                    var existing = Hash(destination);
                    if (string.Equals(existing, sha, StringComparison.OrdinalIgnoreCase))
                        identical.Add(new(selection.Id, selection.Version, relative, bytes, sha, existing));
                    else if (new FileInfo(destination).Length > PluginCfgRules.MaxFileBytes)
                        throw new InstanceException("HCSL-CFG-LIMIT", "A cfg file to overwrite is too large.");
                    else
                        conflicts.Add(new(selection.Id, selection.Version, relative, bytes, sha, existing));
                }
            }
        }
        var (removable, modified) = CleanupEntries(preset, cfgDir, command.Disable);
        return new([.. import], [.. conflicts], [.. identical], removable, modified, truncated);
    }

    private PluginCfgResult Apply(WorkspaceLayout layout, PluginCfgCommand command)
    {
        var (preset, cfgDir) = PresetCfg(layout, command);
        var preview = Preview(layout, command);
        if (preview.Truncated) throw new InstanceException("HCSL-CFG-LIMIT", "Too many cfg files to import at once.");
        var overwrite = new HashSet<string>(command.Overwrite ?? [], StringComparer.Ordinal);
        var packages = new Dictionary<string, LocalPluginStore.Package>(StringComparer.Ordinal);
        LocalPluginStore.Package PackageFor(string id, string version)
        {
            var key = id + "\0" + version;
            if (!packages.TryGetValue(key, out var package))
                packages[key] = package = Package(layout, id, version);
            return package;
        }
        var writes = new List<(PluginCfgEntry Entry, string Source)>();
        foreach (var entry in preview.Import)
            writes.Add((entry, Source(PackageFor(entry.PluginId, entry.Version), entry.Path)));
        foreach (var entry in preview.Conflicts)
        {
            if (!overwrite.Contains(entry.Path)) continue;
            var destination = Destination(cfgDir, entry.Path);
            if (!File.Exists(destination) || !string.Equals(Hash(destination), entry.ExistingHash, StringComparison.OrdinalIgnoreCase))
                throw new InstanceException("HCSL-CFG-CONFLICT", "A cfg file changed after the preview; nothing more was written.");
            writes.Add((entry, Source(PackageFor(entry.PluginId, entry.Version), entry.Path)));
        }
        if (writes.Count == 0) return new(0, 0, 0, ["No cfg files were selected."]);
        long total = 0;
        foreach (var (entry, _) in writes)
        {
            if (entry.Bytes > PluginCfgRules.MaxFileBytes) throw new InstanceException("HCSL-CFG-LIMIT", "A cfg file is too large: " + entry.Path);
            total = checked(total + entry.Bytes);
            if (total > PluginCfgRules.MaxTotalBytes) throw new InstanceException("HCSL-CFG-LIMIT", "Selected cfg files exceed the size limit.");
        }
        var ledger = ReadLedger(preset);
        foreach (var (entry, source) in writes)
        {
            var destination = Destination(cfgDir, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyVerified(source, destination, entry.SourceHash);
            ledger = Upsert(ledger, new(entry.PluginId, entry.Version, entry.Path, entry.SourceHash));
        }
        WriteLedger(preset, ledger);
        return new(preview.Import.Length, preview.Conflicts.Count(c => overwrite.Contains(c.Path)), 0,
            [.. preview.Conflicts.Where(c => !overwrite.Contains(c.Path)).Select(c => "Kept existing: " + c.Path)]);
    }

    private PluginCfgPreview Cleanup(WorkspaceLayout layout, PluginCfgCommand command)
    {
        var (preset, cfgDir) = PresetCfg(layout, command);
        var (removable, modified) = CleanupEntries(preset, cfgDir, command.Disable);
        return new([], [], [], removable, modified);
    }

    private PluginCfgResult CleanupApply(WorkspaceLayout layout, PluginCfgCommand command)
    {
        var (preset, cfgDir) = PresetCfg(layout, command);
        var preview = Cleanup(layout, command);
        var delete = new HashSet<string>(command.Delete ?? [], StringComparer.Ordinal);
        var ledger = ReadLedger(preset);
        var removed = 0;
        foreach (var entry in preview.Removable)
        {
            if (!delete.Contains(entry.Path)) continue;
            var destination = Destination(cfgDir, entry.Path);
            if (!File.Exists(destination) || !string.Equals(Hash(destination), entry.SourceHash, StringComparison.OrdinalIgnoreCase)) continue;
            var trash = Path.Combine(preset, ".cfg-trash");
            LocalPaths.CreatePrivateDirectory(trash);
            File.Move(destination, Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(destination)));
            ledger = Remove(ledger, entry.Path);
            removed++;
            PruneEmpty(Path.GetDirectoryName(destination), cfgDir);
        }
        if (removed > 0) WriteLedger(preset, ledger);
        return new(0, 0, removed, []);
    }

    private (Ledger Ledger, (PluginCfgEntry[] Removable, PluginCfgEntry[] Modified) Entries) Get(WorkspaceLayout layout, PluginCfgCommand command)
    {
        var (preset, cfgDir) = PresetCfg(layout, command);
        return (ReadLedger(preset), CleanupEntries(preset, cfgDir, command.Disable));
    }

    private (PluginCfgEntry[] Removable, PluginCfgEntry[] Modified) CleanupEntries(string preset, string cfgDir, PluginSelection[]? disable)
    {
        var ids = (disable ?? []).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var removable = new List<PluginCfgEntry>();
        var modified = new List<PluginCfgEntry>();
        foreach (var entry in ReadLedger(preset).Files)
        {
            if (!ids.Contains(entry.PluginId)) continue;
            var destination = Destination(cfgDir, entry.Path);
            if (!File.Exists(destination)) continue;
            var hash = Hash(destination);
            var info = new FileInfo(destination);
            if (string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                removable.Add(new(entry.PluginId, entry.Version, entry.Path, info.Length, entry.Sha256, hash));
            else
                modified.Add(new(entry.PluginId, entry.Version, entry.Path, info.Length, entry.Sha256, hash, Modified: true));
        }
        return ([.. removable], [.. modified]);
    }

    private static (string Preset, string Cfg) PresetCfg(WorkspaceLayout layout, PluginCfgCommand command)
    {
        Safe(layout.InstancesRoot);
        var instance = Path.Combine(layout.InstancesRoot, command.InstanceId.ToString("D"));
        Safe(instance);
        var entry = LocalInstanceStore.Load(instance, command.InstanceId);
        if (entry.Definition is null || entry.Presets.All(p => p.Id != command.PresetId))
            throw new InstanceException("HCSL-CFG-PRESET", "The instance or preset no longer exists.");
        if (command.InstanceRevision != 0 && entry.Definition.Revision != command.InstanceRevision)
            throw new InstanceException("HCSL-INSTANCE-STALE", "Instance revision changed. Refresh before syncing cfg files.");
        var preset = Path.Combine(instance, "presets", command.PresetId.ToString("D"));
        Safe(preset);
        var cfg = Path.Combine(preset, "cfg");
        Safe(cfg);
        if (!Directory.Exists(cfg)) throw new InstanceException("HCSL-CFG-DIRECTORY", "Preset CFG directory is missing.");
        return (preset, cfg);
    }

    private static WorkspaceLayout Layout(WorkspaceRequest request) => new(request.SettingsRoot, request.DataRoot, request.LibraryRoot,
        Path.Combine(request.LibraryRoot, "steamcmd"), Path.Combine(request.DataRoot, "instances"),
        Path.Combine(request.LibraryRoot, "server"), Path.Combine(request.LibraryRoot, "runtime"));

    internal static LocalPluginStore.Package Package(WorkspaceLayout layout, string id, string version) =>
        LocalPluginStore.LoadPackage(Path.Combine(layout.LibraryRoot, "plugin-packages"), id, version);

    private static string Source(LocalPluginStore.Package package, string relative)
    {
        var path = Path.Combine(Path.GetDirectoryName(package.Root)!, "cfg", relative.Replace('/', Path.DirectorySeparatorChar));
        Safe(path);
        if (!File.Exists(path)) throw new InstanceException("HCSL-CFG-PATH", "The package cfg file disappeared: " + relative);
        return path;
    }

    /// <summary>Package cfg payloads live beside files/ so deployment never sees them as addons.</summary>
    private static IEnumerable<(string Relative, string Source, string Sha256, long Bytes)> CfgSources(LocalPluginStore.Package package)
    {
        var root = Path.Combine(Path.GetDirectoryName(package.Root)!, "cfg");
        if (!Directory.Exists(root)) yield break;
        Safe(root);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            Safe(path);
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return (PresetCfgRules.Path(relative, requireCfgFile: false), path, Hash(path), info.Length);
        }
    }

    private static string Destination(string cfgDir, string relative)
    {
        var path = cfgDir;
        foreach (var part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            Safe(path);
        }
        if (!path.StartsWith(cfgDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InstanceException("HCSL-CFG-PATH", "CFG path escapes the preset directory.");
        return path;
    }

    private static void CopyVerified(string source, string destination, string expectedHash)
    {
        var temp = destination + ".cfg-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            if (!string.Equals(Hash(temp), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InstanceException("HCSL-CFG-CONFLICT", "The package cfg file changed after validation; nothing was written.");
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Ledger ReadLedger(string preset)
    {
        var path = Path.Combine(preset, ".hcsl-cfg-ledger.json");
        Safe(path);
        if (!File.Exists(path)) return new(1, []);
        try
        {
            var ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json)
                ?? throw new InstanceException("HCSL-CFG-LEDGER", "CFG import ledger is empty.");
            if (ledger.FormatVersion != 1 || ledger.Files is null) throw new InstanceException("HCSL-CFG-LEDGER", "Unknown CFG import ledger format.");
            return ledger;
        }
        catch (JsonException) { throw new InstanceException("HCSL-CFG-LEDGER", "CFG import ledger cannot be read."); }
    }

    private static void WriteLedger(string preset, Ledger ledger)
    {
        var path = Path.Combine(preset, ".hcsl-cfg-ledger.json");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, ledger, Json); stream.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Ledger Upsert(Ledger ledger, LedgerEntry entry) =>
        new(1, [.. ledger.Files.Where(e => !string.Equals(e.Path, entry.Path, StringComparison.Ordinal)), entry]);

    private static Ledger Remove(Ledger ledger, string path) =>
        new(1, [.. ledger.Files.Where(e => !string.Equals(e.Path, path, StringComparison.Ordinal))]);

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void PruneEmpty(string? directory, string root)
    {
        try
        {
            while (directory is not null && directory.Length > root.Length && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }
        catch (IOException) { }
    }

    private static void Safe(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
            LocalPaths.RejectLink(item.FullName);
    }
}
