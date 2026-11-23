using System.Text.Json;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

public static class LocalWorkshopCatalog
{
    private static readonly object Gate = new();

    public static WorkshopItem[] Read(string instanceDirectory, WorkshopItem[]? add)
    {
        lock (Gate)
        {
            if (!Directory.Exists(instanceDirectory))
            {
                if (add is null) return [];
                throw new InstanceException("HCSL-WORKSHOP-INSTANCE", "实例不存在。");
            }
            var path = Path.Combine(instanceDirectory, "workshop-maps.json");
            var existing = File.Exists(path) ? JsonSerializer.Deserialize<WorkshopItem[]>(File.ReadAllBytes(path)) ?? [] : [];
            if (add is null) return existing;
            if (add.Length > 100 || add.Any(x => x is null || !ServerMapRules.TryValidateWorkshopId(x.Id) ||
                x.Children is not null || x.Title is not { Length: > 0 and <= 200 } || x.Title.Any(char.IsControl)))
                throw new InstanceException("HCSL-WORKSHOP-INPUT", "工坊地图资料无效。");
            var next = existing.Concat(add).DistinctBy(x => x.Id).ToArray();
            if (next.Length == existing.Length) return existing;
            if (next.Length > 100) throw new InstanceException("HCSL-WORKSHOP-LIMIT", "每个实例最多登记 100 张工坊地图。");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(next));
                File.Move(temp, path, overwrite: true);
            }
            finally { File.Delete(temp); }
            return next;
        }
    }
}
