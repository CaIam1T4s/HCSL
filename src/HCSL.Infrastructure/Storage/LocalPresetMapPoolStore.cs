using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HCSL.Application.Abstractions;
using HCSL.Core;
namespace HCSL.Infrastructure.Storage;

public sealed class LocalPresetMapPoolStore : IPresetMapPoolStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public MapPoolResult Execute(WorkspaceRequest workspace, MapPoolCommand command)
    {
        if (!Enum.IsDefined(command.Action) || command.InstanceId == Guid.Empty || command.PresetId == Guid.Empty)
            throw new InstanceException("HCSL-MAPPOOL-REQUEST", "Stable instance/preset IDs and action required.");
        try
        {
            if (command.Action == MapPoolAction.Read)
            {
                var paths = LocalWorkspaceManager.ConfiguredOrDefault(workspace);
                LocalPaths.ValidateRoots(paths.SettingsRoot, paths.DataRoot, paths.LibraryRoot);
                return ExecuteRoot(Path.Combine(paths.DataRoot, "instances"), command);
            }
            using var lease = new LocalWorkspaceManager().Acquire(workspace);
            if (File.Exists(Path.Combine(lease.Layout.LibraryRoot, ".hcsl", "server-session.json")))
                throw new InstanceException("HCSL-SERVER-RECOVERY", "Recover the previous server session before editing maps.");
            return ExecuteRoot(lease.Layout.InstancesRoot, command);
        }
        catch (InstanceException) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
        { throw new InstanceException("HCSL-MAPPOOL-IO", "Cannot read/write maps.json; check format, size, links and permissions. Nothing was overwritten on a validation conflict."); }
    }
    private static void Safe(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent) LocalPaths.RejectLink(item.FullName);
    }
    private static MapPoolResult ExecuteRoot(string root, MapPoolCommand c)
    {
        Safe(root);
        var directory = Path.Combine(root, c.InstanceId.ToString("D")); Safe(directory);
        var entry = LocalInstanceStore.Load(directory, c.InstanceId);
        if (entry.Definition is null || entry.Presets.All(p => p.Id != c.PresetId))
            throw new InstanceException("HCSL-MAPPOOL-PRESET", "Instance or preset missing; refresh catalog.");
        var file = Path.Combine(directory, "presets", c.PresetId.ToString("D"), "maps.json"); Safe(file);
        var current = Read(file);
        if (c.Action == MapPoolAction.Read) return current;
        if (entry.Definition.Revision != c.InstanceRevision) throw new InstanceException("HCSL-INSTANCE-STALE", "Instance revision changed; refresh catalog.");
        CheckVersion(current, c);
        var next = new PresetMapPool(1, checked(current.Pool.Revision + 1), c.Maps ?? throw new InstanceException("HCSL-MAPPOOL-INVALID", "Maps are required."));
        MapPoolRules.Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MapPoolRules.MaxBytes) throw new InstanceException("HCSL-MAPPOOL-LIMIT", "Map pool exceeds 32 KiB.");
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = LocalPaths.CreateFile(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            CheckVersion(Read(file), c); Safe(file);
            File.Move(temp, file, overwrite: current.Hash is not null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(next, Hash(bytes));
    }
    private static void CheckVersion(MapPoolResult current, MapPoolCommand c)
    {
        if (current.Pool.Revision != c.ExpectedRevision || !string.Equals(current.Hash, c.ExpectedHash, StringComparison.Ordinal))
            throw new InstanceException("HCSL-MAPPOOL-CONFLICT", "maps.json changed. Reload before saving; draft was not saved.");
    }
    internal static void ValidateForCopy(string file) => _ = Read(file);
    internal static PresetMapPool ReadForDeployment(string file) => Read(file).Pool;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static MapPoolResult Read(string file)
    {
        Safe(file);
        if (!File.Exists(file))
        {
            if (Path.Exists(file)) throw new InstanceException("HCSL-MAPPOOL-IO", "maps.json is not a regular file.");
            return new(new(1, 0, []), null);
        }
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (output.Length + count > MapPoolRules.MaxBytes) throw new InstanceException("HCSL-MAPPOOL-LIMIT", "maps.json exceeds 32 KiB.");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        var pool = JsonSerializer.Deserialize<PresetMapPool>(bytes, Json) ?? throw new InstanceException("HCSL-MAPPOOL-INVALID", "Missing map pool.");
        MapPoolRules.Validate(pool); return new(pool, Hash(bytes));
    }
}
