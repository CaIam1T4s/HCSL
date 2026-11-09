using HCSL.Core;
namespace HCSL.Frontend;
public sealed class PresetMapPoolClient(InstanceClient instances, ServerController server)
{
    public bool CanWrite => server.Connected && !server.Busy && !server.Active && server.State != ServerState.RecoveryRequired;
    public Task<MapPoolResult> ExecuteAsync(MapPoolCommand command)
    {
        if (!server.Connected || server.Busy || (command.Action != MapPoolAction.Read &&
            (server.Active || server.State == ServerState.RecoveryRequired)))
            throw new IOException("HCSL-MAPPOOL-BUSY: Stop the server and finish recovery before editing maps.");
        return instances.MapPoolAsync(command);
    }
}

/// <summary>Shared ordered draft: edits are local until explicit optimistic save.</summary>
public sealed class MapPoolDraft
{
    private readonly List<ServerMapTarget> maps = [];
    public IReadOnlyList<ServerMapTarget> Maps => maps;
    public MapPoolResult? Loaded { get; private set; }
    public bool Dirty => Loaded is null ? maps.Count != 0 : !maps.SequenceEqual(Loaded.Pool.Maps);
    public void Load(MapPoolResult result) { MapPoolRules.Validate(result.Pool); Loaded = result; maps.Clear(); maps.AddRange(result.Pool.Maps); }
    public void Add(ServerMapTarget map)
    {
        var next = maps.Append(map).ToArray(); MapPoolRules.Validate(new(1, 0, next)); maps.Add(map);
    }
    public void Remove(int index) { if (index >= 0 && index < maps.Count) maps.RemoveAt(index); }
    public int Move(int index, int direction)
    {
        if (index < 0 || index >= maps.Count || direction is not (-1 or 1)) return index;
        var next = index + direction;
        if (next < 0 || next >= maps.Count) return index;
        (maps[index], maps[next]) = (maps[next], maps[index]); return next;
    }
    public MapPoolCommand SaveCommand(Guid instanceId, Guid presetId, long instanceRevision)
    {
        if (Loaded is null) throw new IOException("HCSL-MAPPOOL-NOT-LOADED: Reload before saving.");
        return new(MapPoolAction.Save, instanceId, presetId, instanceRevision, Loaded.Pool.Revision, Loaded.Hash, maps.ToArray());
    }
}
