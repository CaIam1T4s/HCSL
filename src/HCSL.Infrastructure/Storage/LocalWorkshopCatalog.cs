using System.Text.Json;
using HCSL.Core;

namespace HCSL.Infrastructure.Storage;

public static class LocalWorkshopCatalog
{
    private static readonly object Gate = new();

    public static void Remove(HCSL.Application.Abstractions.WorkspaceRequest workspace, Guid instanceId, string[] ids)
    {
        if (ids.Length is < 1 or > 100 || ids.Any(id => !ServerMapRules.TryValidateWorkshopId(id)))
            throw new InstanceException("HCSL-WORKSHOP-INPUT", "工坊地图 ID 无效。");
        using var lease = new LocalWorkspaceManager().Acquire(workspace);
        if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
            throw new InstanceException("HCSL-SERVER-RECOVERY", "请先停止服务器或恢复上一次会话。");
        var library = new LocalMapPoolLibraryStore().Execute(workspace, new(MapPoolLibraryAction.List, instanceId)).Library;
        if (library.Pools.Any(p => p.Maps.Any(m => m.Kind == ServerMapKind.Workshop && ids.Contains(m.Value)) ||
            p.CollectionMembers?.Any(m => ids.Contains(m.Id)) == true))
            throw new InstanceException("HCSL-WORKSHOP-REFERENCED", "请先解除地图的图池或合集引用。");
        var instance = Path.Combine(lease.Layout.InstancesRoot, instanceId.ToString("D"));
        var path = Path.Combine(instance, "workshop-maps.json");
        var root = Path.Combine(lease.Layout.LibraryRoot, "runtime", instanceId.ToString("D"), "game", "csgo", "maps", "workshop");
        var targets = ids.Distinct().Select(id => Path.Combine(root, id)).ToArray();
        foreach (var target in targets.Append(path))
            for (var part = target; part is not null; part = Path.GetDirectoryName(part))
                if (new FileInfo(part).LinkTarget is not null || new DirectoryInfo(part).LinkTarget is not null)
                    throw new InstanceException("HCSL-WORKSHOP-PATH", "工坊地图路径包含链接，未移除。");
        lock (Gate)
        {
            var existing = Read(instance, null);
            foreach (var target in targets)
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            var next = existing.Where(x => !ids.Contains(x.Id)).ToArray();
            if (next.Length != existing.Length) Write(path, next);
        }
    }

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
            var existing = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), WorkshopStorageJsonContext.Default.WorkshopItemArray) ?? [] : [];
            if (add is null) return existing;
            if (add.Length > 100 || add.Any(x => x is null || !ServerMapRules.TryValidateWorkshopId(x.Id) ||
                x.Children is not null || x.Title is not { Length: > 0 and <= 200 } || x.Title.Any(char.IsControl)))
                throw new InstanceException("HCSL-WORKSHOP-INPUT", "工坊地图资料无效。");
            var next = existing.Concat(add).DistinctBy(x => x.Id).ToArray();
            if (next.Length == existing.Length) return existing;
            if (next.Length > 100) throw new InstanceException("HCSL-WORKSHOP-LIMIT", "每个实例最多登记 100 张工坊地图。");
            Write(path, next);
            return next;
        }
    }

    private static void Write(string path, WorkshopItem[] items)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(items, WorkshopStorageJsonContext.Default.WorkshopItemArray));
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
