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
            ? "Less than 3072 MiB is available; confirmation cannot bypass this floor."
            : "可用内存不足 3072 MiB，即使确认也禁止启动。",
        "HCSL-SERVER-MEMORY-WARNING" => english
            ? "Available memory is below 4096 MiB. Confirm in the GUI/TUI or pass --allow-low-memory with CLI --start; below 3072 MiB remains blocked."
            : "可用内存不足 4096 MiB。请在 GUI/TUI 确认，或 CLI --start 加 --allow-low-memory；低于 3072 MiB 仍禁止启动。",
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
        "HCSL-SERVER-BIND" => english
            ? "The bind address is not on an active local interface. Pick the address this host has, or switch back to loopback."
            : "绑定地址不在任何活动网卡上。请改成该主机当前的地址，或切回仅本机。",
        "HCSL-INSTANCE-BIND" => english
            ? "Instance bind address is invalid: use an IPv4 literal that belongs to this host."
            : "实例绑定地址不合法：请填写属于本机的 IPv4 地址。",
        _ => null
    };

    /// <summary>Second header line: real state, real map, run identity, log size.</summary>
    public static string ConnectionAddressesLine(bool english, IEnumerable<string>? addresses, int port)
    {
        var values = addresses?.Select(a => a + ":" + port).ToArray() ?? [];
        return (english ? "Local connection addresses: " : "本机连接地址：")
            + (values.Length == 0 ? (english ? "unknown; refresh the instance list" : "未知，请刷新实例列表") : string.Join(" / ", values));
    }

    public static string NetworkLine(ServerNetworkReadiness? value, bool english)
    {
        value ??= new();
        var auth = value.Authentication switch
        {
            SteamAuthenticationState.WaitingForSteam => english ? "waiting for Steam login" : "等待 Steam 登录",
            SteamAuthenticationState.RequestingCertificate => english ? "requesting certificate" : "获取证书中",
            SteamAuthenticationState.Ready => english ? "ready" : "就绪",
            SteamAuthenticationState.Failed => english ? "failed" : "失败",
            _ => english ? "unknown" : "未知"
        };
        var config = value.Configuration switch
        {
            SdrConfigurationState.Retrying => english ? "retrying" : "获取失败，正在重试",
            SdrConfigurationState.Ready => english ? "loaded" : "已加载",
            _ => english ? "unknown" : "未知"
        };
        return english ? $"Steam auth: {auth} · SDR config: {config}" : $"Steam 认证：{auth} · SDR 配置：{config}";
    }

    public static string? NetworkHint(ServerNetworkReadiness? value, bool english)
    {
        if (value is null) return null;
        if (value.Delayed) return english
            ? "Network authentication/configuration is still not ready after 120s of map readiness. Check Steam connectivity and redacted logs; do not disable certificate verification."
            : "地图就绪已满 120 秒，网络认证/配置仍未就绪。请检查 Steam 网络连通性与脱敏日志；不要关闭证书校验。";
        if (value.ClientCertificateRejected) return english
            ? "A client certificate was rejected; this does not establish a password error or server authentication failure."
            : "曾拒绝客户端证书；这不等于密码错误，也不直接判定服务器认证失败。";
        return null;
    }

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

    /// <summary>Where the server listens, in the operator's words.</summary>
    public static string EndpointLine(bool english, string bindAddress, int gamePort, NetworkAccessMode access)
    {
        var scope = access == NetworkAccessMode.Lan ? (english ? "LAN" : "局域网") : (english ? "this host only" : "仅本机");
        return (english ? "Address: " : "地址：") + bindAddress + ":" + gamePort + (english ? " (" : "（") + scope + (english ? ")" : "）");
    }

    /// <summary>What a player types in the CS2 console.</summary>
    public static string ConnectHint(bool english, string bindAddress, int gamePort) =>
        (english ? "Client: connect " : "客户端：connect ") + bindAddress + ":" + gamePort;

    /// <summary>Firewall hint; the operator opens the port, the launcher never changes the host rules.</summary>
    public static string FirewallHint(bool english, int gamePort) =>
        (english ? "Firewall: sudo ufw allow " : "防火墙：sudo ufw allow ") + gamePort + "/udp";

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
