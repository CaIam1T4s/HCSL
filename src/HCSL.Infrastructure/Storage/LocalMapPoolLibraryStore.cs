using System.Security.Cryptography;
using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Instance-level map pool library stored as one file per instance (<c>map-pools.json</c>). It is additive: the legacy
/// per-preset <c>maps.json</c> stays readable, and the first <c>List</c> seeds pools from it so nothing is lost.
/// </summary>
public sealed class LocalMapPoolLibraryStore
{
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<MapPoolLibrary> Json =
        MapPoolStorageJsonContext.CamelCase.MapPoolLibrary;
    private const int MaxBytes = 256 * 1024;

    public MapPoolLibraryResult Execute(WorkspaceRequest workspace, MapPoolLibraryCommand command)
    {
        if (!Enum.IsDefined(command.Action) || command.InstanceId == Guid.Empty)
            throw new InstanceException("HCSL-MAPPOOL-REQUEST", "Stable instance id and action required.");
        try
        {
            if (command.Action == MapPoolLibraryAction.List)
            {
                var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
                LocalPaths.ValidateRoots(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot);
                return ExecuteRoot(Path.Combine(paths.DataRoot, "instances"), command);
            }
            using var lease = new LocalWorkspaceManager().Acquire(workspace);
            if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
                throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before editing map pools.");
            return ExecuteRoot(lease.Layout.InstancesRoot, command);
        }
        catch (InstanceException) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
        {
            throw new InstanceException("HCSL-MAPPOOL-IO",
                "Cannot read/write map-pools.json; check format, size, links and permissions. Nothing was overwritten on a validation conflict.");
        }
    }

    private static MapPoolLibraryResult ExecuteRoot(string root, MapPoolLibraryCommand c)
    {
        Safe(root);
        var directory = Path.Combine(root, c.InstanceId.ToString("D")); Safe(directory);
        var entry = LocalInstanceStore.Load(directory, c.InstanceId);
        if (entry.Definition is null) throw new InstanceException("HCSL-MAPPOOL-INSTANCE", "Instance missing; refresh catalog.");
        var file = Path.Combine(directory, "map-pools.json"); Safe(file);
        var current = Read(file, directory, entry.Presets);
        if (c.Action == MapPoolLibraryAction.List) return current;
        if (entry.Definition.Revision != c.InstanceRevision)
            throw new InstanceException("HCSL-INSTANCE-STALE", "Instance revision changed; refresh catalog.");
        if (current.Library.Revision != c.ExpectedRevision || !string.Equals(current.Hash, c.ExpectedHash, StringComparison.Ordinal))
            throw new InstanceException("HCSL-MAPPOOL-CONFLICT", "map-pools.json changed. Reload before saving; nothing was saved.");
        var next = Apply(current.Library, c);
        MapPoolLibraryRules.Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MaxBytes) throw new InstanceException("HCSL-MAPPOOL-LIMIT", "Map pools exceed 256 KiB.");
        Write(file, bytes, current);
        return new(next, Hash(bytes));
    }

    private static MapPoolLibrary Apply(MapPoolLibrary library, MapPoolLibraryCommand c)
    {
        var revision = checked(library.Revision + 1);
        switch (c.Action)
        {
            case MapPoolLibraryAction.Create:
            {
                if (!MapPoolLibraryRules.Names.Valid(c.Name)) throw new InstanceException("HCSL-MAPPOOL-NAME", "Pool names must be 1-40 characters.");
                if (library.Pools.Length >= MapPoolLibraryRules.MaxPools)
                    throw new InstanceException("HCSL-MAPPOOL-LIMIT", "An instance holds at most 32 pools.");
                if (library.Pools.Any(p => string.Equals(p.Name.Trim(), c.Name!.Trim(), StringComparison.OrdinalIgnoreCase)))
                    throw new InstanceException("HCSL-MAPPOOL-NAME", "A pool with that name already exists.");
                var pool = new MapPoolRecord(Guid.NewGuid(), c.Name!.Trim(), c.Maps ?? [], c.CollectionMembers);
                MapPoolRules.Validate(new(1, 0, pool.Maps));
                return library with { Revision = revision, Pools = [.. library.Pools, pool] };
            }
            case MapPoolLibraryAction.Rename:
            {
                var pool = Require(library, c.PoolId);
                if (!MapPoolLibraryRules.Names.Valid(c.Name)) throw new InstanceException("HCSL-MAPPOOL-NAME", "Pool names must be 1-40 characters.");
                if (library.Pools.Any(p => p.Id != pool.Id && string.Equals(p.Name.Trim(), c.Name!.Trim(), StringComparison.OrdinalIgnoreCase)))
                    throw new InstanceException("HCSL-MAPPOOL-NAME", "A pool with that name already exists.");
                return library with
                {
                    Revision = revision,
                    Pools = library.Pools.Select(p => p.Id == pool.Id ? pool with { Name = c.Name!.Trim() } : p).ToArray()
                };
            }
            case MapPoolLibraryAction.Delete:
            {
                Require(library, c.PoolId);
                var next = MapPoolLibraryRules.Delete(library, c.PoolId, out _);
                return next with { Revision = revision };
            }
            case MapPoolLibraryAction.SaveMaps:
            {
                var pool = Require(library, c.PoolId);
                var maps = c.Maps ?? throw new InstanceException("HCSL-MAPPOOL-INVALID", "Maps are required.");
                MapPoolRules.Validate(new(1, 0, maps));
                return library with
                {
                    Revision = revision,
                    Pools = library.Pools.Select(p => p.Id == pool.Id ? pool with { Maps = maps } : p).ToArray()
                };
            }
            case MapPoolLibraryAction.AssignPreset:
            {
                if (c.PresetId == Guid.Empty) throw new InstanceException("HCSL-MAPPOOL-PRESET", "Preset id required.");
                var assignments = new Dictionary<Guid, Guid>(library.Assignments);
                if (c.PoolId == Guid.Empty) assignments.Remove(c.PresetId);
                else
                {
                    Require(library, c.PoolId);
                    assignments[c.PresetId] = c.PoolId;
                }
                return library with { Revision = revision, Assignments = assignments };
            }
            default:
                throw new InstanceException("HCSL-MAPPOOL-REQUEST", "Unsupported pool action.");
        }
    }

    private static MapPoolRecord Require(MapPoolLibrary library, Guid poolId) =>
        library.Pools.FirstOrDefault(p => p.Id == poolId)
        ?? throw new InstanceException("HCSL-MAPPOOL-POOL", "That pool no longer exists; reload.");

    private static void Write(string file, byte[] bytes, MapPoolLibraryResult current)
    {
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            Safe(file);
            if (File.Exists(file)) File.Move(file, file + ".bak", overwrite: true);
            File.Move(temp, file);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Reads the library, or seeds one from the legacy per-preset pools on first use.</summary>
    private static MapPoolLibraryResult Read(string file, string instanceDirectory, PresetDefinition[] presets)
    {
        Safe(file);
        if (File.Exists(file))
        {
            if (!File.Exists(file) || new FileInfo(file).Length > MaxBytes)
                throw new InstanceException("HCSL-MAPPOOL-LIMIT", "map-pools.json is not a regular file or exceeds 256 KiB.");
            var bytes = File.ReadAllBytes(file);
            var library = JsonSerializer.Deserialize(bytes, Json)
                ?? throw new InstanceException("HCSL-MAPPOOL-INVALID", "Missing pool library.");
            MapPoolLibraryRules.Validate(library);
            return new(library, Hash(bytes));
        }
        // First read: give every preset that already has maps its own pool, so nothing disappears.
        var pools = new List<MapPoolRecord>();
        var assignments = new Dictionary<Guid, Guid>();
        var index = 0;
        foreach (var preset in presets)
        {
            var legacy = Path.Combine(instanceDirectory, "presets", preset.Id.ToString("D"), "maps.json");
            if (!File.Exists(legacy)) continue;
            var loaded = LocalPresetMapPoolStore.ReadForDeployment(legacy);
            if (loaded.Maps.Length == 0) continue;
            index++;
            // List and the first write independently seed the same legacy pools.
            var pool = new MapPoolRecord(preset.Id, "图池 " + index, loaded.Maps);
            pools.Add(pool);
            assignments[preset.Id] = pool.Id;
        }
        var seeded = new MapPoolLibrary(1, 0, pools.ToArray(), assignments);
        MapPoolLibraryRules.Validate(seeded);
        return new(seeded, null);
    }

    private static void Safe(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent) LocalPaths.RejectLink(item.FullName);
    }
}
