using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Frontend.Dev;
using HCSL.Host.Services;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

/// <summary>
/// 概览: what this instance is doing right now and every runtime action for it. Destructive instance operations live here
/// too, so the scope does not need a separate "高级" page.
/// </summary>
public partial class InstanceOverviewPage : UserControl
{
    private HostServerController? subscribed;
    private bool operating, watchingStart, handlingOutcome;
    private Guid? baselineRun;
    private Guid startTarget;
    private long baselineApplied;
    private ServerLogWindow? logs;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public InstanceOverviewPage()
    {
        InitializeComponent();
        Tooltip.SetTip(HelpButton, "启动会先按需部署；停服不会退出界面，也不会取消 SteamCMD");
        Tooltip.SetTip(RefreshButton, "刷新实例状态");
        StopConfirm.Icon = Lucide.Geometry(LucideIconKind.Square);
        DeleteConfirm.Icon = Lucide.Geometry(LucideIconKind.Trash);
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Update(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        Update();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = null;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        Update();
        _ = CheckStartOutcomeAsync();
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var entry = m.Selected?.Entry;
        var definition = entry?.Definition;
        var preset = entry?.Presets.FirstOrDefault(p => p.Id == definition?.SelectedPresetId);
        PortText.Text = definition?.GamePort.ToString() ?? "—";
        AddressText.Text = m.ConnectionAddressText is { Length: > 0 } addresses ? addresses : "未知";
        LayoutText.Text = definition is null ? "—" : $"v{definition.LayoutVersion}";
        NameRow.Description = entry?.Id.ToString("D") ?? "—";
        var players = m.Server.Snapshot?.PlayerRoster is { Players.Length: var online } ? $"在线 {online} 人" : "在线 —";
        ServerRow.Description = definition is null || preset is null
            ? players
            : $"预设 {preset.Name} · {ServerMapRules.Label(new(preset.MapKind, preset.Map))} · {players}";
        DetailRow.Description = m.Server.DetailText;
        var idle = !operating && !watchingStart;
        PortButton.IsEnabled = idle && m.CanMutate && definition is not null;
        StartButton.IsEnabled = idle && m.Selected?.Entry.Definition is not null && m.Server.CanStart;
        DeployButton.IsEnabled = idle && m.CanDeploy;
        RecoverDeployButton.IsEnabled = idle && m.CanRecoverDeployment;
        RecoverButton.IsEnabled = !operating && m.Server.CanRecover;
        ChangeMapButton.IsEnabled = !operating && m.Server.CanSendConsole && !m.Server.Busy;
        PasswordButton.IsEnabled = idle && m.CanMutate;
        RefreshButton.IsEnabled = !operating && !m.Busy;
        LogsButton.IsEnabled = m.Server.Connected;
        StopConfirm.IsEnabled = !operating && m.Server.CanStop;
        RenameButton.IsEnabled = idle && m.CanMutate && definition is not null;
        MigrateButton.IsVisible = definition is { LayoutVersion: var version } && version < InstanceRules.CurrentLayoutVersion;
        MigrateButton.IsEnabled = idle && m.CanMutate && m.Selected is not null;
        OpenRuntimeButton.IsEnabled = !operating && !m.Busy && m.Server.Connected;
        DeleteConfirm.IsEnabled = idle && definition is not null && m.CanCreate;
        if (!m.Server.Active) StopConfirm.IsConfirmed = false;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    /// <summary>Ports are part of the instance record, so this reuses the same editor as the instance list.</summary>
    private async void EditPort(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } original) return;
        var saved = await owner.EditInstanceAsync(original, async draft =>
        {
            var success = await m.UpdateInstanceAsync(original.Id, original.Revision, draft);
            return success ? null : m.Error;
        });
        if (saved) Owner?.ShowInstanceNotification("实例已保存", null, ToastKind.Success);
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        Update();
        try { await action(); }
        catch (Exception) { Error("操作未完成", "请检查宿主状态后重试。"); }
        finally { operating = false; Update(); await CheckStartOutcomeAsync(); }
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m) return;
        if (!m.Server.Connected) await m.InitializeAsync(); else await m.RefreshInstancesAsync();
        if (m.Error.Length > 0) Error("读取实例失败", m.Error);
    });

    private async void DeployServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var preset = m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == d.SelectedPresetId);
        var plan = await m.DeployPreviewAsync(d.Id, d.SelectedPresetId);
        if (plan is null) { Error("无法获取部署预览", "宿主未连接或已有操作。"); return; }
        if (await owner.ChooseAsync("部署预览（不启动）", DevFormat.Plan(plan, m.Selected.Entry, preset), "取消", "部署") != 1) return;
        if (m.Selected?.Entry.Id != d.Id) return;
        if (!await m.DeployAsync()) { Error("部署未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error); return; }
        for (var waited = 0; m.Server.Active && waited < 1800; waited++) await Task.Delay(500);
        var done = m.Server.Code == "HCSL-SERVER-DEPLOYED";
        Owner?.ShowInstanceNotification(done ? "部署完成" : "部署未完成", m.Server.DetailText,
            done ? ToastKind.Success : ToastKind.Error);
    });

    private async void RecoverDeployment(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        if (await owner.ChooseAsync("恢复部署", "保留旧运行目录并删除 journal，不会启动 CS2。", "取消", "恢复") != 1) return;
        if (m.Selected?.Entry.Id != d.Id) return;
        if (!await m.RecoverDeploymentAsync()) { Error("恢复未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error); return; }
        for (var waited = 0; m.Server.Active && waited < 1200; waited++) await Task.Delay(500);
        var done = m.Server.Code == "HCSL-SERVER-DEPLOY-RECOVERED";
        Owner?.ShowInstanceNotification(done ? "恢复完成" : "恢复未完成", m.Server.DetailText,
            done ? ToastKind.Success : ToastKind.Error);
    });

    public Task StartSelectedAsync() => RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var text = m.SelectionText + "\n\n" + m.ConnectionEndpoints(d.GamePort);
        var confirmation = await owner.ConfirmStartAsync(text);
        if (!confirmation.Confirmed || m.Selected?.Entry.Id != d.Id) return;
        startTarget = d.Id;
        baselineRun = m.Server.RunId;
        baselineApplied = m.Server.Applied;
        watchingStart = true;
        if (await m.StartAsync(allowLowMemory: confirmation.AllowLowMemory)) ShowLogs();
        else { watchingStart = false; Error("启动未提交", m.Server.DetailText); }
    });

    private async void StartServer(object? sender, RoutedEventArgs e) => await StartSelectedAsync();

    private async Task CheckStartOutcomeAsync()
    {
        if (!watchingStart || handlingOutcome || operating || Model is not { } m || Owner is not { } owner || m.Server.Busy) return;
        var s = m.Server;
        if (s.ConnectionUnknown || s.Applied <= baselineApplied || s.RunId == baselineRun) { if (s.ConnectionUnknown) watchingStart = false; return; }
        if (s.State is ServerState.Idle or ServerState.Stopped or ServerState.Running) { watchingStart = false; Update(); return; }
        if (s.State is not (ServerState.Failed or ServerState.RecoveryRequired)) return;
        handlingOutcome = true;
        watchingStart = false;
        Update();
        try
        {
            if (s.Code == "HCSL-SERVER-MEMORY-WARNING")
            {
                var failedRun = s.RunId;
                if (await owner.ChooseAsync("低内存启动确认", s.DetailText, "取消", "仍然启动") == 1 &&
                    s.RunId == failedRun && m.Selected?.Entry.Id == startTarget && s.CanStart)
                {
                    baselineRun = s.RunId;
                    baselineApplied = s.Applied;
                    watchingStart = true;
                    if (!await m.StartAsync(allowLowMemory: true)) watchingStart = false;
                }
            }
            else Error("服务器启动失败", s.DetailText);
        }
        finally { handlingOutcome = false; Update(); }
    }

    private async void ChangeMap(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Server.Snapshot is not { RunId: { } run, InstanceId: { } id }) return;
        var target = await owner.MapTargetAsync();
        if (target is null) return;
        if (await owner.ChooseAsync("确认即时换图", ServerMapRules.Label(target) + "\n" + ServerMapRules.ChangeCommand(target), "取消", "换图") != 1) return;
        try
        {
            var receipt = await new ServerMapClient(m.Server).ChangeAsync(id, run, target);
            Owner?.ShowInstanceNotification("换图已投递", ServerMapClient.ReceiptText(receipt), ToastKind.Success);
        }
        catch (IOException error) { Error("换图未完成", error.Message); }
    });

    /// <summary>The hold is the confirmation, so stopping needs no extra dialog.</summary>
    private async void StopServer(object? sender, RoutedEventArgs e)
    {        if (Model is not { } m) return;
        var run = m.Server.RunId;
        if (run is null) { StopConfirm.IsConfirmed = false; return; }
        Owner?.ShowInstanceNotification("正在停止服务器", null, ToastKind.Info);
        var stopped = await m.Server.StopAsync();
        StopConfirm.IsConfirmed = false;
        if (m.Server.RunId != run && m.Server.Active) return;
        Owner?.ShowInstanceNotification(stopped ? "已请求停止服务器" : "停服未提交",
            stopped ? null : m.Server.DetailText, stopped ? ToastKind.Success : ToastKind.Error);
        Update();
    }

    private async void RecoverServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner) return;
        var run = m.Server.RunId;
        if (await owner.ChooseAsync("恢复检查", "检查并清理会话遗留资源。", "取消", "恢复检查") != 1) return;
        if (run != m.Server.RunId) return;
        if (!await m.RecoverAsync()) Error("恢复未提交", m.Error + " " + m.Server.DetailText);
    });

    private async void EditPassword(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var value = await owner.PasswordAsync(d.Name, m.Selected.Entry.HasPassword);
        if (value is not null && m.Selected?.Entry.Id == d.Id) await m.SetPasswordAsync(value);
    });

    private async void RenameInstance(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } original) return;
        var renamed = await owner.EditInstanceAsync(original, async draft =>
        {
            var success = await m.UpdateInstanceAsync(original.Id, original.Revision, draft);
            return success ? null : m.Error;
        }, nameOnly: true);
        if (renamed) Owner?.ShowInstanceNotification("实例已重命名", null, ToastKind.Success);
    });

    private async void MigrateLayout(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } original) return;
        if (await owner.ChooseAsync("迁移实例布局", $"迁移“{original.Name}”的旧目录布局；旧目录归档，共用安装不变。", "取消", "迁移") != 1) return;
        if (m.Selected?.Entry.Id != original.Id || m.Selected?.Entry.Definition?.Revision != original.Revision) return;
        if (!await m.MigrateLayoutAsync()) Error("迁移失败", m.Error);
        else Owner?.ShowInstanceNotification("布局已迁移", null, ToastKind.Success);
    });

    private async void OpenRuntime(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } instance) return;
        if (!await m.OpenRuntimeDirectoryAsync(instance.Id)) Error("无法打开运行目录", m.Error);
    });

    private async void DeleteInstance(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } target) return;
        if (!m.CanCreate || m.Selected?.Entry.Definition?.Revision != target.Revision)
        {
            DeleteConfirm.IsConfirmed = false;
            Error("删除未提交", "实例或宿主状态已变化，请刷新后重试。");
            return;
        }
        operating = true;
        DeleteConfirm.IsConfirmed = false;
        try
        {
            if (!await m.DeleteInstanceAsync(target.Id, target.Revision)) Error("删除失败", m.Error);
            else Owner?.ShowInstanceNotification("实例已删除", target.Name, ToastKind.Success);
        }
        finally { operating = false; Update(); }
    }

    public void ShowSelectedLogs() => ShowLogs();

    private void OpenLogs(object? sender, RoutedEventArgs e) => ShowLogs();

    private void ShowLogs()
    {
        if (logs is not null) { logs.Activate(); return; }
        if (Model is not { } m || Owner is not { } owner) return;
        logs = new ServerLogWindow(m, owner);
        logs.Closed += (_, _) => logs = null;
        logs.Show(owner);
    }
}
