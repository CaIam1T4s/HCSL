using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Map changes reuse the same run-bound trusted console IPC; no shell or optimistic readiness.</summary>
public sealed class ServerMapClient(ServerController server)
{
    public async Task<ServerConsoleReceipt?> ChangeAsync(Guid instanceId, Guid runId, ServerMapTarget target)
    {
        var command = ServerMapRules.ChangeCommand(target);
        if (!server.CanSendConsole || server.Busy || server.Snapshot is not { Active: true, State: ServerState.Running } current
            || current.RunId != runId || current.InstanceId != instanceId)
            throw new IOException("HCSL-MAP-STALE: 运行实例/RunId 或状态已变化，请重新确认。");
        return await server.SendConsoleAsync(runId, command);
    }
    public static string ProgressText(ServerMapProgress? progress, bool english = false) => progress?.Phase switch
    {
        ServerMapPhase.DownloadObserved => english ? "Workshop download log observed; not ready" : "观测到工坊下载日志；新地图尚未确认就绪",
        ServerMapPhase.DownloadFailed => english ? "Workshop download failure log observed; inspect logs" : "观测到工坊下载失败日志；请查看日志，不代表密码/认证故障",
        ServerMapPhase.Unconfirmed => english ? "Map/download unconfirmed after 120s; no automatic retry" : "地图/下载已等待 120 秒仍未确认；不自动重试，请查看日志",
        _ => ""
    };
    public static string ReceiptText(ServerConsoleReceipt? receipt, bool english = false) => receipt switch
    {
        null => english ? "Not submitted: input channel busy or run changed. Confirm again." : "换图命令未提交：输入通道忙或运行已变化，请重新确认。",
        { Delivery: ConsoleDelivery.Written } => english ? "Command written; download/loading success is not confirmed. Inspect the actual map and logs." : "换图命令已写入；不代表下载/加载成功，请观察实际地图与日志。",
        { Delivery: ConsoleDelivery.Rejected } => (english ? "Rejected, not sent: " : "已拒绝，未发送：") + receipt.Code,
        _ => (english ? "Delivery unknown, no automatic retry: " : "投递结果未知，不自动重发：") + receipt.Code
    };
}
