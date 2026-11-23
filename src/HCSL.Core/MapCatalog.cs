namespace HCSL.Core;

/// <summary>Where a catalog entry comes from: a deployed map pack, an official Workshop preview, or a Workshop item.</summary>
public enum MapCatalogSource { Standard, Workshop, Preview }

/// <summary>
/// One map the launcher can offer. The catalog is app-level (shared by every instance), while "downloaded" is a fact
/// of one instance runtime, so it is reported per query rather than stored here.
/// </summary>
public sealed record MapCatalogEntry(string Name, MapCatalogSource Source, string? WorkshopId,
    long SizeBytes = 0, DateTimeOffset? ModifiedAt = null, bool Incomplete = false, string? InternalName = null)
{
    /// <summary>Stable identity of an entry inside one catalog: the kind plus the map name or Workshop id.</summary>
    public string Key => Source == MapCatalogSource.Workshop ? "workshop:" + WorkshopId : "standard:" + Name;
}

public sealed record MapCatalogResult(MapCatalogEntry[] Entries, string? Detail = null, WorkshopItem? Workshop = null);

/// <summary>Which instance runtime to read; the catalog itself is app-level, its download state is not.</summary>
public sealed record MapCatalogCommand(Guid InstanceId, string? WorkshopId = null, WorkshopItem[]? Register = null);

public static class MapCatalogRules
{
    public const int MaxEntries = 5000;

    public static void Validate(MapCatalogResult result)
    {
        if (result.Entries is null || result.Entries.Length > MaxEntries)
            throw new InstanceException("HCSL-MAPCATALOG-INVALID", "The map catalog is missing or larger than 5000 entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in result.Entries)
        {
            if (entry is null) throw new InstanceException("HCSL-MAPCATALOG-INVALID", "A catalog entry is missing.");
            if (entry.Source == MapCatalogSource.Workshop)
            {
                if (string.IsNullOrEmpty(entry.WorkshopId) || !ServerMapRules.TryValidateWorkshopId(entry.WorkshopId))
                    throw new InstanceException("HCSL-MAPCATALOG-INVALID", "A Workshop entry needs a positive numeric id.");
            }
            else if (!ServerMapRules.TryValidateStandard(entry.Name))
                throw new InstanceException("HCSL-MAPCATALOG-INVALID", "A built-in entry needs a plain map name.");
            if (!seen.Add(entry.Key))
                throw new InstanceException("HCSL-MAPCATALOG-DUPLICATE", "Duplicate catalog entry: " + entry.Key + ".");
        }
    }

    /// <summary>How many pools (by name) contain this entry; drives the "所属图池" stack.</summary>
    public static int MembershipCount(string key, IEnumerable<(string Pool, IReadOnlyList<ServerMapTarget> Maps)> pools) =>
        pools.Count(pool => pool.Maps.Any(map => KeyOf(map) == key));

    public static string KeyOf(ServerMapTarget target) => target.Kind == ServerMapKind.Standard
        ? "standard:" + target.Value
        : "workshop:" + target.Value;
}
