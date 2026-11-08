using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
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
        StopButton.IsEnabled = !operating && m.Server.CanStop;
        RecoverButton.IsEnabled = !operating && m.Server.CanRecover;
        PasswordButton.IsEnabled = !operating && !watchingStart && m.CanMutate;
        SelectionText.Text = m.SelectionText;
        StateText.Text = m.Server.Connected ? m.Server.SummaryLine : "状态未知：宿主未连接";
        DetailText.Text = m.Server.DetailText;
        ErrorText.Text = m.Error;
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
    private async void StartServer(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Model is not { } m || Owner is not { } owner || m.Selected?.Entry.Definition is not { } d) return;
        var text = m.SelectionText + "\n\n" + m.ConnectionEndpoints(d.GamePort) +
            "\n必要时先进行 reflink 部署，再启动（超时 120 秒）。实际 VAC 当前关闭。" +
            "\n本机地址为自动识别的连接参考；访问范围由防火墙控制，不提供公网可达性保证。";
        if (await owner.ChooseAsync("启动服务器", text, "取消", "启动") != 1 || m.Selected?.Entry.Id != d.Id) return;
        startTarget = d.Id;
        baselineRun = m.Server.RunId; baselineApplied = m.Server.Applied; watchingStart = true;
        if (await m.StartAsync()) ShowLogs();
        else { watchingStart = false; await owner.ChooseAsync("启动未提交", m.Server.DetailText, "返回"); }
    });
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
