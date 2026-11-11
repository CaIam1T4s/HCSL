using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class InstancesPage : UserControl
{
    private bool operating;
    public InstancesPage() => InitializeComponent();
    private async void CreateInstance(object? sender, RoutedEventArgs e) => await EditAsync(false);
    private async void EditInstance(object? sender, RoutedEventArgs e) => await EditAsync(true);
    private async Task EditAsync(bool editing)
    {
        if (operating || DataContext is not ShellViewModel vm || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        var model = vm.Servers;
        var original = editing ? model.Selected?.Entry.Definition : null;
        if (!model.CanCreate || (editing && original is null)) return;
        operating = true;
        try
        {
            await owner.EditInstanceAsync(original, async draft =>
            {
                var success = original is null ? await model.CreateInstanceAsync(draft)
                    : await model.UpdateInstanceAsync(original.Id, original.Revision, draft);
                return success ? null : model.Error;
            });
        }
        finally { operating = false; }
    }
    private async void RefreshInstances(object? sender, RoutedEventArgs e)
    {
        if (!operating && DataContext is ShellViewModel vm) await vm.Servers.RefreshInstancesAsync();
    }
    private async void MigrateLayout(object? sender, RoutedEventArgs e)
    {
        if (operating || DataContext is not ShellViewModel vm || !vm.Servers.CanMigrateLayout
            || vm.Servers.Selected?.Entry.Definition is not { } original
            || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        operating = true;
        try
        {
            if (await owner.ChooseAsync("迁移实例布局", $"将“{original.Name}”的旧 home/userdata 迁移到实例根目录。\n旧预设目录会归档，不删除；不同 userdata 可能拒绝自动合并。\n服务器必须已停止。", "取消", "迁移") != 1) return;
            if (!await vm.Servers.MigrateLayoutAsync())
                await owner.ChooseAsync("迁移失败", vm.Servers.Error, "返回");
        }
        finally { operating = false; }
    }
    private async void DeleteInstance(object? sender, RoutedEventArgs e)
    {
        if (operating || DataContext is not ShellViewModel vm || !vm.Servers.CanMutate
            || vm.Servers.Selected?.Entry.Definition is not { } original
            || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        operating = true;
        try
        {
            if (await owner.ChooseAsync("删除实例", $"确定删除“{original.Name}”？\n实例将按宿主规则移入回收目录；本界面不提供恢复入口。共用 CS2 基础安装不受影响。", "取消", "删除") != 1) return;
            // Capture identity and revision before confirmation; never delete a newly selected row.
            if (!await vm.Servers.DeleteInstanceAsync(original.Id, original.Revision))
                await owner.ChooseAsync("删除失败", vm.Servers.Error, "返回");
        }
        finally { operating = false; }
    }
}
