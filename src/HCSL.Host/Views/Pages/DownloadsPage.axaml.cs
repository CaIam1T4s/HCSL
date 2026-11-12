using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class DownloadsPage : UserControl
{
    private SteamCmdController? subscribed;
    private Guid? lastChallenge;
    private SteamCmdController? Model => (DataContext as ShellViewModel)?.SteamCmd;
    public DownloadsPage()
    {
        InitializeComponent();
        TaskLogOutput.FontFamily = HCSL.UI.Shared.Services.MonospaceFont.Resolve();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { if (subscribed is not null) subscribed.PropertyChanged -= Changed; subscribed = null; SecretInput.Text = ""; };
    }
    private void Subscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        SecretInput.Text = "";
        lastChallenge = Model?.ChallengeId;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (Model?.ChallengeId != lastChallenge || Model?.NeedsInput != true) SecretInput.Text = "";
        lastChallenge = Model?.ChallengeId;
    }
    private async void ConnectDaemon(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.ConnectAsync(); }
    private async void StartProbe(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.StartAsync(); }
    private async void InstallServer(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.InstallAsync(); }
    private async void ValidateServer(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.ValidateAsync(); }
    private async void CancelProbe(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.CancelAsync(); }
    private async void RecoverProbe(object? sender, RoutedEventArgs e) { if (Model is { } model) await model.RecoverAsync(); }
    private BaselineController? BaselineModel => (DataContext as ShellViewModel)?.Baseline;
    private async void RefreshBaselineStatus(object? sender, RoutedEventArgs e)
    {
        if (BaselineModel is { } model) await model.RefreshStatusAsync();
    }
    /// <summary>Refreshing rewrites the local baseline, so it needs the same explicit confirmation as other writes.</summary>
    private async void RefreshBaseline(object? sender, RoutedEventArgs e)
    {
        if (BaselineModel is not { } model || !model.CanRefresh) return;
        if (TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        if (await owner.ChooseAsync("刷新启动基线",
                "将重新校验当前 CS2 安装的入口、原生文件和必要配置，并把结果写为本地启动基线。\n不会启动服务器，也不会删除旧基线。\n确认继续？",
                "取消", "校验并刷新") != 1) return;
        await model.RefreshBaselineAsync();
        await owner.ChooseAsync("启动基线", model.Status.Length > 0 ? model.Status : model.StatusText, "返回");
    }
    private async void SubmitSecret(object? sender, RoutedEventArgs e)
    {
        var secret = SecretInput.Text ?? "";
        SecretInput.Text = "";
        if (Model is { } model) await model.SubmitAsync(secret);
    }
}
