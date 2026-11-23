namespace HCSL.Core;

/// <summary>One named map pool of an instance; presets reference it instead of carrying their own copy.</summary>
public sealed record MapPoolRecord(Guid Id, string Name, ServerMapTarget[] Maps, WorkshopItem[]? CollectionMembers = null);

/// <summary>
/// Instance-level pool library. Pools are shared by every preset of one instance; the assignment map says which pool a
/// preset uses. Kept apart from the legacy per-preset <c>maps.json</c> so old instances still read before migrating.
/// </summary>
public sealed record MapPoolLibrary(int FormatVersion, long Revision, MapPoolRecord[] Pools, Dictionary<Guid, Guid> Assignments)
{
    public MapPoolRecord? PoolOf(Guid presetId) =>
        Assignments.TryGetValue(presetId, out var poolId) ? Pools.FirstOrDefault(p => p.Id == poolId) : null;
}

public enum MapPoolLibraryAction { List, Create, Rename, Delete, SaveMaps, AssignPreset }

public sealed record MapPoolLibraryCommand(MapPoolLibraryAction Action, Guid InstanceId, long InstanceRevision = 0,
    Guid PoolId = default, Guid PresetId = default, string? Name = null, ServerMapTarget[]? Maps = null,
    long ExpectedRevision = 0, string? ExpectedHash = null, WorkshopItem[]? CollectionMembers = null);

public sealed record MapPoolLibraryResult(MapPoolLibrary Library, string? Hash);

public static class MapPoolLibraryRules
{
    public const int MaxPools = 32;
    public const int MaxNameLength = 40;

    public static void Validate(MapPoolLibrary library)
    {
        if (library.FormatVersion != 1 || library.Revision < 0 || library.Pools is null || library.Assignments is null
            || library.Pools.Length > MaxPools)
            throw new InstanceException("HCSL-MAPPOOL-LIBRARY", "Unsupported pool library format or more than 32 pools.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in library.Pools)
        {
            if (pool is null || pool.Id == Guid.Empty || !ids.Add(pool.Id))
                throw new InstanceException("HCSL-MAPPOOL-LIBRARY", "Pools need unique ids.");
            if (!Names.Valid(pool.Name) || !names.Add(pool.Name.Trim()))
                throw new InstanceException("HCSL-MAPPOOL-NAME", "Pool names must be 1-" + MaxNameLength + " characters and unique in the instance.");
            MapPoolRules.Validate(new(1, 0, pool.Maps ?? []));
            if (pool.CollectionMembers is { } members &&
                (pool.Maps is not [{ Kind: ServerMapKind.WorkshopCollection }] || members.Length > 100 ||
                 members.Any(m => m is null || m.Children is not null || !ServerMapRules.TryValidateWorkshopId(m.Id) ||
                     m.Title is not { Length: > 0 and <= 200 } || m.Title.Any(char.IsControl))))
                throw new InstanceException("HCSL-MAPPOOL-COLLECTION", "合集成员快照无效。");
        }
        foreach (var pair in library.Assignments)
        {
            if (!ids.Contains(pair.Value))
                throw new InstanceException("HCSL-MAPPOOL-LIBRARY", "A preset points at a pool that does not exist.");
        }
    }

    /// <summary>Removes a pool and clears every preset assignment that pointed at it (delete unbinds, never blocks).</summary>
    public static MapPoolLibrary Delete(MapPoolLibrary library, Guid poolId, out Guid[] unbound)
    {
        unbound = library.Assignments.Where(a => a.Value == poolId).Select(a => a.Key).ToArray();
        var pools = library.Pools.Where(p => p.Id != poolId).ToArray();
        var assignments = library.Assignments.Where(a => a.Value != poolId).ToDictionary(a => a.Key, a => a.Value);
        return library with { Pools = pools, Assignments = assignments };
    }

    public static class Names
    {
        public static bool Valid(string? name) => !string.IsNullOrWhiteSpace(name)
            && name.Trim().Length is >= 1 and <= MaxNameLength && !name.Trim().Any(char.IsControl);
    }
}
