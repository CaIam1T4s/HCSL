using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HCSL.Host.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    public void ShowDownloadSettings() => DownloadSettingsSection.BringIntoView();
    internal void ShowStartupBaseline() => StartupBaselineHeading.BringIntoView();
    private HCSL.Host.ViewModels.ShellViewModel? Model => DataContext as HCSL.Host.ViewModels.ShellViewModel;
    private async void RecoverDownload(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && model.SteamCmd.CanStart) await model.SteamCmd.RecoverAsync();
    }
    private async void CheckBaseline(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model && model.Baseline.CanRefresh) await model.Baseline.RefreshStatusAsync();
    }
    private async void RebuildBaseline(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !model.Baseline.CanRefresh || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        if (await owner.ChooseAsync("重建启动基线", "将重新校验当前内容库中的服务端文件并重建部署校验记录。不会启动服务器。", "取消", "校验并重建") != 1) return;
        await model.Baseline.RefreshBaselineAsync();
        await owner.ChooseAsync("启动基线", model.Baseline.StatusText, "返回");
    }

    private async void ChooseBackground(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window) return;
        try
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择首页背景", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("背景图片")
                { Patterns = ["*.png", "*.jpg", "*.jpeg"], MimeTypes = ["image/png", "image/jpeg"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            window.SetBackground(stream);
            BackgroundStatus.Text = $"当前背景：{files[0].Name}（本次会话）";
        }
        catch (Exception)
        {
            BackgroundStatus.Text = "无法读取图片，请选择有效的 PNG 或 JPEG 文件。";
        }
    }

    private void ResetBackground(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window) window.ResetBackground();
        BackgroundStatus.Text = "当前使用默认背景";
    }
}
