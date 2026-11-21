using HCSL.Core;

namespace HCSL.Infrastructure.Server;

/// <summary>One map pack or map file the deployment produced, described without exposing its absolute path.</summary>
public sealed record MapFile(string RelativePath, long SizeBytes, DateTimeOffset? ModifiedAt);

/// <summary>
/// Builds the map catalog from a deployed game directory: CS2 ships maps as `.vpk` packs (`maps/*.vpk`), custom maps
/// may be loose `.bsp`, and Workshop items live in `maps/workshop/&lt;fileid&gt;/**`. Pure on the file list, so the rules
/// are testable without a real deployment.
/// </summary>
public static class MapCatalogScanner
{
    private const string WorkshopRoot = "maps/workshop/";

    /// <summary>A map pack or a loose map; everything else in the folder is a sidecar, not a map.</summary>
    private static bool IsMapFile(string path) =>
        path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase);

    public static MapCatalogResult Build(IEnumerable<MapFile> files, int maxEntries = MapCatalogRules.MaxEntries)
    {
        var entries = new Dictionary<string, MapCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A Workshop folder exists before its download finishes, so remember every folder and mark the ones with no map.
        foreach (var file in files)
        {
            var path = file.RelativePath.Replace('\\', '/');
            if (path.StartsWith(WorkshopRoot, StringComparison.OrdinalIgnoreCase))
            {
                var parts = path[WorkshopRoot.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var id = parts[0];
                if (!ServerMapRules.TryValidateWorkshopId(id)) continue;
                // The folder counts even when it holds no map yet: that is exactly "downloading or failed".
                folders.Add(id);
                if (!IsMapFile(path) || parts.Length < 2) continue;
                var key = "workshop:" + id;
                if (entries.TryGetValue(key, out var known) && !known.Incomplete) continue;
                entries[key] = new MapCatalogEntry(Name(id), MapCatalogSource.Workshop, id, file.SizeBytes, file.ModifiedAt);
                continue;
            }
            if (!IsMapFile(path)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!ServerMapRules.TryValidateStandard(name)) continue;
            // "*_vanity" packs are menu/homepage artwork for a map, not something a server can load.
            if (name.EndsWith("_vanity", StringComparison.OrdinalIgnoreCase)) continue;
            // CS2 bundles "workshop_preview_*" packs: they are official previews of Workshop maps, not the download.
            var source = name.StartsWith("workshop_preview_", StringComparison.OrdinalIgnoreCase)
                ? MapCatalogSource.Preview : MapCatalogSource.Standard;
            entries["standard:" + name] = new MapCatalogEntry(name, source, null, file.SizeBytes, file.ModifiedAt);
        }
        // Folders that exist but hold no map are shown as incomplete instead of disappearing without a trace.
        foreach (var id in folders)
        {
            var key = "workshop:" + id;
            if (entries.TryGetValue(key, out var found) && !found.Incomplete) continue;
            entries[key] = new MapCatalogEntry(Name(id), MapCatalogSource.Workshop, id, Incomplete: true);
        }
        var ordered = entries.Values
            .OrderBy(e => e.Source)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxEntries))
            .ToArray();
        var detail = folders.Count == 0 ? "no Workshop folders found" : folders.Count + " Workshop folders";
        return new(ordered, detail);
    }

    /// <summary>Scans a deployed game directory; a missing directory is an empty catalog, not an error.</summary>
    public static MapCatalogResult Scan(string gameRoot, int maxEntries = MapCatalogRules.MaxEntries)
    {
        var files = new List<MapFile>();
        var maps = Path.Combine(gameRoot, "csgo", "maps");
        try
        {
            if (Directory.Exists(maps))
            {
                foreach (var file in Directory.EnumerateFiles(maps, "*", SearchOption.TopDirectoryOnly).Where(IsMapFile))
                    files.Add(Describe(gameRoot, file));
                var workshop = Path.Combine(maps, "workshop");
                if (Directory.Exists(workshop))
                {
                    foreach (var item in Directory.EnumerateDirectories(workshop))
                    {
                        var id = Path.GetFileName(item);
                        var hasMap = false;
                        foreach (var file in Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories).Where(IsMapFile))
                        {
                            hasMap = true;
                            files.Add(Describe(gameRoot, file));
                            break; // one map per Workshop item is enough to know it is usable
                        }
                        if (!hasMap) files.Add(new MapFile("maps/workshop/" + id + "/", 0, null));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new([], "catalog scan failed: " + e.GetType().Name);
        }
        return Build(files, maxEntries);
    }

    private static MapFile Describe(string gameRoot, string file)
    {
        var info = new FileInfo(file);
        var relative = Path.GetRelativePath(gameRoot, file).Replace('\\', '/');
        return new(relative, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : null);
    }

    private static string Name(string workshopId) => "Workshop #" + workshopId;
}
