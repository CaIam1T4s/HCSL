using HCSL.Core;
namespace HCSL.Frontend;
public sealed partial class HostServerController
{
    private PresetMapPoolClient? mapPool;
    private PresetMapPoolClient MapPool => mapPool ??= new(instances, Server);

    /// <summary>Single write gate for map-pool editing; views never rebuild these conditions.</summary>
    public bool CanEditMapPool => !Busy && MapPool.CanWrite;

    public async Task<MapPoolResult> MapPoolAsync(MapPoolCommand command)
    {
        if (Busy || !Server.Connected || (command.Action != MapPoolAction.Read && !CanEditMapPool))
            throw new IOException("HCSL-MAPPOOL-BUSY: Stop server/SteamCMD and finish recovery before saving maps.");
        if (!Instances.Any(i => i.Entry.Id == command.InstanceId && i.Entry.Presets.Any(p => p.Id == command.PresetId)))
            throw new IOException("HCSL-MAPPOOL-PRESET: Refresh catalog.");
        Busy = true; Notify();
        try { return await MapPool.ExecuteAsync(command); }
        finally { Busy = false; Notify(); }
    }

    /// <summary>The map catalog only reads files; it is available while the server runs and while SteamCMD is busy.</summary>
    public async Task<MapCatalogResult> MapCatalogAsync(Guid instanceId)
    {
        if (!Server.Connected) throw new IOException("HCSL-MAPCATALOG-OFFLINE: The daemon is not connected.");
        if (!Instances.Any(i => i.Entry.Id == instanceId))
            throw new IOException("HCSL-MAPCATALOG-INSTANCE: Refresh the instance list.");
        return await instances.MapCatalogAsync(instanceId);
    }
}
