using System.Diagnostics;
using HCSL.Infrastructure.Steam;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Deployment;

/// <summary>Offline only. Every operation holds the same cross-process leases as SteamCMD and instance editing.</summary>
public sealed class RuntimeDeploymentService : IRuntimeDeploymentService
{
    internal sealed record InventoryFile(string Path, long Bytes, string Sha256);
    internal sealed record Baseline(string BuildId, string EntrypointSha256, InventoryFile[] Inventory, string[] Configs);
    private sealed record InstallState(int FormatVersion, int AppId, string Status, string? BuildId);
    internal sealed record Transaction(int FormatVersion, Guid OperationId, Guid InstanceId, Guid DataId, Guid LibraryId);
    private readonly Baseline baseline;
    private readonly Action<string>? checkpoint;
    public RuntimeDeploymentService() : this(ReadBaseline()) { }
    internal RuntimeDeploymentService(Baseline baseline, Action<string>? checkpoint = null) { this.baseline = baseline; this.checkpoint = checkpoint; }
    private static Baseline ReadBaseline()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HCSL.LinuxStartupBaseline")!;
        using var json = JsonDocument.Parse(stream); var root = json.RootElement;
        return new(root.GetProperty("buildId").GetString()!, root.GetProperty("entrypointSha256").GetString()!,
            root.GetProperty("conservativeCopyInventory").EnumerateArray().Select(f => new InventoryFile(f.GetProperty("path").GetString()!, f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!)).ToArray(),
            root.GetProperty("independentConfigPaths").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }
    private static void Platform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DeploymentException("HCSL-DEPLOY-PLATFORM", "Only the verified Linux x64 offline deployment baseline is supported.");
    }
    private static string Id(Guid id) => id == Guid.Empty ? throw new DeploymentException("HCSL-DEPLOY-ID", "Instance ID is required.") : id.ToString("D");
    private static string Journal(WorkspaceLayout l, Guid id) => Path.Combine(l.LibraryRoot, ".hcsl", "deployment-" + Id(id) + ".json");
    private static string Target(WorkspaceLayout l, Guid id) => Path.Combine(l.RuntimeRoot, Id(id));
    private static string Stage(WorkspaceLayout l, Transaction t) => Path.Combine(l.RuntimeRoot, ".staging-" + Id(t.InstanceId) + "-" + t.OperationId.ToString("N"));
    private static string Previous(WorkspaceLayout l, Transaction t) => Path.Combine(l.RuntimeRoot, ".previous-" + Id(t.InstanceId) + "-" + t.OperationId.ToString("N"));
    private static string InRoot(string root, string relative)
    {
        var path = root;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new DeploymentException("HCSL-DEPLOY-PATH", "Invalid relative path.");
        foreach (var part in relative.Split('/'))
        {
            if (part is ".." or "." or "" || part.Contains('\\')) throw new DeploymentException("HCSL-DEPLOY-PATH", "Invalid path component.");
            path = Path.Combine(path, part); LocalPaths.RejectLink(path);
        }
        return path;
    }
    public DeploymentPlan Plan(WorkspaceRequest workspace, DeploymentOptions options, CancellationToken cancellationToken = default)
    {
        Platform(); using var lease = new LocalWorkspaceManager().Acquire(workspace);
        return Prepare(lease, options, cancellationToken);
    }
    internal DeploymentPlan Prepare(IWorkspaceLease lease, DeploymentOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(options.CopyMode)) throw new DeploymentException("HCSL-DEPLOY-MODE", "Unknown copy policy.");
        var l = lease.Layout; var id = options.InstanceId; Id(id);
        if (File.Exists(Journal(l, id))) throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "An interrupted deployment must be recovered first.");
        foreach (var directory in new[] { Path.Combine(l.SteamCmdRoot, "tool"), SteamCmdService.UserCoordinationDirectory() })
        {
            var steamJournal = SteamCmdJournal.PathFor(directory);
            LocalPaths.RejectLink(steamJournal); LocalPaths.RejectLink(steamJournal + ".tmp");
            if (File.Exists(steamJournal) || File.Exists(steamJournal + ".tmp"))
                throw new DeploymentException("HCSL-DEPLOY-STEAMCMD", "An unfinished SteamCMD session must be recovered before deployment.");
        }
        var entry = LocalInstanceStore.Load(InRoot(l.InstancesRoot, Id(id)), id);
        var d = entry.Definition!; var p = entry.Presets.Single(p => p.Id == d.SelectedPresetId);
        if (options.ExpectedRevision is { } revision && d.Revision != revision)
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "Instance changed since the plan was reviewed.");
        var instance = InRoot(l.InstancesRoot, Id(id)); var preset = InRoot(instance, "presets/" + Id(p.Id));
        PresetMapPool mapPool;
        try { mapPool = LocalPresetMapPoolStore.ReadForDeployment(InRoot(preset, "maps.json")); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
        string? collection;
        ServerMapGroupPlan? rotation;
        try
        {
            collection = MapPoolRules.CollectionId(mapPool);
            rotation = collection is null ? ServerMapGroupRules.Create(p.Id, p.Map, mapPool) : null;
        }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
        var pluginSet = PluginSet(preset, l.LibraryRoot);
        var authorization = DeploymentFiles.Read<JsonElement>(InRoot(preset, "authorization.json"));
        if (!authorization.TryGetProperty("FormatVersion", out var av) || av.GetInt32() != 1 ||
            !authorization.TryGetProperty("Rules", out var rules) || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() != 0)
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Access rules are not deployable yet; nonempty or unknown manifests are refused.");
        var state = DeploymentFiles.Read<InstallState>(InRoot(l.LibraryRoot, ".hcsl/cs2-install.json"));
        if (state is not { FormatVersion: 1, AppId: 730, Status: "Ready" } || state.BuildId != baseline.BuildId)
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Ready installation must match the verified deployment baseline. Update the baseline after a new game build.");
        var manifest = SmallText(InRoot(l.ServerRoot, "steamapps/appmanifest_730.acf"));
        if (!Regex.IsMatch(manifest, "\"appid\"\\s*\"730\"", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(manifest, "\"StateFlags\"\\s*\"4\"", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(manifest, "\"buildid\"\\s*\"" + Regex.Escape(state.BuildId!) + "\"", RegexOptions.IgnoreCase))
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Steam manifest and installation state disagree.");
        var info = SmallText(InRoot(l.ServerRoot, "game/csgo/steam.inf"));
        if (!Regex.IsMatch(info, @"(?im)^\s*appid\s*=\s*730\s*$")) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Invalid steam.inf.");
        if (collection is null)
        {
            var map = InRoot(l.ServerRoot, "game/csgo/maps/" + p.Map + ".vpk");
            if (!File.Exists(map) || new FileInfo(map).Length == 0)
                throw new DeploymentException("HCSL-DEPLOY-MAP", "Selected map is not present as a built-in map package; Workshop deployment is not implemented.");
        }
        if (rotation is not null)
        {
            foreach (var pooledMap in mapPool.Maps)
            {
                var package = InRoot(l.ServerRoot, "game/csgo/maps/" + pooledMap.Value + ".vpk");
                if (!File.Exists(package) || new FileInfo(package).Length == 0)
                    throw new DeploymentException("HCSL-DEPLOY-ROTATION", "Every standard map in the pool must be installed as a built-in map package.");
            }
            var existingRotation = InRoot(l.ServerRoot, "game/csgo/" + ServerMapGroupRules.FileName);
            if (File.Exists(existingRotation) && new FileInfo(existingRotation).Length != 0)
                throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The base installation already has a nonempty gamemodes_server.txt; merge it manually before enabling a managed map group.");
        }
        if (collection is not null)
        {
            var seed = InRoot(l.ServerRoot, "game/csgo/maps/" + ServerMapRules.CollectionSeedMap + ".vpk");
            if (!File.Exists(seed) || new FileInfo(seed).Length == 0)
                throw new DeploymentException("HCSL-DEPLOY-MAP", "The built-in seed map required to start a Workshop collection is missing.");
        }
        VerifyBaseline(l.ServerRoot, ct);
        var sdk = InRoot(l.SteamCmdRoot, "tool/linux64/steamclient.so");
        if (!File.Exists(sdk) || new FileInfo(sdk).Length == 0) throw new DeploymentException("HCSL-DEPLOY-SDK", "Managed SteamCMD linux64/steamclient.so is missing.");
        var mappings = new[]
        {
            new DeploymentMapping("game/csgo/cfg", InRoot(preset, "cfg")),
            new DeploymentMapping("game/csgo/addons", InRoot(preset, "addons")),
            new DeploymentMapping("game/csgo/logs", InRoot(instance, "logs")),
            new DeploymentMapping("game/csgo/recordings", InRoot(instance, "recordings")),
            new DeploymentMapping("userdata", InRoot(preset, "userdata")),
            new DeploymentMapping("home", InRoot(preset, "home"))
        };
        foreach (var m in mappings) if (Directory.Exists(m.PersistentPath)) DeploymentFiles.Tree(m.PersistentPath, _ => { }, ct);
        // SDK ancestors may not exist yet; check their existing components before any seeding.
        var existingSdk = InRoot(preset, "home/.steam/sdk64/steamclient.so");
        if (File.Exists(existingSdk) && new FileInfo(existingSdk).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-SDK", "Persistent Steam SDK file is empty; inspect it before deployment.");
        foreach (var reserved in new[] { "game/csgo/addons", "game/csgo/logs", "game/csgo/recordings" })
        {
            var source = InRoot(l.ServerRoot, reserved);
            if (File.Exists(source) || (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any()))
                throw new DeploymentException("HCSL-DEPLOY-MAPPING", "Base installation contains content reserved for an instance; import must be handled explicitly.");
        }
        int count = 0; long bytes = 0, seedBytes = new FileInfo(sdk).Length;
        DeploymentFiles.Tree(InRoot(l.ServerRoot, "game"), f =>
        {
            count++; bytes = checked(bytes + new FileInfo(f).Length);
            var relative = Path.GetRelativePath(l.ServerRoot, f).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("game/csgo/cfg/", StringComparison.Ordinal)) seedBytes = checked(seedBytes + new FileInfo(f).Length);
        }, ct);
        if (count == 0) throw new DeploymentException("HCSL-DEPLOY-FILES", "Empty game installation.");
        var minimum = checked(256L * 1024 * 1024 + count * 4096L + (options.CopyMode == DeploymentCopyMode.FullCopy ? bytes : 0));
        if (lease.Capacity.LibraryAvailableBytes < minimum || lease.Capacity.DataAvailableBytes < seedBytes + 64L * 1024 * 1024 ||
            (Path.GetPathRoot(l.LibraryRoot) == Path.GetPathRoot(l.DataRoot) && lease.Capacity.LibraryAvailableBytes < minimum + seedBytes + 64L * 1024 * 1024))
            throw new DeploymentException("HCSL-DEPLOY-SPACE", "Insufficient free space for the selected deployment policy and persistent seeds.");
        var target = Target(l, id); ValidateExisting(lease, id, target, verifyRotation: false); EnsureStopped(target);
        var notes = new List<string>
        {
            "Offline deployment only; no server is launched.", "No resource links are approved; the entire game tree is independently cloned.",
            "Reflink support is tested during deployment, with no silent full-copy fallback.", "CoW writes can consume additional disk space later.",
            "Missing base CFG files are seeded once; existing persistent CFG files are never overwritten.", "Previous deployments and unknown runtime writes are retained, not deleted."
        };
        if (rotation is not null) notes.Add($"A launcher-owned CS2 map group '{rotation.Name}' is generated; pool order does not prove the engine's actual next map.");
        else if (collection is not null) notes.Add($"Workshop collection #{collection} is hosted natively and supplies its own map order; the preset startup map is ignored.");
        else if (mapPool.Maps.Length != 0)
            notes.Add("Workshop or mixed map pools are not deployed as a native map group; use confirmed live map changes.");
        if (pluginSet.Packages.Length != 0)
            notes.Add($"{pluginSet.Packages.Length} plugin package(s) are copied into the preset addons directory; package-owned addons files are replaced on redeploy.");
        else notes.Add("No plugin packages are enabled.");
        return new(id, d.Revision, p.Id, p.Revision, state.BuildId!, target, options.CopyMode, count, bytes, minimum, seedBytes + 64L * 1024 * 1024, mappings,
            [.. notes], rotation?.Name, rotation?.Hash, collection, pluginSet.Hash);
    }
    private static ServerMapGroupPlan? Rotation(string presetPath, PresetDefinition preset)
    {
        try { return ServerMapGroupRules.Create(preset.Id, preset.Map, LocalPresetMapPoolStore.ReadForDeployment(InRoot(presetPath, "maps.json"))); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
    }
    private static string? Collection(string presetPath)
    {
        try { return MapPoolRules.CollectionId(LocalPresetMapPoolStore.ReadForDeployment(InRoot(presetPath, "maps.json"))); }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-ROTATION", e.Message); }
    }
    private static (string? Hash, LocalPluginStore.Package[] Packages) PluginSet(string presetPath, string libraryRoot)
    {
        try
        {
            var manifest = LocalPluginStore.ReadManifestForDeployment(InRoot(presetPath, "plugins.json"));
            var packages = new List<LocalPluginStore.Package>();
            foreach (var plugin in manifest.Plugins.Where(p => p.Enabled))
                packages.Add(LocalPluginStore.LoadPackage(Path.Combine(libraryRoot, "plugin-packages"), plugin.Id, plugin.Version));
            using var sha = SHA256.Create();
            foreach (var package in packages)
            {
                var line = Encoding.UTF8.GetBytes(package.Id + "\0" + package.Version + "\0" + package.Hash + "\n");
                sha.TransformBlock(line, 0, line.Length, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            return (packages.Count == 0 ? null : Convert.ToHexString(sha.Hash!), packages.ToArray());
        }
        catch (InstanceException e) { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException) { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", e.Message); }
    }
    private sealed record PluginLedger(int FormatVersion, PluginLedgerEntry[] Packages);
    private sealed record PluginLedgerEntry(string Id, string Version, PluginLedgerFile[] Files);
    private sealed record PluginLedgerFile(string Path, string Sha256);
    private static PluginLedger ReadPluginLedger(string path)
    {
        LocalPaths.RejectLink(path);
        if (!File.Exists(path)) return new(1, []);
        try
        {
            var ledger = DeploymentFiles.Read<PluginLedger>(path);
            if (ledger.FormatVersion != 1 || ledger.Packages is null) throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Unknown plugin install ledger.");
            return ledger;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin install ledger cannot be read; no plugin files were changed."); }
    }
    private static string AddonsPath(string addons, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Invalid plugin install path.");
        var path = addons;
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or "..") throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Invalid plugin install path.");
            path = Path.Combine(path, part); LocalPaths.RejectLink(path);
        }
        return path;
    }
    private static void CopyPluginFile(string source, string destination, string sha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".plugin-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temp, false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(source));
            // Hash the copy, not the source: a package edited after validation must not reach the preset.
            if (!string.Equals(HashFile(temp), sha256, StringComparison.OrdinalIgnoreCase))
                throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "A plugin package file changed after it was validated; no persistent plugin file was replaced.");
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool IsUnder(string path, string root) =>
        path.Equals(root, LocalPaths.Comparison) || path.StartsWith(root + "/", LocalPaths.Comparison);
    private static void RejectReservedPluginPath(string relative)
    {
        if (IsUnder(relative, "csgo/cfg") || IsUnder(relative, "csgo/logs") || IsUnder(relative, "csgo/recordings"))
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin packages may not write preset CFG, logs or recordings.");
        if (relative.Equals("csgo/addons", LocalPaths.Comparison))
            throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "A plugin package file may not replace the addons directory.");
    }
    private sealed record PluginAddonFile(string RelativePath, string SourcePath, string Sha256);
    /// <summary>Persistent addon changes prepared during staging and applied once, with an on-disk backup for rollback.</summary>
    private sealed class PluginAddonPlan(string addons, string ledgerPath, PluginAddonFile[] writes, PluginAddonFile[] removals, PluginLedger ledger)
    {
        private readonly string backupRoot = Path.Combine(Path.GetDirectoryName(addons)!, ".hcsl-plugin-backup-" + Guid.NewGuid().ToString("N"));
        private readonly List<(string Path, bool Existed)> applied = [];
        private bool ledgerExisted, settled;
        private string Destination(string relative) => AddonsPath(addons, relative["csgo/addons/".Length..]);
        private string BackupPath(string destination) => Path.Combine(backupRoot, "files", Path.GetRelativePath(addons, destination));
        private void Backup(string destination)
        {
            var backup = BackupPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(destination, backup, true);
        }
        internal void Apply()
        {
            try
            {
                Directory.CreateDirectory(backupRoot);
                if (File.Exists(ledgerPath)) { File.Copy(ledgerPath, Path.Combine(backupRoot, "ledger.json"), true); ledgerExisted = true; }
                foreach (var write in writes)
                {
                    var destination = Destination(write.RelativePath);
                    var existed = File.Exists(destination);
                    if (existed) Backup(destination);
                    applied.Add((destination, existed));
                    CopyPluginFile(write.SourcePath, destination, write.Sha256);
                }
                foreach (var removal in removals)
                {
                    var destination = Destination(removal.RelativePath);
                    if (!File.Exists(destination)) continue;
                    if (!string.Equals(HashFile(destination), removal.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    Backup(destination);
                    File.Delete(destination);
                    PruneEmptyDirectories(Path.GetDirectoryName(destination));
                }
                DeploymentFiles.AtomicJson(ledgerPath, ledger);
            }
            catch (Exception e)
            {
                try { Rollback(); }
                catch (Exception rollback)
                {
                    throw new DeploymentException("HCSL-DEPLOY-PLUGINS",
                        "Plugin addon files could not be written and the backup could not be restored; inspect the preset addons directory (" + rollback.GetType().Name + ").");
                }
                if (e is DeploymentException) throw;
                throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin addon files could not be written; persistent plugin files were restored.");
            }
        }
        internal void Commit()
        {
            if (settled) return;
            settled = true;
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
        }
        internal void Rollback()
        {
            if (settled) return;
            try
            {
                for (var i = applied.Count - 1; i >= 0; i--)
                    if (!applied[i].Existed && File.Exists(applied[i].Path)) File.Delete(applied[i].Path);
                var files = Path.Combine(backupRoot, "files");
                if (Directory.Exists(files))
                    foreach (var backup in Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories))
                    {
                        var destination = Path.Combine(addons, Path.GetRelativePath(files, backup));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(backup, destination, true);
                    }
                var ledgerBackup = Path.Combine(backupRoot, "ledger.json");
                if (File.Exists(ledgerBackup)) File.Copy(ledgerBackup, ledgerPath, true);
                else if (!ledgerExisted && File.Exists(ledgerPath)) File.Delete(ledgerPath);
            }
            finally
            {
                settled = true;
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
            }
        }
        private void PruneEmptyDirectories(string? directory)
        {
            for (; directory is not null && !string.Equals(directory, addons, LocalPaths.Comparison); directory = Path.GetDirectoryName(directory))
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
                Directory.Delete(directory);
            }
        }
    }
    /// <summary>Builds the addon change plan and overlays non-addon package files into the staging tree. Persistent addons are untouched here.</summary>
    private static PluginAddonPlan PlanPlugins(string stage, string presetPath, LocalPluginStore.Package[] packages)
    {
        try
        {
            var addons = InRoot(presetPath, "addons"); LocalPaths.RejectLink(addons);
            if (!Directory.Exists(addons)) throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Preset addons directory is missing.");
            var ledgerPath = InRoot(addons, ".hcsl-plugin-ledger.json");
            var old = ReadPluginLedger(ledgerPath);
            var writes = new List<PluginAddonFile>();
            var installed = new List<PluginLedgerEntry>();
            // Windows compares paths case-insensitively; the ledger and reserved roots must follow the same rule.
            var keep = new HashSet<string>(StringComparer.FromComparison(LocalPaths.Comparison));
            foreach (var package in packages)
            {
                var files = new List<PluginLedgerFile>();
                foreach (var file in package.Files)
                {
                    RejectReservedPluginPath(file.RelativePath);
                    if (IsUnder(file.RelativePath, "csgo/addons"))
                    {
                        writes.Add(new(file.RelativePath, file.FullPath, file.Sha256));
                        keep.Add(file.RelativePath);
                        files.Add(new(file.RelativePath, file.Sha256));
                    }
                    else CopyPluginFile(file.FullPath, InRoot(stage, "game/" + file.RelativePath), file.Sha256);
                }
                installed.Add(new(package.Id, package.Version, [.. files]));
            }
            var removals = new List<PluginAddonFile>();
            foreach (var oldPackage in old.Packages ?? [])
            {
                foreach (var file in oldPackage.Files ?? [])
                {
                    if (keep.Contains(file.Path) || !IsUnder(file.Path, "csgo/addons")) continue;
                    string destination;
                    try { destination = AddonsPath(addons, file.Path["csgo/addons/".Length..]); }
                    catch (DeploymentException) { continue; }
                    if (!File.Exists(destination)) continue;
                    if (!string.Equals(HashFile(destination), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    removals.Add(new(file.Path, destination, file.Sha256));
                }
            }
            return new(addons, ledgerPath, [.. writes], [.. removals], new PluginLedger(1, [.. installed]));
        }
        catch (DeploymentException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException or InvalidDataException or NotSupportedException)
        { throw new DeploymentException("HCSL-DEPLOY-PLUGINS", "Plugin files could not be prepared; no persistent plugin file was changed."); }
    }
    private static void ClearRotation(string stage)
    {
        var path = InRoot(stage, "game/csgo/" + ServerMapGroupRules.FileName); LocalPaths.RejectLink(path);
        if (!File.Exists(path)) return;
        if (!File.ReadAllText(path).StartsWith(ServerMapGroupRules.ManagedMarker, StringComparison.Ordinal)) return;
        File.Delete(path);
    }
    private static void WriteRotation(string stage, ServerMapGroupPlan rotation)
    {
        var path = InRoot(stage, "game/csgo/" + ServerMapGroupRules.FileName); LocalPaths.RejectLink(path);
        if (File.Exists(path) && new FileInfo(path).Length != 0)
            throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The staged game already has a nonempty gamemodes_server.txt.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(rotation.Content);
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string SmallText(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new DeploymentException("HCSL-DEPLOY-STATE", "Installation metadata is too large.");
        return File.ReadAllText(path);
    }
    internal void VerifyBaseline(string server, CancellationToken ct)
    {
        VerifyHash(InRoot(server, "game/cs2.sh"), baseline.EntrypointSha256);
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(InRoot(server, "game/cs2.sh")) & UnixFileMode.UserExecute) == 0)
            throw new DeploymentException("HCSL-DEPLOY-FILES", "cs2.sh is not executable.");
        foreach (var f in baseline.Inventory)
        {
            ct.ThrowIfCancellationRequested(); var path = InRoot(server, f.Path);
            if (new FileInfo(path).Length != f.Bytes) throw new DeploymentException("HCSL-DEPLOY-BASELINE", "Native file size differs from the verified baseline.");
            VerifyHash(path, f.Sha256);
        }
        foreach (var config in baseline.Configs) if (new FileInfo(InRoot(server, config)).Length == 0)
            throw new DeploymentException("HCSL-DEPLOY-FILES", "Required path configuration is missing.");
    }
    internal static bool RotationFileMatches(DeploymentReceipt receipt, string target)
    {
        if (receipt.RotationCollection is not null) return receipt.RotationGroup is null && receipt.RotationHash is null;
        if (receipt.RotationHash is null) return true;
        var rotation = InRoot(target, "game/csgo/" + ServerMapGroupRules.FileName); LocalPaths.RejectLink(rotation);
        return File.Exists(rotation) && HashFile(rotation).Equals(receipt.RotationHash, StringComparison.Ordinal);
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void VerifyHash(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new DeploymentException("HCSL-DEPLOY-BASELINE", "A native file or entrypoint differs from the verified baseline.");
    }
    private static void EnsureStopped(string target)
    {
        LocalPaths.RejectLink(target);
        if (File.Exists(Path.Combine(target, ".hcsl-running.json"))) throw new DeploymentException("HCSL-DEPLOY-RUNNING", "Runtime is marked as in use.");
        if (!Directory.Exists(target)) return;
        foreach (var process in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(process), out var pid) || pid == Environment.ProcessId) continue;
            foreach (var name in new[] { "cwd", "exe" })
            {
                try
                {
                    var link = File.ResolveLinkTarget(Path.Combine(process, name), true)?.FullName;
                    if (link != null && LocalPaths.Contains(target, link)) throw new DeploymentException("HCSL-DEPLOY-RUNNING", "A process is using this runtime; stop it before redeploying.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    internal static DeploymentReceipt? ValidateExisting(IWorkspaceLease lease, Guid id, string target, bool verifyRotation = true)
    {
        LocalPaths.RejectLink(target);
        if (File.Exists(target)) throw new DeploymentException("HCSL-DEPLOY-UNMANAGED", "Runtime path is a file.");
        if (!Directory.Exists(target)) return null;
        var receiptFile = Path.Combine(target, "deployment.json");
        if (!File.Exists(receiptFile)) throw new DeploymentException("HCSL-DEPLOY-UNMANAGED", "Existing runtime has no deployment receipt; it was not modified.");
        var receipt = DeploymentFiles.Read<DeploymentReceipt>(receiptFile);
        if (receipt.FormatVersion != 1 || receipt.Status != "DeployedOffline" || receipt.InstanceId != id || receipt.DataId != lease.Identity.DataId || receipt.LibraryId != lease.Identity.LibraryId || receipt.OperationId == Guid.Empty)
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Runtime ownership or deployment format does not match.");
        if (receipt.PersistentMappings == null || receipt.PersistentMappings.Length != 6 || receipt.PersistentMappings.Select(m => m.RuntimePath).Distinct().Count() != 6)
            throw new DeploymentException("HCSL-DEPLOY-STATE", "Incomplete persistent mapping metadata.");
        if (verifyRotation && receipt.RotationHash is not null && !RotationFileMatches(receipt, target))
            throw new DeploymentException("HCSL-DEPLOY-ROTATION", "The generated gamemodes_server.txt is missing or modified; redeploy the preset.");
        // Unknown files are retained in previous generations. Unknown links are not followed or silently accepted.
        var instance = InRoot(lease.Layout.InstancesRoot, Id(id));
        foreach (var mapping in receipt.PersistentMappings)
        {
            var allowed = mapping.RuntimePath switch
            {
                "game/csgo/cfg" => "presets/" + Id(receipt.PresetId) + "/cfg",
                "game/csgo/addons" => "presets/" + Id(receipt.PresetId) + "/addons",
                "game/csgo/logs" => "logs", "game/csgo/recordings" => "recordings",
                "userdata" => "presets/" + Id(receipt.PresetId) + "/userdata",
                "home" => "presets/" + Id(receipt.PresetId) + "/home",
                _ => throw new DeploymentException("HCSL-DEPLOY-PATH", "Unknown persistent mapping.")
            };
            if (!string.Equals(InRoot(instance, allowed), mapping.PersistentPath, LocalPaths.Comparison))
                throw new DeploymentException("HCSL-DEPLOY-PATH", "Persistent mapping is outside its instance.");
        }
        void Walk(string dir)
        {
            LocalPaths.RejectLink(dir);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var rel = Path.GetRelativePath(target, path).Replace(Path.DirectorySeparatorChar, '/');
                var m = receipt.PersistentMappings.SingleOrDefault(m => m.RuntimePath == rel);
                if (m != null)
                {
                    var link = new DirectoryInfo(path).LinkTarget;
                    if (link == null || !Path.IsPathFullyQualified(link) || !string.Equals(Path.GetFullPath(link), m.PersistentPath, LocalPaths.Comparison))
                        throw new DeploymentException("HCSL-DEPLOY-PATH", "Persistent runtime link was modified.");
                    continue;
                }
                LocalPaths.RejectLink(path); if (Directory.Exists(path)) Walk(path);
            }
        }
        Walk(target); return receipt;
    }
    public DeploymentReceipt Deploy(WorkspaceRequest workspace, DeploymentOptions options, IProgress<DeploymentEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        Platform(); using var lease = new LocalWorkspaceManager().Acquire(workspace);
        return Deploy(lease, options, progress, cancellationToken);
    }
    /// <summary>
    /// Deploys under a lease the caller already holds. Starting a server takes the lease for the whole
    /// session, so it must deploy through this overload: acquiring a second lease in the same process
    /// fails with HCSL-LOCK.
    /// </summary>
    internal DeploymentReceipt Deploy(IWorkspaceLease lease, DeploymentOptions options, IProgress<DeploymentEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        Platform();
        var plan = Prepare(lease, options, cancellationToken); var l = lease.Layout;
        var tx = new Transaction(1, Guid.NewGuid(), options.InstanceId, lease.Identity.DataId, lease.Identity.LibraryId);
        var journal = Journal(l, tx.InstanceId); var stage = Stage(l, tx); var previous = Previous(l, tx);
        // Once this is durable, all partial files and renames have a known recovery owner.
        DeploymentFiles.AtomicJson(journal, tx); checkpoint?.Invoke("Journaled");
        Directory.CreateDirectory(stage);
        progress?.Report(new("Cloning", 0, plan.FileCount)); int done = 0;
        var sourceGame = InRoot(l.ServerRoot, "game");
        DeploymentFiles.Tree(sourceGame, file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(l.ServerRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            DeploymentFiles.Copy(file, InRoot(stage, relative), options.CopyMode);
            done++; if (done % 64 == 0 || done == plan.FileCount) progress?.Report(new("Cloning", done, plan.FileCount));
        }, cancellationToken);
        // Preserve empty directories too, since engine and plugin search paths may depend on them.
        void EmptyDirs(string source)
        {
            foreach (var dir in Directory.EnumerateDirectories(source))
            { cancellationToken.ThrowIfCancellationRequested(); LocalPaths.RejectLink(dir); Directory.CreateDirectory(InRoot(stage, Path.GetRelativePath(l.ServerRoot, dir).Replace(Path.DirectorySeparatorChar, '/'))); EmptyDirs(dir); }
        }
        EmptyDirs(sourceGame); VerifyBaseline(stage, cancellationToken); checkpoint?.Invoke("Cloned");
        progress?.Report(new("PersistentData", done, plan.FileCount));
        foreach (var mapping in plan.PersistentMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = InRoot(stage, mapping.RuntimePath);
            Directory.CreateDirectory(mapping.PersistentPath);
            if (Directory.Exists(destination))
            {
                if (mapping.RuntimePath == "game/csgo/cfg") Seed(destination, mapping.PersistentPath, cancellationToken);
                else if (Directory.EnumerateFileSystemEntries(destination).Any())
                    throw new DeploymentException("HCSL-DEPLOY-MAPPING", "Base installation contains data in a directory reserved for an instance; no data was discarded.");
                Directory.Delete(destination, true); // This is exclusively our newly cloned staging tree, never an existing deployment.
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.CreateSymbolicLink(destination, mapping.PersistentPath);
        }
        var home = plan.PersistentMappings.Single(m => m.RuntimePath == "home").PersistentPath;
        var sdk = InRoot(home, ".steam/sdk64/steamclient.so");
        if (!File.Exists(sdk)) SeedFile(InRoot(l.SteamCmdRoot, "tool/linux64/steamclient.so"), sdk);
        var presetPath = Path.Combine(InRoot(l.InstancesRoot, Id(tx.InstanceId)), "presets", Id(plan.PresetId));
        var currentCollection = Collection(presetPath);
        if (!string.Equals(currentCollection, plan.RotationCollection, StringComparison.Ordinal))
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The map pool changed while deployment was prepared. Redeploy to regenerate the rotation.");
        var currentPlugins = PluginSet(presetPath, l.LibraryRoot);
        if (!string.Equals(currentPlugins.Hash, plan.PluginSetHash, StringComparison.Ordinal))
            throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The plugin selection changed while deployment was prepared. Redeploy.");
        var pluginChanges = PlanPlugins(stage, presetPath, currentPlugins.Packages);
        if (plan.RotationHash is not null)
        {
            var entry = LocalInstanceStore.Load(InRoot(l.InstancesRoot, Id(tx.InstanceId)), tx.InstanceId);
            var preset = entry.Presets.Single(p => p.Id == plan.PresetId);
            var current = Rotation(presetPath, preset);
            if (current is null || !string.Equals(current.Hash, plan.RotationHash, StringComparison.Ordinal))
                throw new DeploymentException("HCSL-DEPLOY-CONFLICT", "The map pool changed while deployment was prepared. Redeploy to regenerate the map group.");
            WriteRotation(stage, current);
        }
        else if (currentCollection is null)
            ClearRotation(stage);
        var receipt = new DeploymentReceipt(1, tx.OperationId, "DeployedOffline", lease.Identity.DataId, lease.Identity.LibraryId,
            plan.InstanceId, plan.PresetId, plan.InstanceRevision, plan.PresetRevision, plan.BuildId, plan.CopyMode, DateTimeOffset.UtcNow, plan.PersistentMappings,
            plan.RotationGroup, plan.RotationHash, plan.RotationCollection, plan.PluginSetHash);
        DeploymentFiles.AtomicJson(Path.Combine(stage, "deployment.json"), receipt);
        ValidateExisting(lease, tx.InstanceId, stage); cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke("Prepared");
        EnsureStopped(plan.RuntimePath); ValidateExisting(lease, tx.InstanceId, plan.RuntimePath, verifyRotation: false);
        // Persistent addons are backed up and rolled back if publication fails. A crash between the two
        // renames stays recoverable through the journal and the previous generation.
        try
        {
            pluginChanges.Apply();
            checkpoint?.Invoke("PluginsApplied");
        }
        catch { pluginChanges.Rollback(); throw; }
        try
        {
            // Ignore cancellation inside the short publication window. Recovery handles process termination between renames.
            if (Directory.Exists(plan.RuntimePath)) Directory.Move(plan.RuntimePath, previous);
            checkpoint?.Invoke("PreviousMoved");
            Directory.Move(stage, plan.RuntimePath);
        }
        catch { pluginChanges.Rollback(); throw; }
        pluginChanges.Commit();
        ValidateExisting(lease, tx.InstanceId, plan.RuntimePath); checkpoint?.Invoke("Published");
        File.Delete(journal); progress?.Report(new("DeployedOffline", done, plan.FileCount)); return receipt;
    }
    private static void Seed(string source, string target, CancellationToken ct)
    {
        DeploymentFiles.Tree(source, file =>
        {
            ct.ThrowIfCancellationRequested(); var dest = InRoot(target, Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'));
            if (!File.Exists(dest)) SeedFile(file, dest);
        }, ct);
    }
    private static void SeedFile(string source, string destination)
    {
        LocalPaths.RejectLink(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".seed-" + Guid.NewGuid().ToString("N");
        try { DeploymentFiles.Copy(source, temp, DeploymentCopyMode.FullCopy); File.Move(temp, destination, false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public string Recover(WorkspaceRequest workspace, Guid instanceId)
    {
        Platform(); using var lease = new LocalWorkspaceManager().Acquire(workspace); var l = lease.Layout;
        var journal = Journal(l, instanceId); LocalPaths.RejectLink(journal);
        if (!File.Exists(journal)) return "NoPendingDeployment";
        var tx = DeploymentFiles.Read<Transaction>(journal);
        if (tx.FormatVersion != 1 || tx.OperationId == Guid.Empty || tx.InstanceId != instanceId || tx.DataId != lease.Identity.DataId || tx.LibraryId != lease.Identity.LibraryId)
            throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "Unknown deployment journal; nothing was changed.");
        var target = Target(l, instanceId); var previous = Previous(l, tx); var stage = Stage(l, tx);
        LocalPaths.RejectLink(stage); LocalPaths.RejectLink(previous); EnsureStopped(target);
        var current = ValidateExisting(lease, instanceId, target);
        if (current?.OperationId == tx.OperationId) { File.Delete(journal); return "PublishedDeploymentConfirmed"; }
        if (!Directory.Exists(target) && Directory.Exists(previous))
        {
            ValidateExisting(lease, instanceId, previous); EnsureStopped(previous); Directory.Move(previous, target);
        }
        else if (Directory.Exists(target) && Directory.Exists(previous))
            throw new DeploymentException("HCSL-DEPLOY-RECOVERY", "Both previous and unexpected current deployments exist; inspect before continuing.");
        // Partial staging trees remain intact for diagnosis. They are never promoted or recursively deleted by recovery.
        File.Delete(journal); return "PreviousDeploymentPreserved_StagingRetained";
    }
}
