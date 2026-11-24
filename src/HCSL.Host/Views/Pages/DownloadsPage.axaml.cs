using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using HCSL.Contracts;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

public partial class DownloadsPage : UserControl
{
    private ShellViewModel? subscribed;
    private readonly Stopwatch elapsed = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool held, submitting, handlingFailure, validating, authenticationCancelled;
    private Guid? operation;
    private SteamCmdController? Model => (DataContext as ShellViewModel)?.SteamCmd;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public DownloadsPage()
    {
        InitializeComponent();
        InitializeFrameworkCatalog();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        SettingsButton.Icon = Lucide.Geometry(LucideIconKind.Settings);
        timer.Tick += (_, _) =>
        {
            var time = elapsed.Elapsed;
            Elapsed.Text = time.TotalHours >= 1 ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" : time.ToString(@"mm\:ss");
        };
        DownloadDialog.Closing += (_, e) => { if (held) e.Cancel = true; };
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => { Subscribe(); _ = RefreshAsync(); };
        DetachedFromVisualTree += (_, _) => { Unsubscribe(); timer.Stop(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = DataContext as ShellViewModel;
        if (subscribed is null) return;
        subscribed.SteamCmd.PropertyChanged += Changed;
        subscribed.Baseline.PropertyChanged += Changed;
        Update();
    }
    private void Unsubscribe()
    {
        if (subscribed is null) return;
        subscribed.SteamCmd.PropertyChanged -= Changed;
        subscribed.Baseline.PropertyChanged -= Changed;
        subscribed = null;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Update();
    private async Task RefreshAsync()
    {
        if (Model is not { } model || held) return;
        if (!model.Connected) await model.ConnectAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (model.Connected) await model.RefreshContentStatusAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Update();
        await RefreshFrameworkRowsAsync();
    }
    private async void RefreshResources(object? sender, RoutedEventArgs e)
    {
        if (catalogId is { } id) await LoadCatalogAsync(new(id, Refresh: true), catalogGeneration);
        else await RefreshAsync();
    }
    private void OpenSettings(object? sender, RoutedEventArgs e) => Owner?.OpenDownloadSettings();
    private void OpenServer(object? sender, RoutedEventArgs e) => ServerDialog.IsOpen = true;
    private async void InstallServer(object? sender, RoutedEventArgs e) => await RunAsync(false);
    private async void ValidateServer(object? sender, RoutedEventArgs e) => await RunAsync(true);

    private void OpenTask(bool isValidation, bool resumed)
    {
        held = true;
        validating = isValidation;
        authenticationCancelled = false;
        TaskTitle.Text = resumed ? "SteamCMD 任务" : isValidation ? "校验 CS2 服务端" : "下载 CS2 服务端";
        DownloadDialog.Title = TaskTitle.Text;
        TaskSteps.Steps.Clear();
        TaskSteps.Steps.Add(new StepperStep { Label = "准备下载工具" });
        TaskSteps.Steps.Add(new StepperStep { Label = "验证下载条件" });
        TaskSteps.Steps.Add(new StepperStep { Label = isValidation ? "校验服务端文件" : "下载服务端文件" });
        if (!isValidation) TaskSteps.Steps.Add(new StepperStep { Label = "应用服务端文件" });
        TaskSteps.Steps.Add(new StepperStep { Label = "检查部署校验记录" });
        TaskSteps.CurrentIndex = 0;
        ProgressRegion.IsVisible = true;
        ProgressRegion.IsLoading = true;
        elapsed.Restart();
        Elapsed.Text = resumed ? "-" : "00:00";
        if (!resumed) timer.Start();
        ServerDialog.IsOpen = false;
        Owner?.SetDownloadsBlocked(true);
        DownloadDialog.IsOpen = true;
    }

    private async Task RunAsync(bool isValidation)
    {
        if (held || Model is not { } model || subscribed is not { } shell || Owner is not { } owner) return;
        if (!model.Connected) await model.ConnectAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (!model.CanStart) return;
        if (!shell.Servers.CanDownloadResources)
        {
            ServerDialog.IsOpen = false;
            await Dialog.ShowAsync(this, "暂不能下载", new TextBlock { Text = "请先连接宿主并停止服务器，等待其他操作结束。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                null, new DialogAction("关闭", false));
            return;
        }
        var previous = model.LastSnapshot?.Id;
        submitting = true;
        operation = null;
        OpenTask(isValidation, false);
        try
        {
            if (isValidation) await model.ValidateAsync(); else await model.InstallAsync();
            // Controller notifications are dispatched, so drain them before attributing the response to this task.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            operation = model.LastSnapshot?.Id;
            if (operation is null || operation == previous)
            {
                handlingFailure = true;
                await ShowFailureAsync(model.Detail, null);
                handlingFailure = false;
                EndTask();
                return;
            }
        }
        finally { submitting = false; }
        Update();
    }

    private void Update()
    {
        if (Model is not { } model || subscribed is not { } shell) return;
        ServerVersion.Text = model.ContentBuildId ?? (model.ContentInstalled == false ? "未下载" : "-");
        InstallButton.Content = model.ContentInstalled == false ? "安装" : "更新";
        // Reopening the UI must not dismiss or re-submit a task owned by the daemon.
        if (!held && model.Running)
        {
            operation = model.LastSnapshot?.Id;
            OpenTask(false, true);
        }
        if (!held) return;
        CancelButton.IsEnabled = !handlingFailure &&
            (model.CanCancel && model.LastSnapshot?.Id == operation && model.LastSnapshot?.Stage != ProbeStage.Cancelling ||
             !model.Connected && model.CanConnect);
        if (model.LastSnapshot is not { } snapshot) return;
        // Speed and bytes come straight from the daemon's measurement; unknown stays a dash.
        TaskSpeed.Text = model.TransferSpeedText;
        TaskBytes.Text = model.TransferBytesText;
        if (!model.Connected)
        {
            ProgressRegion.IsVisible = true;
            ProgressRegion.IsLoading = true;
            TaskSteps.Steps[TaskSteps.CurrentIndex].Error = "连接已断开，任务状态未知；取消时将尝试重新连接。";
            return;
        }
        if (!submitting && operation != snapshot.Id)
        {
            if (!handlingFailure) _ = LostOperationAsync();
            return;
        }
        var index = snapshot.Stage switch
        {
            ProbeStage.Authenticating or ProbeStage.Authenticated or ProbeStage.WaitingForPassword or
                ProbeStage.WaitingForGuardCode or ProbeStage.WaitingForMobileApproval => 1,
            ProbeStage.DownloadingContent or ProbeStage.ValidatingContent => 2,
            ProbeStage.InstallingContent => 2,
            ProbeStage.CheckingBaseline => TaskSteps.Steps.Count - 1,
            _ => TaskSteps.CurrentIndex
        };
        if (snapshot.Running && operation == snapshot.Id)
        {
            TaskSteps.CurrentIndex = Math.Clamp(index, 0, TaskSteps.Steps.Count - 1);
            foreach (var step in TaskSteps.Steps) { step.Error = null; step.Description = null; }
            if (snapshot.Stage == ProbeStage.ValidatingContent) TaskSteps.Steps[2].Label = "校验服务端文件";
            if (snapshot.Stage == ProbeStage.DownloadingContent) TaskSteps.Steps[2].Label = "下载服务端文件";
            if (snapshot.Stage == ProbeStage.Cancelling) TaskSteps.Steps[TaskSteps.CurrentIndex].Description = "正在取消，等待任务清理完成。";
            var known = snapshot.Stage != ProbeStage.Cancelling &&
                snapshot.Percent is { } p && double.IsFinite(p) && p is >= 0 and <= 100;
            if (known) TaskProgress.Value = snapshot.Percent!.Value;
            ProgressRegion.IsVisible = true;
            ProgressRegion.IsLoading = !known;
            if (!authenticationCancelled && (snapshot.Stage is ProbeStage.WaitingForPassword or ProbeStage.WaitingForGuardCode or ProbeStage.WaitingForMobileApproval))
            {
                authenticationCancelled = true;
                _ = model.CancelAsync(); // Login UI is deliberately not implemented in this iteration.
            }
        }
        if (submitting || handlingFailure || operation != snapshot.Id || snapshot.Running) return;
        if (snapshot.Stage == ProbeStage.Succeeded && snapshot.Result?.Code == "HCSL-CS2-BASELINE-PENDING")
        {
            _ = ShowBaselineWarningAsync();
            return;
        }
        if (!authenticationCancelled && (snapshot.Stage is ProbeStage.Succeeded or ProbeStage.Cancelled))
        {
            EndTask();
            _ = RefreshAsync();
        }
        else if (snapshot.Stage is ProbeStage.Failed or ProbeStage.TimedOut or ProbeStage.RecoveryRequired or ProbeStage.Cancelled)
            _ = FailAsync(snapshot);
    }

    private async Task ShowBaselineWarningAsync()
    {
        handlingFailure = true;
        // This is successful content processing with a maintenance warning, not a failed download.
        EndTask();
        try
        {
            var choice = await Dialog.ShowAsync(this, "校验记录待修复",
                new TextBlock { Text = "服务端文件已处理完成，但部署校验记录未能刷新。请前往设置中的启动基线，重新校验并重建记录。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                null, new DialogAction("关闭", false), new DialogAction("前往设置", true, ButtonVariant.Primary));
            if (choice is true) Owner?.OpenDownloadSettings();
        }
        finally { handlingFailure = false; }
        await RefreshAsync();
    }

    private async Task LostOperationAsync()
    {
        handlingFailure = true;
        timer.Stop(); elapsed.Stop();
        ProgressRegion.IsVisible = false;
        const string reason = "宿主任务身份已改变，无法确认原任务结果。请在下载设置中执行恢复检查；不会取消其他任务或自动重试。";
        TaskSteps.Steps[TaskSteps.CurrentIndex].Error = reason;
        await ShowFailureAsync(reason, null);
        EndTask();
        handlingFailure = false;
    }

    private async Task FailAsync(OperationSnapshot snapshot)
    {
        handlingFailure = true;
        timer.Stop();
        elapsed.Stop();
        ProgressRegion.IsVisible = false;
        var reason = authenticationCancelled ? "该任务需要 Steam 认证；当前版本尚未提供登录界面，任务已取消。" : snapshot.Message;
        TaskSteps.Steps[TaskSteps.CurrentIndex].Error = reason;
        var retry = await ShowFailureAsync(reason, snapshot);
        EndTask();
        handlingFailure = false;
        if (retry && !authenticationCancelled) await RunAsync(validating);
    }

    private async Task<bool> ShowFailureAsync(string reason, OperationSnapshot? snapshot)
    {
        if (Owner is not { } owner) return false;
        var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
        var summary = snapshot?.Result?.ExitCode is { } exit ? $"{reason} · SteamCMD 退出码 {exit}" : reason;
        content.Children.Add(new TextBlock { Text = summary, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var lines = snapshot?.Events.Select(e => e.Message).ToArray() ?? [];
        var lastError = Array.FindLastIndex(lines, l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase) || l.Contains("失败", StringComparison.Ordinal));
        var excerpt = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, (lastError < 0 ? lines.Length : lastError + 1) - 12)).Take(20));
        if (excerpt.Length > 6000) excerpt = excerpt[^6000..];
        content.Children.Add(new TextBlock { Text = lastError < 0 ? "末尾任务记录" : "最后报错片段" });
        content.Children.Add(new Textarea { Text = excerpt.Length == 0 ? "没有可用的报错记录。" : excerpt, IsReadOnly = true, Height = 180,
            FontFamily = HCSL.UI.Shared.Services.MonospaceFont.Resolve() });
        var copy = new TKCode.Arc.Button { Content = "复制错误信息", Variant = ButtonVariant.Ghost };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (owner.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(summary + Environment.NewLine + excerpt);
                else owner.ShowInstanceNotification("剪贴板不可用");
            }
            catch (Exception) { owner.ShowInstanceNotification("复制失败"); }
        };
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(copy);
        var retry = !authenticationCancelled && snapshot is { Stage: not ProbeStage.RecoveryRequired };
        var close = new DialogAction("关闭", false);
        DownloadDialog.IsOpen = false;
        if (retry)
            return await Dialog.ShowAsync(this, "下载失败", content, null, close,
                new DialogAction("重试", true, ButtonVariant.Primary)) is true;
        await Dialog.ShowAsync(this, "下载失败", content, null, close);
        return false;
    }

    private void EndTask()
    {
        held = false;
        timer.Stop(); elapsed.Stop();
        DownloadDialog.IsOpen = false;
        Owner?.SetDownloadsBlocked(false);
    }
    private async void CancelProbe(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || handlingFailure) return;
        if (!model.Connected) await model.ConnectAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (model.CanCancel && model.LastSnapshot?.Id == operation) await model.CancelAsync();
        Update();
    }
}
