using Avalonia.Controls;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    /// <summary>Read-only CFG tree. Editing stays external; only exec remains as an explicit action.</summary>
    public async Task EditCfgAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "CFG 浏览 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "只读浏览：显示目录与 .cfg；其他文件请用「打开 CFG 目录」。保存/改名/删除已移除；exec 仍可执行选中的 .cfg。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var path = new TextBlock { Text = "根目录", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var files = new Select();
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        content.Children.Add(path); content.Children.Add(files); content.Children.Add(status);
        string current = "";
        PresetCfgCommand Command(PresetCfgAction action, string? name = null) =>
            new(action, instance.Id, preset.Id, instance.Revision, name, Path: current);
        async Task Refresh()
        {
            var list = await model.CfgAsync(Command(PresetCfgAction.List));
            current = list.Path ?? "";
            path.Text = current.Length == 0 ? "根目录" : "cfg/" + current;
            files.ItemsSource = list.Files;
            files.SelectedIndex = list.Files.Length > 0 ? 0 : -1;
            status.Text = list.Truncated
                ? $"已显示 {list.Files.Length} / {list.Total} 项；其余请用「打开 CFG 目录」。"
                : $"{list.Files.Count(f => f.Kind == PresetCfgKind.Directory)} 个目录，{list.Files.Count(f => f.Kind == PresetCfgKind.File)} 个 .cfg。";
        }
        try
        {
            try { await Refresh(); } catch (Exception e) { status.Text = e.Message; }
            while (!windowClosed)
            {
                var choice = await ShowOverlayAsync(content, ["关闭", "刷新", "进入目录", "返回上级", "执行 exec", "打开 CFG 目录"]);
                if (choice <= 0) break;
                try
                {
                    switch (choice)
                    {
                        case 1: await Refresh(); break;
                        case 2:
                            if (files.SelectedItem is not PresetCfgFile selected || selected.Kind != PresetCfgKind.Directory)
                                throw new IOException("请选择一个目录。");
                            current = selected.RelativePath ?? selected.Name;
                            await Refresh(); break;
                        case 3:
                            if (current.Length == 0) break;
                            current = current.Contains('/') ? current[..current.LastIndexOf('/')] : "";
                            await Refresh(); break;
                        case 4:
                            if (files.SelectedItem is not PresetCfgFile file || file.Kind != PresetCfgKind.File)
                                throw new IOException("请选择一个 .cfg 文件。");
                            var target = file.RelativePath ?? file.Name;
                            if (!model.CanExecCfg(instance.Id, preset.Id) || model.Server.RunId is not { } run)
                                throw new IOException("运行中的实例/预设必须与此页面一致，且服务器处于运行中。");
                            var read = await model.CfgAsync(new(PresetCfgAction.Read, instance.Id, preset.Id, instance.Revision, target));
                            if (await ChooseAsync("向运行服务器执行 CFG", $"实例：{instance.Name} ({instance.Id:D})\n预设：{preset.Name} ({preset.Id:D})\nRunId：{run:D}\nexec {target}\n可能修改游戏状态、换图或停止服务器；仅回执写入，不保证引擎执行成功。", "取消", "执行 exec") != 1) break;
                            status.Text = await model.ExecCfgAsync(instance.Id, preset.Id, run, target, read.Hash ?? "");
                            break;
                        case 5:
                            if (!await model.OpenPresetCfgAsync(instance.Id, preset.Id)) status.Text = model.Error;
                            else status.Text = "已在文件管理器中打开 CFG 目录。";
                            break;
                    }
                }
                catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
                { status.Text = e is InstanceException error ? error.Code + ": " + error.Message : e.Message; }
            }
        }
        finally { files.ItemsSource = null; }
    }
}
