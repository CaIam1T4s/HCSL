using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc.Icons.Lucide;

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
    // A failed start can be one transient snapshot: a status poll arriving right after it overwrites the state, so the
    // failure is captured on the notifying thread instead of being read back later.
    private (string Code, string Detail)? pendingStartFailure;
    private HostServerController? Servers => shellServers;

    public Task StartHomeServerAsync() => StartSelectedAsync();

    public void OpenSelectedInstanceLogs() => ShowLogs();

    public Task StartSelectedAsync() => RunStartAsync();

    /// <summary>SidebarItem is not a control, so the action rows are resolved by key inside the section list.</summary>
    private TKCode.Arc.SidebarItem? ScopeItem(string key) =>
        InstanceSidebar.Sections.SelectMany(section => section.Items).FirstOrDefault(item => item.Key == key);

    /// <summary>The action rows follow the host state; nothing else in the scope offers them.</summary>
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
            pendingStartFailure = null;
            if (await m.StartAsync(allowLowMemory: confirmation.AllowLowMemory)) ShowLogs();
            else
            {
                // A rejected start already carries the daemon's code and next step; show it in full, not as a toast.
                watchingStart = false;
                if (m.Server.DetailText is { Length: > 0 } detail) await ChooseAsync("服务器启动失败", detail, "返回");
                else ShowInstanceNotification("启动未提交", m.Error);
            }
        }
        finally { starting = false; UpdateScopeFooter(); _ = CheckStartOutcomeAsync(); }
    }

    /// <summary>Remembers a failed start while its snapshot is current; called from the change notification.</summary>
    private void CaptureStartFailure()
    {
        if (!watchingStart || Servers is not { } m) return;
        var s = m.Server;
        if (s.State is not (ServerState.Failed or ServerState.RecoveryRequired) || s.RunId == baselineRun) return;
        pendingStartFailure = (s.Code, s.DetailText);
    }

    /// <summary>Watches the run that this window started, so a low-memory or failed start reports once.</summary>
    private async Task CheckStartOutcomeAsync()
    {
        if (!watchingStart || handlingOutcome || Servers is not { } m) return;
        var s = m.Server;
        var failure = pendingStartFailure;
        if (failure is null)
        {
            if (s.ConnectionUnknown) { watchingStart = false; return; }
            if (s.Applied <= baselineApplied || s.RunId == baselineRun) return;
            if (s.State is ServerState.Idle or ServerState.Stopped or ServerState.Running) { watchingStart = false; return; }
            if (s.State is not (ServerState.Failed or ServerState.RecoveryRequired)) return;
            failure = (s.Code, s.DetailText);
        }
        pendingStartFailure = null;
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
            else await ChooseAsync("服务器启动失败", failure.Value.Detail, "返回");
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
