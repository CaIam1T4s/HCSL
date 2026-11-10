using HCSL.Core;
namespace HCSL.Frontend;

public sealed partial class HostServerController
{
    private PresetPluginClient? plugins;
    private PresetPluginClient Plugins => plugins ??= new(instances, Server);

    /// <summary>Editing is allowed only where the framework-free client allows it; no duplicated gate.</summary>
    public bool CanEditPlugins => !Busy && Plugins.CanWrite;

    /// <summary>Read-only probe used by the import form; never writes the library.</summary>
    public async Task<PluginImportPreview> ProbePluginImportAsync(string sourcePath)
    {
        if (Busy || !Server.Connected) throw new IOException("HCSL-PLUGIN-BUSY: Host is disconnected or another operation is active.");
        Busy = true; Notify();
        try { return await instances.PluginImportProbeAsync(new(sourcePath)); }
        finally { Busy = false; Notify(); }
    }

    public async Task<PluginImportResult> ImportPluginAsync(PluginImportCommand command)
    {
        if (Busy || !Server.Connected) throw new IOException("HCSL-PLUGIN-BUSY: Host is disconnected or another operation is active.");
        if (command.Enable && !CanEditPlugins) throw new IOException("HCSL-PLUGIN-BUSY: Stop the server and finish recovery before enabling plugins.");
        if (!Instances.Any(i => i.Entry.Id == command.InstanceId && i.Entry.Presets.Any(p => p.Id == command.PresetId)))
            throw new IOException("HCSL-PLUGIN-PRESET: Refresh the instance catalog.");
        Busy = true; Notify();
        try { return await Plugins.ImportAsync(command); }
        finally { Busy = false; Notify(); }
    }

    public async Task<PluginResult> PluginsAsync(PluginCommand command)
    {
        if (Busy || !Server.Connected || (command.Action != PluginAction.List && !CanEditPlugins))
            throw new IOException("HCSL-PLUGIN-BUSY: Stop the server/SteamCMD and finish recovery before editing plugins.");
        if (!Instances.Any(i => i.Entry.Id == command.InstanceId && i.Entry.Presets.Any(p => p.Id == command.PresetId)))
            throw new IOException("HCSL-PLUGIN-PRESET: Refresh the instance catalog.");
        Busy = true; Notify();
        try { return await Plugins.ExecuteAsync(command); }
        finally { Busy = false; Notify(); }
    }
}
