namespace HCSL.Core;

public enum MapPoolAction { Read, Save }
public sealed record PresetMapPool(int FormatVersion, long Revision, ServerMapTarget[] Maps);
public sealed record MapPoolCommand(MapPoolAction Action, Guid InstanceId, Guid PresetId, long InstanceRevision = 0,
    long ExpectedRevision = 0, string? ExpectedHash = null, ServerMapTarget[]? Maps = null);
public sealed record MapPoolResult(PresetMapPool Pool, string? Hash);
public static class MapPoolRules
{
    public const int MaxMaps = 128;
    public const int MaxBytes = 32768;
    public static void Validate(PresetMapPool pool)
    {
        if (pool.FormatVersion != 1 || pool.Revision < 0 || pool.Maps is null || pool.Maps.Length > MaxMaps)
            throw new InstanceException("HCSL-MAPPOOL-INVALID", "Unsupported map pool format/revision or more than 128 maps.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in pool.Maps)
        {
            if (map is null) throw new InstanceException("HCSL-MAPPOOL-INVALID", "Map target is missing.");
            ServerMapRules.Validate(map);
            if (!seen.Add($"{map.Kind}:{map.Value}")) throw new InstanceException("HCSL-MAPPOOL-DUPLICATE", "Duplicate map target.");
        }
        var collections = pool.Maps.Count(m => m.Kind == ServerMapKind.WorkshopCollection);
        if (collections != 0 && (collections != 1 || pool.Maps.Length != 1))
            throw new InstanceException("HCSL-MAPPOOL-COLLECTION", "A workshop collection must be the only entry in the map pool; mixed collections are not supported.");
    }
    /// <summary>The collection that defines a native workshop rotation, or null for ordinary pool entries.</summary>
    public static string? CollectionId(PresetMapPool pool)
    {
        Validate(pool);
        return pool.Maps.Length == 1 && pool.Maps[0].Kind == ServerMapKind.WorkshopCollection ? pool.Maps[0].Value : null;
    }
    public static ServerMapTarget Parse(string value)
    {
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 2) throw new InstanceException("HCSL-MAPPOOL-INPUT", "Use: standard de_dust2 / workshop 12345 / collection 12345.");
        var kind = fields[0].ToLowerInvariant() switch { "standard" => ServerMapKind.Standard, "workshop" => ServerMapKind.Workshop, "collection" => ServerMapKind.WorkshopCollection, _ => (ServerMapKind)(-1) };
        var target = new ServerMapTarget(kind, fields[1]); ServerMapRules.Validate(target); return target;
    }
}

/// <summary>Ordered native rotation was observed, but not every build/mode/source combination is verified.</summary>
public static class NativeMapRotation
{
    public const bool Verified = false;
    public static string Status(bool english = false) => english
        ? "An all-standard pool is deployed as a launcher-owned CS2 map group and has rotated in order. A single workshop collection starts from a built-in seed map, then supplies its own native workshop order; only an actual workshop map load is published as ready. Keep end-match voting enabled so nextlevel is written; an empty target is detected and recovered once. Mixed pools stay manual, and the actual next map is known only from logs."
        : "全普通地图池会随部署生成启动器受管的 CS2 地图组，已实测可按顺序轮换。只含一个工坊合集的图池会先用内置种子地图启动，再由合集提供原生顺序；只有真实工坊地图加载才算就绪。请保持结束投票开启以写入 nextlevel；空目标会被检测并尝试一次恢复。混排仍只手工换图，实际下一张只以日志为准。";
    public static void RequireVerified() => throw new InstanceException("HCSL-ROTATION-UNVERIFIED", "Not every build, game mode and map-source combination has been verified; do not claim universal native rotation support.");
}
