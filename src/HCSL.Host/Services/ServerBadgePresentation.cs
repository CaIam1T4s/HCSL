using HCSL.Core;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Services;

public static class ServerBadgePresentation
{
    public static (string Text, BadgeTone Tone, LucideIconKind Icon) For(ServerState state, bool connected = true) =>
        !connected ? ("状态未知", BadgeTone.Neutral, LucideIconKind.CircleAlert) : state switch
        {
            ServerState.Preparing => ("准备中", BadgeTone.Info, LucideIconKind.PackageOpen),
            ServerState.Starting => ("启动中", BadgeTone.Info, LucideIconKind.LoaderCircle),
            ServerState.LoadingMap => ("加载地图", BadgeTone.Info, LucideIconKind.Rocket),
            ServerState.Running => ("运行中", BadgeTone.Success, LucideIconKind.CircleCheckBig),
            ServerState.Stopping => ("停服中", BadgeTone.Info, LucideIconKind.Clock),
            ServerState.Failed => ("启动失败", BadgeTone.Danger, LucideIconKind.CircleAlert),
            ServerState.RecoveryRequired => ("需要恢复", BadgeTone.Warning, LucideIconKind.TriangleAlert),
            _ => ("已停止", BadgeTone.Neutral, LucideIconKind.Pause)
        };
}
