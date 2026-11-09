using Avalonia.Controls;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    public async Task EditCfgAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "CFG 管理 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock { Text = "仅预设 cfg 根目录的 UTF-8 .cfg（8 KiB）。保存不是执行；文件操作需停服。受管密码文件不展示。内容可能含敏感信息。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var files = new Select();
        content.Children.Add(new TextBlock { Text = "选择 CFG 后点击读取；文件名输入框也用于重命名" });
        var name = new Input { Text = "practice.cfg", PlaceholderText = "文件名（重命名时在此输入新名）" };
        var editor = new Textarea { FrameHeight = 190, FrameMinHeight = 0, IsResizable = false, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
        editor.Classes.Add("server-log");
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        content.Children.Add(files); content.Children.Add(name); content.Children.Add(editor); content.Children.Add(status);
        string? loadedName = null, hash = null;
        var saved = "";
        bool Dirty() => (editor.Text ?? "") != saved || (loadedName is not null && name.Text != loadedName);
        PresetCfgCommand Command(PresetCfgAction action, string? file = null, string? text = null, string? expected = null, string? newName = null)
            => new(action, instance.Id, preset.Id, instance.Revision, file, text, expected, newName);
        async Task Refresh()
        {
            var list = await model.CfgAsync(Command(PresetCfgAction.List));
            files.ItemsSource = list.Files; files.SelectedIndex = list.Files.Length > 0 ? 0 : -1;
        }
        async Task<bool> Discard() => !Dirty() || await ChooseAsync("未保存的 CFG 草稿", "该操作将丢弃编辑器中的未保存内容；不会自动保存或执行。", "继续编辑", "丢弃草稿") == 1;
        try
        {
            try { await Refresh(); } catch (Exception e) { status.Text = e.Message; }
            while (!windowClosed)
            {
                var choice = await ShowOverlayAsync(content, ["关闭", "刷新列表", "读取", "新建", "保存", "重命名", "删除", "执行 exec"]);
                if (choice <= 0) { if (windowClosed || await Discard()) break; else continue; }
                try
                {
                    if (choice is >= 3 and <= 6 && !model.CanEditCfg)
                        throw new IOException("HCSL-CFG-BUSY: 请先停服并完成恢复/工具任务。");
                    switch (choice)
                    {
                        case 1: await Refresh(); status.Text = "列表已刷新；当前草稿与已加载版本保持不变。"; break;
                        case 2:
                            if (files.SelectedItem is not PresetCfgFile selected) throw new IOException("请先选择一个 CFG 文件。");
                            if (!await Discard()) break;
                            var read = await model.CfgAsync(Command(PresetCfgAction.Read, selected.Name));
                            loadedName = read.Name; name.Text = read.Name; hash = read.Hash; editor.Text = saved = read.Content ?? "";
                            status.Text = "已读取；编辑不会自动保存，保存不会自动 exec。";
                            break;
                        case 3:
                            if (!await Discard()) break;
                            loadedName = hash = null; name.Text = "practice.cfg"; editor.Text = saved = "";
                            status.Text = "新草稿；输入未占用的文件名后保存，不会覆盖现有文件。"; break;
                        case 4:
                            var target = PresetCfgRules.Name(name.Text);
                            if (loadedName is not null && loadedName != target) throw new IOException("请先使用重命名，或新建另一份草稿。保存不会隐式改名。");
                            var written = await model.CfgAsync(Command(PresetCfgAction.Save, target, editor.Text ?? "", hash));
                            loadedName = written.Name; name.Text = written.Name; hash = written.Hash; saved = editor.Text ?? "";
                            await Refresh(); status.Text = "已保存到预设；没有向服务器执行 exec。"; break;
                        case 5:
                            if (loadedName is null || hash is null) throw new IOException("请先读取或保存一个文件。");
                            var renamed = PresetCfgRules.Name(name.Text);
                            if (await ChooseAsync("重命名 CFG", $"实例：{instance.Name}\n预设：{preset.Name}\n{loadedName} → {renamed}\n不会修改 CFG 引用；未保存的文本仍保留为草稿。", "取消", "重命名") != 1) break;
                            await model.CfgAsync(Command(PresetCfgAction.Rename, loadedName, expected: hash, newName: renamed));
                            loadedName = renamed; await Refresh(); status.Text = "已重命名；不会自动修改其他 CFG 中的 exec 引用。"; break;
                        case 6:
                            if (loadedName is null || hash is null) throw new IOException("请先读取或保存一个文件。");
                            if (await ChooseAsync("删除 CFG", $"实例：{instance.Name}\n预设：{preset.Name}\n文件：{loadedName}\n移到预设 .cfg-trash；当前草稿将丢弃，不会修改其他文件引用。", "取消", "删除") != 1) break;
                            await model.CfgAsync(Command(PresetCfgAction.Delete, loadedName, expected: hash));
                            loadedName = hash = null; editor.Text = saved = ""; name.Text = "practice.cfg";
                            await Refresh(); status.Text = "已移到预设 .cfg-trash。"; break;
                        case 7:
                            if (loadedName is null || hash is null || Dirty()) throw new IOException("请先保存或重新读取文件；不会执行未保存的草稿。");
                            if (!model.CanExecCfg(instance.Id, preset.Id) || model.Server.RunId is not { } run)
                                throw new IOException("运行中的实例/预设必须与此编辑器一致，且服务器处于运行中；未知运行预设时不执行。");
                            if (await ChooseAsync("向运行服务器执行 CFG", $"实例：{instance.Name} ({instance.Id:D})\n预设：{preset.Name} ({preset.Id:D})\nRunId：{run:D}\nexec {loadedName}\n可能修改游戏状态、换图或停止服务器；仅回执写入，不保证引擎执行成功。", "取消", "执行 exec") != 1) break;
                            status.Text = await model.ExecCfgAsync(instance.Id, preset.Id, run, loadedName, hash); break;
                    }
                }
                catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
                { status.Text = e is InstanceException error ? error.Code + ": " + error.Message : e.Message; }
            }
        }
        finally { editor.Text = ""; saved = ""; hash = null; }
    }
}
