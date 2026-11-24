using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class PresetOverviewPage : UserControl
{
    private bool editing;

    public PresetOverviewPage() => InitializeComponent();

    private async void EditPreset(object? sender, RoutedEventArgs e)
    {
        if (editing || DataContext is not ShellViewModel { ManagedPresetId: { } presetId } shell ||
            TopLevel.GetTopLevel(this) is not MainWindow owner ||
            shell.Servers is not { CanMutate: true, Selected.Entry: { Definition: { } instance } entry } servers ||
            entry.Presets.FirstOrDefault(p => p.Id == presetId) is not { } preset) return;

        editing = true;
        try
        {
            await owner.EditPresetAsync("编辑基本信息", preset, false, async draft =>
                await servers.UpdatePresetAsync(instance.Id, instance.Revision, draft) ? null : servers.Error);
        }
        catch (Exception)
        {
            owner.ShowInstanceNotification("操作未完成", "请检查宿主状态后重试。");
        }
        finally { editing = false; }
    }
}
