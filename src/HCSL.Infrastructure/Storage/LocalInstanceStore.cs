using System.Security.Cryptography;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Server;
namespace HCSL.Infrastructure.Storage;

// Every mutation holds the existing cross-process workspace lease, including SteamCMD exclusion.
// Listing is read-only and deliberately lock-free: a live server session pins the lease for its whole
// lifetime, and the daemon must still be able to show the catalog it is serving.
public sealed class LocalInstanceStore : IInstanceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public InstanceCatalog Execute(WorkspaceRequest workspace, InstanceCommand command)
    {
        if (!Enum.IsDefined(command.Action)) throw new InstanceException("HCSL-INSTANCE-REQUEST", "Unknown instance action.");
        if (command.Action == InstanceAction.List)
        {
            var listRoot = ListRoot(workspace);
            return (Directory.Exists(listRoot) ? Catalog(listRoot) : new InstanceCatalog([]))
                with { ConnectionAddresses = LocalServerAddresses.Discover() };
        }
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        var root = lease.Layout.InstancesRoot;
        Safe(root);
        if (command.Action is InstanceAction.Create or InstanceAction.CreatePreset or InstanceAction.CopyPreset &&
            JsonSerializer.SerializeToUtf8Bytes(Catalog(root)).Length > 16000)
            throw new InstanceException("HCSL-INSTANCE-LIMIT", "Catalog capacity reached; paginated management is not implemented yet.");
        if (command.Action != InstanceAction.List) Mutate(root, command);
        return Catalog(root) with { ConnectionAddresses = LocalServerAddresses.Discover() };
    }
    /// <summary>Read-only bind address of one instance, without taking the lease a live run holds.
    /// Used by the daemon to name the address before (or without) starting a server.</summary>
    public static ServerNetwork NetworkOf(WorkspaceRequest workspace, Guid id)
    {
        var root = ListRoot(workspace);
        return InstanceRules.Resolve(Load(Path.Combine(root, id.ToString("D")), id).Definition?.Network);
    }
    // Metadata writes are temp-file renames, so a lock-free reader sees either the old or the new
    // complete file. Roots and markers are still validated before reading.
    private static string ListRoot(WorkspaceRequest workspace)
    {
        var configured = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
        LocalPaths.ValidateRoots(configured.SettingsRoot, configured.DataRoot, configured.LibraryRoot);
        var root = Path.Combine(configured.DataRoot, "instances");
        LocalPaths.RejectLink(root);
        return root;
    }
    private static string Id(Guid id) => id != Guid.Empty ? id.ToString("D") : throw new InstanceException("HCSL-INSTANCE-ID", "A stable ID is required.");
    private static void Safe(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InstanceException("HCSL-INSTANCE-PATH", "Linked instance content is not accepted.");
    }
    private static T Read<T>(string file)
    {
        Safe(file);
        if (new FileInfo(file).Length > 1024 * 1024) throw new IOException("Metadata too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) ?? throw new IOException("Empty metadata.");
    }
    private static void Write<T>(string file, T value)
    {
        Safe(file);
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static InstanceEntry Load(string path, Guid id)
    {
        Safe(path);
        var d = Read<InstanceDefinition>(Path.Combine(path, "instance.json"));
        if (d.FormatVersion != 1 || d.Id != id || d.Revision < 1 || d.GamePort is < 1 or > 65535 || d.LayoutVersion is < 1 or > InstanceRules.CurrentLayoutVersion) throw new IOException("Invalid instance metadata.");
        InstanceRules.Name(d.Name);
        var presets = Path.Combine(path, "presets"); Safe(presets);
        var list = new List<PresetDefinition>();
        foreach (var dir in Directory.GetDirectories(presets))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "D", out var pid)) continue;
            Safe(dir);
            var p = Read<PresetDefinition>(Path.Combine(dir, "preset.json"));
            if (p.FormatVersion != 1 || p.Id != pid || p.Revision < 1) throw new IOException("Invalid preset metadata.");
            InstanceRules.Preset(p); list.Add(p);
        }
        if (!list.Any(p => p.Id == d.SelectedPresetId)) throw new IOException("Selected preset is missing.");
        return new(id, d, list.OrderBy(p => p.Name).ToArray(), HasPassword: InstancePasswordConfig.Read(path).Length != 0);
    }
    private static InstanceCatalog Catalog(string root)
    {
        var entries = new List<InstanceEntry>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "D", out var id)) continue;
            try { entries.Add(Load(dir, id)); }
            catch (Exception e) when (e is IOException or JsonException or InstanceException or UnauthorizedAccessException)
            { entries.Add(new(id, null, [], "HCSL-INSTANCE-CORRUPT")); }
        }
        return new(entries.OrderBy(e => e.Definition?.Name ?? e.Id.ToString()).ToArray());
    }
    private static void CheckRevision(long actual, long expected)
    {
        if (actual != expected) throw new InstanceException("HCSL-INSTANCE-CONFLICT", "Configuration changed. Refresh before saving.");
    }
    private static void Port(string root, int port, Guid except = default)
    {
        if (port is < 1 or > 65535) throw new InstanceException("HCSL-INSTANCE-PORT", "Port must be between 1 and 65535.");
        var catalog = Catalog(root);
        if (catalog.Instances.Any(e => e.Error != null)) throw new InstanceException("HCSL-INSTANCE-CORRUPT", "Resolve damaged instance metadata before assigning ports.");
        if (catalog.Instances.Any(e => e.Id != except && e.Definition!.GamePort == port))
            throw new InstanceException("HCSL-INSTANCE-PORT", "Port is already assigned to an instance.");
    }
    private static void CreatePreset(string path, PresetDefinition preset, string? source = null)
    {
        InstanceRules.Preset(preset);
        var stage = path + ".creating-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        // Interrupted staging is deliberately retained and never published as a valid preset.
        foreach (var child in new[] { "cfg", "addons" }) Directory.CreateDirectory(Path.Combine(stage, child));
        if (source != null)
        {
            Copy(Path.Combine(source, "cfg"), Path.Combine(stage, "cfg"));
            var maps = Path.Combine(source, "maps.json"); Safe(maps);
            LocalPresetMapPoolStore.ValidateForCopy(maps);
            if (File.Exists(maps)) File.Copy(maps, Path.Combine(stage, "maps.json"));
            foreach (var name in new[] { "plugins.json", "authorization.json" })
            { var file = Path.Combine(source, name); Safe(file); File.Copy(file, Path.Combine(stage, name)); }
        }
        else
        {
            Write(Path.Combine(stage, "plugins.json"), new { FormatVersion = 1, Plugins = Array.Empty<object>() });
            Write(Path.Combine(stage, "authorization.json"), new { FormatVersion = 1, Rules = Array.Empty<object>() });
        }
        Write(Path.Combine(stage, "preset.json"), preset);
        Directory.Move(stage, path);
    }
    private static void Copy(string source, string destination)
    {
        Safe(source);
        foreach (var file in Directory.GetFiles(source))
        {
            // Generated per-run reference to the instance secret is never a preset asset.
            if (Path.GetFileName(file) == InstancePasswordConfig.ExecName) continue;
            Safe(file); File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(source))
        { Safe(dir); var target = Path.Combine(destination, Path.GetFileName(dir)); Directory.CreateDirectory(target); Copy(dir, target); }
    }
    private static void MigrateLayout(string path, string file, InstanceDefinition d, InstanceEntry entry)
    {
        if (d.LayoutVersion >= InstanceRules.CurrentLayoutVersion) return;
        var selected = entry.Presets.Single(p => p.Id == d.SelectedPresetId);
        var homeSource = Path.Combine(path, "presets", selected.Id.ToString("D"), "home");
        var userdataSource = Path.Combine(path, "presets", selected.Id.ToString("D"), "userdata");
        var homeTarget = Path.Combine(path, "home");
        var userdataTarget = Path.Combine(path, "userdata");
        if (!CanMigrateTree(homeTarget, homeSource) || !CanMigrateTree(userdataTarget, userdataSource))
            throw new InstanceException("HCSL-INSTANCE-LAYOUT-CONFLICT", "Instance-level home or userdata already contains different data.");
        foreach (var preset in entry.Presets.Where(p => p.Id != selected.Id))
        {
            var otherUserdata = Path.Combine(path, "presets", preset.Id.ToString("D"), "userdata");
            // HOME contains per-run Steam caches; the selected preset is the identity source.
            // Different userdata can contain real per-preset settings and must not be merged.
            if (!SameTree(userdataSource, otherUserdata))
                throw new InstanceException("HCSL-INSTANCE-LAYOUT-CONFLICT", "Another preset has different userdata; choose a single source manually.");
        }
        var stage = path + ".layout-v2-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            StageTree(homeSource, Path.Combine(stage, "home"));
            StageTree(userdataSource, Path.Combine(stage, "userdata"));
            PublishTree(Path.Combine(stage, "home"), homeTarget);
            PublishTree(Path.Combine(stage, "userdata"), userdataTarget);
            var backup = Path.Combine(path, "backups", "layout-v1-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            foreach (var preset in entry.Presets)
            {
                var source = Path.Combine(path, "presets", preset.Id.ToString("D"));
                foreach (var name in new[] { "home", "userdata" })
                {
                    var item = Path.Combine(source, name);
                    if (!Directory.Exists(item)) continue;
                    var target = Path.Combine(backup, "presets", preset.Id.ToString("D"), name);
                    Directory.CreateDirectory(target);
                    Copy(item, target);
                }
            }
            Write(file, d with { LayoutVersion = InstanceRules.CurrentLayoutVersion, Revision = d.Revision + 1 });
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch (IOException) { }
        }
    }
    private static bool CanMigrateTree(string target, string source)
    {
        if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any()) return true;
        return SameTree(source, target);
    }
    private static void StageTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        if (Directory.Exists(source)) Copy(source, target);
    }
    private static void PublishTree(string source, string target)
    {
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            if (!SameTree(source, target))
                throw new InstanceException("HCSL-INSTANCE-LAYOUT-CONFLICT", "Instance-level data changed during migration.");
            return;
        }
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(source, target);
    }
    private static bool SameTree(string left, string right)
    {
        var leftExists = Directory.Exists(left); var rightExists = Directory.Exists(right);
        if (!leftExists && !rightExists) return true;
        if (!leftExists || !rightExists) return !HasEntries(left) && !HasEntries(right);
        var l = Files(left); var r = Files(right);
        if (!l.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(r.Keys.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)) return false;
        foreach (var pair in l)
            if (pair.Value.Length != r[pair.Key].Length || !Hash(pair.Value).SequenceEqual(Hash(r[pair.Key]))) return false;
        return true;
        static Dictionary<string, string> Files(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                Safe(file);
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                // Steam writes rotating logs into HOME; they are not part of the instance identity.
                if (relative.StartsWith("Steam/logs/", StringComparison.OrdinalIgnoreCase)) continue;
                result[relative] = file;
            }
            return result;
        }
    }
    private static bool HasEntries(string path) => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
    private static byte[] Hash(string path) { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
    private static void Recycle(string root, string source)
    {
        var trash = Path.Combine(root, ".trash"); Safe(trash); Directory.CreateDirectory(trash);
        Directory.Move(source, Path.Combine(trash, Path.GetFileName(source) + "-" + Guid.NewGuid().ToString("N")));
    }
    private static void Mutate(string root, InstanceCommand c)
    {
        if (c.Action == InstanceAction.Create)
        {
            InstanceRules.Name(c.Name); Port(root, c.GamePort); InstanceRules.Network(c.Network);
            var id = Guid.NewGuid(); var pid = Guid.NewGuid();
            var stage = Path.Combine(root, ".creating-" + id); Directory.CreateDirectory(stage);
            foreach (var dir in new[] { "presets", "recordings", "logs", "backups", "home", "userdata" }) Directory.CreateDirectory(Path.Combine(stage, dir));
            CreatePreset(Path.Combine(stage, "presets", Id(pid)), new(1, pid, 1, "Default"));
            Write(Path.Combine(stage, "instance.json"), new InstanceDefinition(1, id, 1, c.Name!.Trim(), c.GamePort, pid, c.Network, InstanceRules.CurrentLayoutVersion));
            Directory.Move(stage, Path.Combine(root, Id(id))); return;
        }
        var path = Path.Combine(root, Id(c.InstanceId));
        InstanceEntry entry;
        try { entry = Load(path, c.InstanceId); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { throw new InstanceException("HCSL-INSTANCE-CORRUPT", "Instance metadata cannot be read; original files were preserved."); }
        var d = entry.Definition!;
        CheckRevision(d.Revision, c.Revision);
        var file = Path.Combine(path, "instance.json");
        if (c.Action == InstanceAction.SetPassword)
        {
            InstanceRules.Password(c.Password);
            InstancePasswordConfig.Write(path, c.Password!);
            // No gameplay/deployment revision change: password applies at next start,
            // independently of preset selection and without forcing a 70GB redeploy.
            return;
        }
        if (c.Action == InstanceAction.Delete) { Recycle(root, path); return; }
        if (c.Action == InstanceAction.Update)
        {
            InstanceRules.Name(c.Name); Port(root, c.GamePort, d.Id); InstanceRules.Network(c.Network);
            // A command that omits Network leaves the stored address alone, so older callers cannot reset
            // an instance to loopback by accident.
            Write(file, d with { Name = c.Name!.Trim(), GamePort = c.GamePort, Network = c.Network ?? d.Network, Revision = d.Revision + 1 }); return;
        }
        if (c.Action == InstanceAction.MigrateLayout)
        {
            MigrateLayout(path, file, d, entry);
            return;
        }
        var parent = Path.Combine(path, "presets");
        if (c.Action == InstanceAction.CreatePreset)
        {
            InstanceRules.Name(c.Name);
            var initial = c.Preset ?? new PresetDefinition(1, Guid.Empty, 1, c.Name!.Trim());
            InstanceRules.Preset(initial);
            Write(file, d with { Revision = d.Revision + 1 });
            var pid = Guid.NewGuid(); CreatePreset(Path.Combine(parent, Id(pid)), initial with { FormatVersion = 1, Id = pid, Revision = 1, Name = c.Name!.Trim() });
        }
        else
        {
            var p = entry.Presets.SingleOrDefault(p => p.Id == c.PresetId) ?? throw new InstanceException("HCSL-PRESET-MISSING", "Preset was not found.");
            var dir = Path.Combine(parent, Id(p.Id));
            switch (c.Action)
            {
                case InstanceAction.SelectPreset:
                    Write(file, d with { SelectedPresetId = p.Id, Revision = d.Revision + 1 }); return;
                case InstanceAction.UpdatePreset:
                    var next = c.Preset ?? throw new InstanceException("HCSL-PRESET-INVALID", "Preset is required.");
                    CheckRevision(p.Revision, next.Revision); InstanceRules.Preset(next);
                    Write(file, d with { Revision = d.Revision + 1 });
                    Write(Path.Combine(dir, "preset.json"), next with { Id = p.Id, FormatVersion = 1, Revision = p.Revision + 1 }); break;
                case InstanceAction.CopyPreset:
                    InstanceRules.Name(c.Name); var pid = Guid.NewGuid();
                    Write(file, d with { Revision = d.Revision + 1 });
                    CreatePreset(Path.Combine(parent, Id(pid)), p with { Id = pid, Name = c.Name!.Trim(), Revision = 1 }, dir); break;
                case InstanceAction.DeletePreset:
                    if (p.Id == d.SelectedPresetId || entry.Presets.Length <= 1) throw new InstanceException("HCSL-PRESET-SELECTED", "Select another preset before deleting this one.");
                    Write(file, d with { Revision = d.Revision + 1 });
                    Recycle(root, dir); break;
                default: throw new InstanceException("HCSL-INSTANCE-REQUEST", "Unknown instance action.");
            }
        }
        // A revision may be consumed on failed I/O, but published child changes never retain the old revision.
    }
}
