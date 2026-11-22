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

    /// <summary>
    /// One dispatcher for the instance sidebar: the 运行 rows are actions, the others are sections. Public so the GUI test
    /// can invoke the same path the sidebar uses.
    /// </summary>
    public void InvokeInstanceScopeAction(string key)
    {
        switch (key)
        {
            case "start": _ = StartSelectedAsync(); return;
            case "console": ShowLogs(); return;
            default: (DataContext as ShellViewModel)?.SelectInstanceSection(key); return;
        }
    }

    /// <summary>The 运行 rows follow the host state; nothing else in the scope offers them.</summary>
    private void UpdateScopeFooter()
    {
        if (ScopeItem("start") is not { } start || ScopeItem("console") is not { } console) return;
        if (shellServers is not { } m)
        {
            start.IsEnabled = console.IsEnabled = false;
            start.IsVisible = true;
            return;
        }
        var idle = !starting && m.Server.Connected && !m.Busy;
        start.IsEnabled = idle && m.Selected?.Entry.Definition is not null && m.Server.CanStart;
        console.IsEnabled = idle;
        start.IsVisible = !m.Server.Active;
        console.IsVisible = m.Server.Connected;
    }

    /// <summary>SidebarItem is not a control, so its rows are resolved by key rather than through generated fields.</summary>
    private TKCode.Arc.SidebarItem? ScopeItem(string key) =>
        InstanceSidebar.Sections.SelectMany(section => section.Items).FirstOrDefault(item => item.Key == key);

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
