using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views;

/// <summary>
/// Runtime actions for the selected instance, kept out of the instance scope: starting happens from the home page and the
/// instance list, logs and map changes from the console window.
/// </summary>
public partial class MainWindow
{
    private ServerLogWindow? logs;
    private bool starting, watchingStart, handlingOutcome;
    private Guid? baselineRun;
    private Guid startTarget;
    private long baselineApplied;
    private HostServerController? Servers => shellServers;

    public Task StartHomeServerAsync() => StartSelectedAsync();

    public void OpenSelectedInstanceLogs() => ShowLogs();

    public Task StartSelectedAsync() => RunStartAsync();

    private void StartScopedInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = StartSelectedAsync();

    private void OpenScopedConsole(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowLogs();

    /// <summary>The scope footer offers the two runtime entry points; their availability follows the host state.</summary>
    private void UpdateScopeFooter()
    {
        if (shellServers is not { } m)
        {
            InstanceStartButton.IsEnabled = InstanceConsoleButton.IsEnabled = false;
            return;
        }
        var idle = !starting && m.Server.Connected && !m.Busy;
        InstanceStartButton.IsEnabled = idle && m.Selected?.Entry.Definition is not null && m.Server.CanStart;
        InstanceConsoleButton.IsEnabled = idle;
        InstanceStartButton.IsVisible = !m.Server.Active;
        InstanceConsoleButton.IsVisible = m.Server.Connected;
    }

    private async Task RunStartAsync()
    {
        if (starting || Servers is not { } m || m.Selected?.Entry.Definition is not { } d) return;
        starting = true;
        try
        {
            var text = m.SelectionText + "\n\n" + m.ConnectionEndpoints(d.GamePort);
            var confirmation = await ConfirmStartAsync(text);
            if (!confirmation.Confirmed || m.Selected?.Entry.Id != d.Id) return;
            startTarget = d.Id;
            baselineRun = m.Server.RunId;
            baselineApplied = m.Server.Applied;
            watchingStart = true;
            if (await m.StartAsync(allowLowMemory: confirmation.AllowLowMemory)) ShowLogs();
            else
            {
                watchingStart = false;
                ShowInstanceNotification("启动未提交", m.Server.DetailText);
            }
        }
        finally { starting = false; UpdateScopeFooter(); }
    }

    /// <summary>Watches the run that this window started, so a low-memory or failed start reports once.</summary>
    private async Task CheckStartOutcomeAsync()
    {
        if (!watchingStart || handlingOutcome || starting || Servers is not { } m || m.Server.Busy) return;
        var s = m.Server;
        if (s.ConnectionUnknown || s.Applied <= baselineApplied || s.RunId == baselineRun)
        {
            if (s.ConnectionUnknown) watchingStart = false;
            return;
        }
        if (s.State is ServerState.Idle or ServerState.Stopped or ServerState.Running)
        {
            watchingStart = false;
            return;
        }
        if (s.State is not (ServerState.Failed or ServerState.RecoveryRequired)) return;
        handlingOutcome = true;
        watchingStart = false;
        try
        {
            if (s.Code == "HCSL-SERVER-MEMORY-WARNING")
            {
                var failedRun = s.RunId;
                if (await ChooseAsync("低内存启动确认", s.DetailText, "取消", "仍然启动") == 1 &&
                    s.RunId == failedRun && m.Selected?.Entry.Id == startTarget && s.CanStart)
                {
                    baselineRun = s.RunId;
                    baselineApplied = s.Applied;
                    watchingStart = true;
                    if (!await m.StartAsync(allowLowMemory: true)) watchingStart = false;
                }
            }
            else ShowInstanceNotification("服务器启动失败", s.DetailText);
        }
        finally { handlingOutcome = false; }
    }

    private void ShowLogs()
    {
        if (logs is not null) { logs.Activate(); return; }
        if (Servers is not { } m) return;
        logs = new ServerLogWindow(m, this);
        logs.Closed += (_, _) => logs = null;
        logs.Show(this);
    }
}
