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
        _ = ProbeAdminAvailabilityAsync();
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
    /// The 管理员 section only makes sense when the preset has a framework whose admin list HCSL can read. The probe runs
    /// once per instance/preset and hides the row (switching away if it was open) when the list is unavailable.
    /// </summary>
    private async Task ProbeAdminAvailabilityAsync()
    {
        if (Servers is not { } m || m.Selected?.Entry.Definition is not { } definition || !m.Server.Connected) return;
        // In the preset scope the availability belongs to that preset, not to the instance's current one.
        var presetId = (DataContext as ShellViewModel)?.ManagedPresetId ?? definition.SelectedPresetId;
        var key = (definition.Id, presetId);
        if (adminProbe == key) return;
        adminProbe = key;
        var available = true;
        try { await m.AdminsAsync(definition.Id, presetId); }
        catch (IOException) { available = false; }
        catch (Exception) { return; }
        ApplyAdminAvailability(available);
    }

    /// <summary>Called after the plugin list changes, so a newly enabled admin framework shows the section again.</summary>
    public void RefreshAdminAvailability()
    {
        adminProbe = null;
        _ = ProbeAdminAvailabilityAsync();
    }

    private void ApplyAdminAvailability(bool available)
    {
        if (PresetItem("admins") is { } admins) admins.IsVisible = available;
        if (!available && DataContext is ShellViewModel { IsPresetAdmins: true } shell)
            shell.SelectPresetSection("cfg");
    }

    /// <summary>The preset sidebar's rows are resolved by key, the same way the instance rows are.</summary>
    private TKCode.Arc.SidebarItem? PresetItem(string key) =>
        PresetSidebar.Sections.SelectMany(section => section.Items).FirstOrDefault(item => item.Key == key);

    private (Guid Instance, Guid Preset)? adminProbe;

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
            var presetId = m.Selected?.Entry.Definition?.SelectedPresetId ?? d.SelectedPresetId;
            if (await m.StartAsync(allowLowMemory: confirmation.AllowLowMemory))
            {
                ShowLogs();
                _ = WarnManualPoolAsync(m, d.Id, presetId);
            }
            else
            {
                // A rejected start already carries the daemon's code and next step; show it in full, not as a toast.
                watchingStart = false;
                if (m.Server.DetailText is { Length: > 0 } detail) ShowStartFailure(m.Server.Code, detail);
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
            else ShowStartFailure(failure.Value.Code, failure.Value.Detail);
        }
        finally { handlingOutcome = false; }
    }

    private void ShowStartFailure(string code, string detail)
    {
        var message = code switch
        {
            "HCSL-DEPLOY-VALIDATION-INTERRUPTED" => "服务端校验未完成。请到「下载中心」完成校验后再部署。",
            "HCSL-DEPLOY-INSTALL-STATE" => "安装记录缺少有效版本。请到「下载中心」校验服务端文件。",
            "HCSL-DEPLOY-BASELINE" => "启动基线校验失败。请到「设置 → 启动基线」校验并重建。",
            _ => detail
        };
        ShowInstanceNotification("服务器启动失败", message);
    }

    private async Task WarnManualPoolAsync(HostServerController model, Guid instanceId, Guid presetId)
    {
        try
        {
            var result = await model.MapPoolsAsync(new(MapPoolLibraryAction.List, instanceId));
            if (result.Library.PoolOf(presetId) is not { } pool ||
                MapPoolRules.Rotation(new(1, 0, pool.Maps)) != MapPoolRotation.Manual) return;
            var title = $"图池「{pool.Name}」不支持自动轮换";
            if (logs is { } window) window.NotifyManualPool(title);
            else ShowInstanceNotification(title, kind: TKCode.Arc.ToastKind.Warning);
        }
        catch (Exception error) when (error is IOException or InstanceException)
        {
            // Advisory only: reading pool metadata does not affect a submitted start.
        }
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
