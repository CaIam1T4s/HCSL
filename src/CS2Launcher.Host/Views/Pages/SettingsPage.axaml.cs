using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CS2Launcher.Host.Views.Pages;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

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
