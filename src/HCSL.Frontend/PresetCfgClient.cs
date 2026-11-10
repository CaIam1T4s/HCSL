using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Framework-independent CFG operations shared by the desktop and terminal host.</summary>
public sealed class PresetCfgClient(InstanceClient instances, ServerController server)
{
    public bool CanWrite => PresetEditGate.CanWrite(server);

    public Task<PresetCfgResult> ExecuteAsync(PresetCfgCommand command)
    {
        if (!server.Connected) throw new IOException("HCSL-CFG-BUSY: Host is disconnected.");
        if (command.Action is not (PresetCfgAction.List or PresetCfgAction.Read) && !CanWrite)
            throw new IOException("HCSL-CFG-BUSY: Stop the server and complete recovery before editing CFG.");
        return instances.CfgAsync(command);
    }
    public bool CanExec(Guid instanceId, Guid presetId) => server.CanSendConsole && !server.Busy &&
        server.State == ServerState.Running && server.Active && server.Snapshot?.InstanceId == instanceId &&
        server.Snapshot.Preset?.Id == presetId && server.RunId is not null;

    public async Task<string> ExecAsync(Guid instanceId, Guid presetId, Guid expectedRun, string name, string expectedHash)
    {
        name = PresetCfgRules.Path(name, requireCfgFile: true);
        void CheckRun()
        {
            if (!CanExec(instanceId, presetId) || server.RunId != expectedRun)
                throw new IOException("HCSL-CFG-RUN: Running instance/preset/RunId does not match this editor.");
        }
        CheckRun();
        var saved = await ExecuteAsync(new(PresetCfgAction.Read, instanceId, presetId, Name: name));
        if (saved.Hash != expectedHash) throw new IOException("HCSL-CFG-CONFLICT: File changed; reload before executing.");
        CheckRun();
        var receipt = await server.SendConsoleAsync(expectedRun, "exec " + name);
        return receipt is null ? "未发送 / Not sent: run changed or console busy." : receipt.Code + ": " + (receipt.Delivery switch
        {
            ConsoleDelivery.Written => "已写入输入通道，不代表执行成功 / Written; execution not confirmed.",
            ConsoleDelivery.Rejected => "已拒绝，未发送 / Rejected; not sent.",
            _ => "投递结果未知；未自动重发 / Delivery unknown; not retried."
        });
    }
}
