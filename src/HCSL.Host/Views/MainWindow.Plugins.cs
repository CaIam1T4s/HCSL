using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    public async Task EditPluginsAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset)
    {
        var draft = new PluginDraft();
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "插件 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "插件包来自内容库 plugin-packages；这里只选择版本和顺序，不联网下载。启用的包在下次部署时复制到预设 addons，禁用后重新部署会移除未被修改的包文件。Metamod/CSS 需要包本身提供正确的 addons 和 gameinfo 覆盖。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var list = new Select(); content.Children.Add(list);
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; content.Children.Add(status);
        string Label(PluginSelection p)
        {
            var info = draft.Loaded?.Selected.FirstOrDefault(s => s.Id == p.Id && s.Version == p.Version);
            var name = info?.Name ?? p.Id;
            return $"{(p.Enabled ? "[启用]" : "[禁用]")} {name} ({p.Id}) {p.Version}";
        }
        void Refresh(int index = 0)
        {
            list.ItemsSource = draft.Plugins.Select(Label).ToArray();
            list.SelectedIndex = draft.Plugins.Count == 0 ? -1 : Math.Clamp(index, 0, draft.Plugins.Count - 1);
        }
        static string RejectionText(PluginResult result) => result.Rejected is { Length: > 0 } rejected
            ? " " + rejected.Length + " 个包被拒绝：" + string.Join("；", rejected.Take(3).Select(x => $"{x.Id}/{x.Version} {x.Code}"))
            : "";
        async Task Load()
        {
            var result = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
            draft.Load(result);
            Refresh(); status.Text = "已读取插件清单；未部署。" + RejectionText(result);
        }
        async Task<bool> Discard() => !draft.Dirty || await ChooseAsync("未保存的插件清单", "丢弃未保存的插件选择？", "继续编辑", "丢弃草稿") == 1;
        async Task AddPlugin()
        {
            var available = draft.Loaded?.Available.Where(a => draft.Plugins.All(p => p.Id != a.Id)).ToArray() ?? [];
            if (available.Length == 0)
            {
                await ChooseAsync("没有可添加的插件", "内容库 plugin-packages 中没有未选择的包版本。请先把包放到 <library>/plugin-packages/<id>/<version>/plugin.json + files/。", "返回");
                return;
            }
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock { Text = "添加插件包", FontSize = 20 });
            var select = new Select { ItemsSource = available.Select(a => $"{a.Name} ({a.Id}) {a.Version}").ToArray(), SelectedIndex = 0 };
            body.Children.Add(select);
            body.Children.Add(new TextBlock { Text = "同一插件只能选择一个版本；数组顺序就是部署顺序。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (await ShowOverlayAsync(body, ["取消", "添加"]) == 1)
            {
                var package = available[Math.Clamp(select.SelectedIndex, 0, available.Length - 1)];
                try { draft.Add(package.Id, package.Version); Refresh(draft.Plugins.Count - 1); status.Text = "已加入草稿；保存后仍不会自动部署。"; }
                catch (InstanceException e) { status.Text = e.Code + ": " + e.Message; }
            }
        }
        async Task ImportPlugin()
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择插件发行目录（含 addons/、csgo/ 或 files/csgo/）",
                AllowMultiple = false
            });
            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path)) return;
            var (defaultId, defaultName, meta) = DetectImport(path);
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = "导入插件包", FontSize = 20 });
            body.Children.Add(new TextBlock { Text = "目录：" + path, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var importId = new Input { Text = defaultId };
            var importName = new Input { Text = defaultName };
            var importVersion = new Input { Text = "1.0.0" };
            var importEnable = new TKCode.Arc.Checkbox { Content = "导入后加入当前预设并启用（需要停服）", IsChecked = false };
            var importForce = new TKCode.Arc.Checkbox { Content = "覆盖同名版本", IsChecked = false };
            body.Children.Add(new TextBlock { Text = "Id" }); body.Children.Add(importId);
            body.Children.Add(new TextBlock { Text = "名称" }); body.Children.Add(importName);
            body.Children.Add(new TextBlock { Text = "版本" }); body.Children.Add(importVersion);
            body.Children.Add(importEnable); body.Children.Add(importForce);
            body.Children.Add(new TextBlock
            {
                Text = meta is null
                    ? "未检测到 Metamod 子插件二进制；普通 addons 包不需要 metaplugins.ini。"
                    : "将生成 addons/metamod/metaplugins.ini：" + meta + "（清单中需排在 Metamod:Source 之后）。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            if (await ShowOverlayAsync(body, ["取消", "导入"]) != 1) return;
            var result = await model.ImportPluginAsync(new PluginImportCommand(instance.Id, preset.Id, path,
                importId.Text ?? "", importName.Text ?? "", importVersion.Text ?? "", meta,
                importEnable.IsChecked == true, importForce.IsChecked == true, instance.Revision));
            await Load();
            status.Text = $"已导入 {result.Id} {result.Version}（{result.Files} 文件，{result.Bytes / 1024} KiB）；"
                + (result.Enabled ? "已启用。" : "未启用。")
                + (result.Warnings.Length == 0 ? "" : " 警告：" + string.Join("；", result.Warnings.Take(3)))
                + " 重新部署后生效。";
        }
        static (string Id, string Name, string? Meta) DetectImport(string path)
        {
            static bool Has(string root, params string[] parts) => Directory.Exists(Path.Combine([root, .. parts]));
            if (Has(path, "addons", "sourcemod") || Has(path, "csgo", "addons", "sourcemod") || Has(path, "files", "csgo", "addons", "sourcemod"))
                return ("sourcemod", "SourceMod", "addons/sourcemod/bin/sourcemod_mm");
            if (Has(path, "addons", "metamod") || Has(path, "csgo", "addons", "metamod") || Has(path, "files", "csgo", "addons", "metamod"))
                return ("metamod", "Metamod:Source", null);
            return (Path.GetFileName(path).TrimEnd('/').ToLowerInvariant().Replace(' ', '-'), Path.GetFileName(path.TrimEnd('/')), null);
        }
        try
        {
            try { await Load(); } catch (Exception e) { status.Text = e.Message; }
            while (!windowClosed)
            {
                var choice = await ShowOverlayAsync(content, ["关闭", "添加插件", "导入插件包", "移除", "启用/禁用", "上移", "下移", "保存清单", "重新读取"]);
                if (choice <= 0) { if (windowClosed || await Discard()) break; else continue; }
                try
                {
                    if (choice == 8) { if (await Discard()) await Load(); continue; }
                    // Importing into the library is allowed while running; enabling is gated by the controller.
                    if (!model.CanEditPlugins && choice != 2)
                        throw new IOException("HCSL-PLUGIN-BUSY: 请先停服并完成恢复/工具任务。");
                    var index = list.SelectedIndex;
                    switch (choice)
                    {
                        case 1: await AddPlugin(); break;
                        case 2: await ImportPlugin(); break;
                        case 3: draft.Remove(index); Refresh(index); status.Text = "已从草稿移除；保存后生效。"; break;
                        case 4: draft.Toggle(index); Refresh(index); status.Text = "已切换启用状态草稿；保存后生效。"; break;
                        case 5: Refresh(draft.Move(index, -1)); status.Text = "已上移草稿；数组顺序即部署顺序。"; break;
                        case 6: Refresh(draft.Move(index, 1)); status.Text = "已下移草稿；数组顺序即部署顺序。"; break;
                        case 7:
                            draft.Load(await model.PluginsAsync(draft.SaveCommand(instance.Id, preset.Id, instance.Revision)));
                            Refresh(index); status.Text = "插件清单已保存；下次部署/启动会应用，未自动部署。"; break;
                    }
                }
                catch (Exception e) when (e is IOException or InstanceException) { status.Text = e is InstanceException i ? i.Code + ": " + i.Message : e.Message; }
            }
        }
        finally { list.ItemsSource = null; }
    }
}
