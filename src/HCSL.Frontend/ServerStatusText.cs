using System.Text.RegularExpressions;
using HCSL.Core;

namespace HCSL.Frontend;

public enum ServerSeverity { Neutral, Busy, Good, Bad }

/// <summary>Pure formatting of daemon-owned server state. No UI, no clock, no I/O.</summary>
public static class ServerStatusText
{
    private static readonly Regex PhysicsLine = new(@"^\s*Created physics for (\S+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>Actual map reported by the running server, taken from the last physics line in the log.</summary>
    public static string? MapFromLog(IEnumerable<ServerLogLine>? lines)
    {
        if (lines is null) return null;
        string? map = null;
        foreach (var line in lines)
        {
            var match = PhysicsLine.Match(line.Text ?? "");
            if (match.Success) map = match.Groups[1].Value;
        }
        return map;
    }

    public static string StateLabel(ServerState state, bool english) => english ? state switch
    {
        ServerState.Idle => "Idle", ServerState.Preparing => "Preparing", ServerState.Starting => "Starting",
        ServerState.LoadingMap => "Loading map", ServerState.Running => "Running", ServerState.Stopping => "Stopping",
        ServerState.Stopped => "Stopped", ServerState.Failed => "Failed",
        ServerState.RecoveryRequired => "Recovery required", _ => state.ToString()
    } : state switch
    {
        ServerState.Idle => "空闲", ServerState.Preparing => "准备中", ServerState.Starting => "启动中",
        ServerState.LoadingMap => "加载地图", ServerState.Running => "运行中", ServerState.Stopping => "停止中",
        ServerState.Stopped => "已停止", ServerState.Failed => "失败", ServerState.RecoveryRequired => "需要恢复检查",
        _ => state.ToString()
    };

    public static ServerSeverity Severity(ServerState state) => state switch
    {
        ServerState.Running => ServerSeverity.Good,
        ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap or ServerState.Stopping => ServerSeverity.Busy,
        ServerState.Failed or ServerState.RecoveryRequired => ServerSeverity.Bad,
        _ => ServerSeverity.Neutral
    };

    /// <summary>One-line explanation for a daemon code; the code itself is always shown by the caller.</summary>
    public static string? CodeHint(string? code, bool english) => code switch
    {
        "HCSL-SERVER-DEPLOYMENT" => english
            ? "No deployment yet. Run: build/HCSL.DeploymentCheck --instance <UUID> --deploy"
            : "尚未部署该实例。先运行：build/HCSL.DeploymentCheck --instance <UUID> --deploy",
        "HCSL-SERVER-STALE" => english
            ? "Instance or preset changed after deployment. Redeploy before starting."
            : "部署后实例或预设已修改。请重新部署后再启动。",
        "HCSL-SERVER-MEMORY" => english
            ? "Less than 4096 MiB is available; the start was refused."
            : "可用内存不足 4096 MiB，已拒绝启动。",
        "HCSL-SERVER-PLATFORM" => english
            ? "Server lifecycle is Linux-only in this build."
            : "当前构建仅在 Linux 上提供服务器生命周期。",
        "HCSL-SERVER-BUSY" => english
            ? "A server or operation is already in progress."
            : "已有服务器或操作正在进行。",
        "HCSL-SERVER-PORT" => english
            ? "The game port is busy or unusable."
            : "游戏端口已被占用或不可用。",
        "HCSL-SERVER-UNAVAILABLE" or "HCSL-DAEMON-ACCESS" or "HCSL-DAEMON-PLATFORM" => english
            ? "The local daemon is unreachable. Retry connecting."
            : "无法联系本机宿主，请重试连接。",
        "HCSL-SERVER-START-TIMEOUT" => english
            ? "Waited for map readiness for too long; check the log."
            : "等待地图就绪超时，请查看日志。",
        "HCSL-SERVER-STOPPED-FORCED" => english
            ? "Stopped by force; cleanup was confirmed."
            : "已强制停服，并确认清理完成。",
        "HCSL-SERVER-EXITED" or "HCSL-SERVER-EXIT" => english
            ? "The server process exited on its own."
            : "服务器进程自行退出。",
        "HCSL-SERVER-RECOVERY" => english
            ? "The previous run needs a recovery check."
            : "上次运行需要恢复检查。",
        "HCSL-SERVER-OPTIONS" => english
            ? "Launch options are invalid."
            : "启动参数不合法。",
        _ => null
    };

    /// <summary>Second header line: real state, real map, run identity, log size.</summary>
    public static string SummaryLine(bool english, ServerState state, Guid? runId, string? logMap, string? presetMap,
        bool mapLoaded, bool truncated, int lineCount, string? pending, bool historical = false)
    {
        var pendingText = pending switch
        {
            "start" => english ? "start requested" : "启动请求中",
            "stop" => english ? "stop requested" : "停服请求中",
            "recover" => english ? "recovery requested" : "恢复请求中",
            _ => null
        };
        var stateText = pendingText ?? (StateLabel(state, english) + (state is ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap or ServerState.Stopping ? "…" : ""));
        // "ready" is a property of a live run: a stopped or failed run keeps its last map name but must
        // not read as if that map were still serving.
        var live = state is ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap or ServerState.Running or ServerState.Stopping;
        var mapText = logMap is not null
            ? logMap + (historical || !live ? (english ? " (last run)" : "（上次运行）")
                : !mapLoaded ? (english ? " (loading)" : "（加载中）")
                : state == ServerState.Running ? (english ? " (ready)" : "（已就绪）")
                : (english ? " (stopping)" : "（停止中）"))
            : presetMap is not null ? (english ? presetMap + " (preset)" : presetMap + "（预设）")
            : english ? "none" : "未选择";
        var parts = new List<string>
        {
            (english ? "State: " : "状态：") + stateText,
            (english ? "Map: " : "地图：") + mapText
        };
        if (runId is { } id) parts.Add((english ? "Run " : "RunId ") + id.ToString("N")[..8]);
        if (mapLoaded && logMap is null && state == ServerState.Running) parts.Add(english ? "map ready" : "地图已就绪");
        parts.Add((english ? "Log " : "日志 ") + lineCount + (english ? " lines" : " 行"));
        if (truncated) parts.Add(english ? "log truncated" : "日志已截断");
        return string.Join(" · ", parts);
    }

    public static string DetailLine(string? code, string? detail, string? error, bool english)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(code)) parts.Add(code!);
        var hint = CodeHint(code, english);
        if (hint is not null) parts.Add(hint);
        if (!string.IsNullOrWhiteSpace(detail)) parts.Add(detail!);
        if (!string.IsNullOrWhiteSpace(error)) parts.Add(error!);
        return string.Join(" — ", parts);
    }
}
