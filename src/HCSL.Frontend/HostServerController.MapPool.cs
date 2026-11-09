using HCSL.Core;
namespace HCSL.Frontend;
public sealed partial class HostServerController
{
    public async Task<MapPoolResult> MapPoolAsync(MapPoolCommand command)
    {
        if (Busy || !Server.Connected || (command.Action != MapPoolAction.Read && !CanCreate))
            throw new IOException("HCSL-MAPPOOL-BUSY: Stop server/SteamCMD and finish recovery before saving maps.");
        if (!Instances.Any(i => i.Entry.Id == command.InstanceId && i.Entry.Presets.Any(p => p.Id == command.PresetId)))
            throw new IOException("HCSL-MAPPOOL-PRESET: Refresh catalog.");
        Busy = true; Notify();
        try { return await new PresetMapPoolClient(instances, Server).ExecuteAsync(command); }
        finally { Busy = false; Notify(); }
    }
}
