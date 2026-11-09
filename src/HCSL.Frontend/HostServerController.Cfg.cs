using HCSL.Core;
namespace HCSL.Frontend;

public sealed partial class HostServerController
{
    private PresetCfgClient? cfg;
    private PresetCfgClient Cfg => cfg ??= new(instances, Server);
    public async Task<PresetCfgResult> CfgAsync(PresetCfgCommand command)
    {
        if (Busy || !Server.Connected) throw new IOException("HCSL-CFG-BUSY: Host is disconnected or another operation is active.");
        var write = command.Action is not (PresetCfgAction.List or PresetCfgAction.Read);
        if (write && !CanCreate) throw new IOException("HCSL-CFG-BUSY: Stop the server and complete recovery before saving CFG.");
        if (!Instances.Any(i => i.Entry.Id == command.InstanceId && i.Entry.Presets.Any(p => p.Id == command.PresetId)))
            throw new IOException("HCSL-CFG-PRESET: Refresh the instance catalog.");
        Busy = true; Notify();
        try { return await Cfg.ExecuteAsync(command); }
        finally { Busy = false; Notify(); }
    }
    public bool CanExecCfg(Guid instanceId, Guid presetId) => !Busy && Cfg.CanExec(instanceId, presetId);

    public async Task<string> ExecCfgAsync(Guid instanceId, Guid presetId, Guid expectedRun, string name, string expectedHash)
    {
        if (!CanExecCfg(instanceId, presetId)) throw new IOException("HCSL-CFG-RUN: Running target does not match.");
        Busy = true; Notify();
        try { return await Cfg.ExecAsync(instanceId, presetId, expectedRun, name, expectedHash); }
        finally { Busy = false; Notify(); }
    }
}
