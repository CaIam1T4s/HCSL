using Avalonia.Controls;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;
public partial class MainWindow
{
    public async Task EditMapPoolAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset)
    {
        var draft = new MapPoolDraft();
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "地图池 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock { Text = "最多 128 项；添加/删除/排序只修改草稿，保存不换图。工坊合集必须单独占用图池，并由合集决定启动地图/顺序。活动会话只读。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var list = new Select(); content.Children.Add(list);
        content.Children.Add(new TextBlock { Text = NativeMapRotation.Status(), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; content.Children.Add(status);
        void Refresh(int index = 0)
        {
            list.ItemsSource = draft.Maps.Select((map, i) => $"{i + 1}. {ServerMapRules.Label(map)}").ToArray();
            list.SelectedIndex = draft.Maps.Count == 0 ? -1 : Math.Clamp(index, 0, draft.Maps.Count - 1);
        }
        async Task Load()
        {
            draft.Load(await model.MapPoolAsync(new(MapPoolAction.Read, instance.Id, preset.Id)));
            Refresh(); status.Text = "已读取地图池；实际下一张地图：未知。";
        }
        async Task<bool> Discard() => !draft.Dirty || await ChooseAsync("未保存的地图池", "丢弃未保存的地图池草稿？", "继续编辑", "丢弃草稿") == 1;
        try
        {
            try { await Load(); } catch (Exception e) { status.Text = e.Message; }
            while (!windowClosed)
            {
                var choice = await ShowOverlayAsync(content, ["关闭", "添加", "删除", "上移", "下移", "保存地图池", "重新读取"]);
                if (choice <= 0) { if (windowClosed || await Discard()) break; else continue; }
                try
                {
                    if (choice == 6) { if (await Discard()) await Load(); continue; }
                    if (!model.CanEditMapPool)
                        throw new IOException("HCSL-MAPPOOL-BUSY: 请先停服并完成恢复/工具任务。");
                    var index = list.SelectedIndex;
                    switch (choice)
                    {
                        case 1:
                            var target = await MapTargetAsync(allowCollection: true);
                            if (target is not null) { draft.Add(target); Refresh(draft.Maps.Count - 1); }
                            break;
                        case 2: draft.Remove(index); Refresh(index); break;
                        case 3: Refresh(draft.Move(index, -1)); break;
                        case 4: Refresh(draft.Move(index, 1)); break;
                        case 5:
                            draft.Load(await model.MapPoolAsync(draft.SaveCommand(instance.Id, preset.Id, instance.Revision)));
                            Refresh(index); status.Text = "地图池已保存；未换图。全普通池生成地图组，单合集池由合集原生轮换，混排仍需手工换图。"; break;
                    }
                }
                catch (Exception e) when (e is IOException or InstanceException) { status.Text = e is InstanceException i ? i.Code + ": " + i.Message : e.Message; }
            }
        }
        finally { list.ItemsSource = null; }
    }
}
