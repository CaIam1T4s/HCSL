using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Frontend.Dev;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class InstancePage : UserControl
{
    private HostServerController? subscribed;
    private bool updating, operating, watchingStart, handlingOutcome;
    private Guid? baselineRun;
    private Guid startTarget;
    private long baselineApplied;
    private ServerLogWindow? logs;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    public InstancePage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { if (subscribed is not null) subscribed.PropertyChanged -= Changed; subscribed = null; };
        InstanceSelect.SelectionChanged += (_, _) => { if (!updating && Model is { } m) m.Selected = InstanceSelect.SelectedItem as HostInstanceItem; };
        PresetSelect.SelectionChanged += (_, _) => { if (!updatingPresets) UpdatePresetButtons(); };
    }
    private void Subscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        Update();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { Update(); _ = CheckStartOutcomeAsync(); }
    private void Update()
    {
        if (Model is not { } m) return;
        updating = true;
        try { InstanceSelect.ItemsSource = m.Instances; InstanceSelect.SelectedItem = m.Selected; }
        finally { updating = false; }
        InstanceSelect.IsEnabled = !operating && !watchingStart && !m.Busy && !m.Server.Busy;
        RefreshButton.IsEnabled = !operating && !m.Busy;
        StartButton.IsEnabled = !operating && !watchingStart && !m.Busy && m.Selected?.Entry.Definition is not null && m.Server.CanStart;
        DeployButton.IsEnabled = !operating && !watchingStart && m.CanDeploy;
        RecoverDeployButton.IsEnabled = !operating && !watchingStart && m.CanRecoverDeployment;
        StopButton.IsEnabled = !operating && m.Server.CanStop;
        ChangeMapButton.IsEnabled = !operating && m.Server.CanSendConsole && !m.Server.Busy;
        RecoverButton.IsEnabled = !operating && m.Server.CanRecover;
        PasswordButton.IsEnabled = !operating && !watchingStart && m.CanMutate;
        SelectionText.Text = m.SelectionText;
        StateText.Text = m.Server.Connected ? m.Server.SummaryLine : "状态未知：宿主未连接";
        DetailText.Text = m.Server.DetailText;
        ErrorText.Text = m.Error;
        UpdatePresets();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true; Update();
        try { await action(); }
        catch (Exception) { if (Owner is { } owner) await owner.ChooseAsync("操作未完成", "请检查宿主状态后重试；敏感详情未输出。", "返回"); }
        finally { operating = false; Update(); await CheckStartOutcomeAsync(); }
    }
    private async void RefreshInstances(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m) return;
        if (!m.Server.Connected) await m.InitializeAsync(); else await m.RefreshInstancesAsync();
    });
    private async void DeployServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var preset = m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == d.SelectedPresetId);
        var plan = await m.DeployPreviewAsync(d.Id, d.SelectedPresetId);
        var preview = plan is null
            ? "无法获取部署预览（宿主未连接或已有操作）；请查看状态后再试。"
            : DevFormat.Plan(plan, m.Selected.Entry, preset);
        var confirmation = await owner.ChooseAsync("部署预览（不启动）",
            preview + "\n\n继续部署？宿主自动探测 reflink/全量复制，不会启动 CS2。\n运行中的服务器需先停服；部署失败不会写入新的运行目录。",
            "取消", "部署");
        if (confirmation != 1 || m.Selected?.Entry.Id != d.Id) return;
        if (!await m.DeployAsync()) { await owner.ChooseAsync("部署未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error, "返回"); return; }
        // The request is accepted asynchronously; wait for the daemon to finish the deployment task.
        for (var waited = 0; m.Server.Active && waited < 1800; waited++) await Task.Delay(500);
        var done = m.Server.Code == "HCSL-SERVER-DEPLOYED";
        await owner.ChooseAsync(done ? "部署完成" : "部署未完成", m.Server.DetailText, "返回");
    });
    private async void RecoverDeployment(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        if (await owner.ChooseAsync("恢复部署",
            "检测到中断的部署时会保留旧运行目录并删除 journal；不会启动 CS2，也不会部署新内容。\n恢复后可再点「部署（不启动）」。",
            "取消", "恢复") != 1 || m.Selected?.Entry.Id != d.Id) return;
        if (!await m.RecoverDeploymentAsync())
        {
            await owner.ChooseAsync("恢复未提交", m.Error.Length == 0 ? m.Server.DetailText : m.Error, "返回");
            return;
        }
        for (var waited = 0; m.Server.Active && waited < 1200; waited++) await Task.Delay(500);
        var done = m.Server.Code == "HCSL-SERVER-DEPLOY-RECOVERED";
        await owner.ChooseAsync(done ? "恢复完成" : "恢复未完成", m.Server.DetailText, "返回");
    });
    public Task StartSelectedAsync() => RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var text = m.SelectionText + "\n\n" + m.ConnectionEndpoints(d.GamePort) +
            "\n必要时先进行 reflink 部署，再启动（超时 120 秒）。实际 VAC 当前关闭。" +
            "\n本机地址为自动识别的连接参考；访问范围由防火墙控制，不提供公网可达性保证。";
        var confirmation = await owner.ConfirmStartAsync(text);
        if (!confirmation.Confirmed || m.Selected?.Entry.Id != d.Id) return;
        startTarget = d.Id;
        baselineRun = m.Server.RunId; baselineApplied = m.Server.Applied; watchingStart = true;
        if (await m.StartAsync(allowLowMemory: confirmation.AllowLowMemory)) ShowLogs();
        else { watchingStart = false; await owner.ChooseAsync("启动未提交", m.Server.DetailText, "返回"); }
    });
    private async void StartServer(object? sender, RoutedEventArgs e) => await StartSelectedAsync();

    private async Task CheckStartOutcomeAsync()
    {
        if (!watchingStart || handlingOutcome || operating || Model is not { } m || Owner is not { } owner || m.Server.Busy) return;
        var s = m.Server;
        if (s.ConnectionUnknown) { watchingStart = false; Update(); return; }
        if (s.Applied <= baselineApplied || s.RunId == baselineRun) return;
        if (s.State is ServerState.Idle or ServerState.Stopped) { watchingStart = false; Update(); return; }
        if (s.State == ServerState.Running) { watchingStart = false; Update(); return; }
        if (s.State is not (ServerState.Failed or ServerState.RecoveryRequired)) return;
        handlingOutcome = true; watchingStart = false; Update();
        try
        {
            if (s.Code == "HCSL-SERVER-MEMORY-WARNING")
            {
                var failedRun = s.RunId;
                if (await owner.ChooseAsync("低内存启动确认", s.DetailText + "\n\n可用内存不足 4 GiB，继续可能导致卡顿或 OOM。低于 3 GiB 仍禁止启动。服务器硬上限 2560 MiB，禁止 swap。仅本次有效。", "取消", "仍然启动") == 1 && s.RunId == failedRun && m.Selected?.Entry.Id == startTarget && s.CanStart)
                {
                    baselineRun = s.RunId; baselineApplied = s.Applied; watchingStart = true;
                    if (!await m.StartAsync(allowLowMemory: true)) watchingStart = false;
                }
            }
            else await owner.ChooseAsync("服务器启动失败", s.DetailText, "返回");
        }
        finally { handlingOutcome = false; Update(); }
    }
    private async void ChangeMap(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Server.Snapshot is not { RunId: { } run, InstanceId: { } id }) return;
        var target = await owner.MapTargetAsync();
        if (target is null) return;
        if (await owner.ChooseAsync("确认即时换图", $"运行实例：{id}\nRunId：{run}\n目标：{ServerMapRules.Label(target)}\n{ServerMapRules.ChangeCommand(target)}\n会影响当前玩家，不修改预设；投递不代表加载成功。", "取消", "换图") != 1) return;
        try { var receipt = await new ServerMapClient(m.Server).ChangeAsync(id, run, target); await owner.ChooseAsync("换图投递结果", ServerMapClient.ReceiptText(receipt), "返回"); }
        catch (IOException e) { await owner.ChooseAsync("换图未完成", e.Message, "返回"); }
    });
    private async void StopServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner) return;
        var run = m.Server.RunId;
        if (await owner.ChooseAsync("停服确认", $"停止运行会话 {run}？\n先尝试正常退出，超时整组清理；不会退出界面，也不会取消 SteamCMD。", "取消", "停服") == 1 && run == m.Server.RunId)
            if (!await m.Server.StopAsync()) await owner.ChooseAsync("停服未提交", m.Server.DetailText, "返回");
    });
    private async void RecoverServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner) return;
        var run = m.Server.RunId;
        if (await owner.ChooseAsync("恢复检查", $"检查并清理会话 {run} 的遗留资源。不会强杀无法确认归属的进程。", "取消", "恢复检查") == 1 && run == m.Server.RunId)
            if (!await m.RecoverAsync()) await owner.ChooseAsync("恢复未提交", m.Error + "\n" + m.Server.DetailText, "返回");
    });
    private async void EditPassword(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var value = await owner.PasswordAsync(d.Name, m.Selected.Entry.HasPassword);
        if (value is not null && m.Selected?.Entry.Id == d.Id) await m.SetPasswordAsync(value);
    });
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
