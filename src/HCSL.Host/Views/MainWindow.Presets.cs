using Avalonia.Controls;
using Avalonia.Layout;
using HCSL.Core;
using TKCode.Arc;

namespace HCSL.Host.Views;

public partial class MainWindow
{
    public async Task<bool> EditPresetAsync(string title, PresetDefinition? original, bool nameOnly,
        Func<PresetDefinition, Task<string?>> save)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 20 });
        var name = new Input { Text = nameOnly ? (original?.Name ?? "") + " 副本" : original?.Name ?? "", PlaceholderText = "预设名称（1–80 字符）" };
        var map = new Input { Text = original?.Map ?? "de_dust2", PlaceholderText = "地图名或单张工坊 ID", Width = 300 };
        var mapKind = new Select { ItemsSource = new[] { "普通地图", "工坊地图 ID" }, SelectedIndex = (int)(original?.MapKind ?? ServerMapKind.Standard), Width = 140 };
        var type = new Input { Text = (original?.GameType ?? 0).ToString() };
        var mode = new Input { Text = (original?.GameMode ?? 1).ToString() };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        void Field(string label, Control field) { content.Children.Add(new TextBlock { Text = label }); content.Children.Add(field); }
        Field("预设名称", name);
        if (!nameOnly)
        {
            var mapRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            mapRow.Children.Add(mapKind); mapRow.Children.Add(map);
            Field("启动地图：来源 / 名称或 ID", mapRow);
            Field("game_type（0–10）", type); Field("game_mode（0–100）", mode);
        }
        content.Children.Add(new TextBlock { Text = nameOnly ? "复制配置及 CFG 等预设文件，不复制实例入服密码；新副本不会自动选中。" :
            "保存后下次启动按需部署；不会立即换图。VAC 偏好保留原值，实际 VAC 仍关闭。工坊仅接受单张地图 ID；可用性与下载结果由 CS2 验证。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(error);
        while (await ShowOverlayAsync(content, ["取消", "保存"]) == 1)
        {
            try
            {
                if (!int.TryParse(type.Text, out var gameType) || !int.TryParse(mode.Text, out var gameMode))
                    throw new InstanceException("HCSL-PRESET-INVALID", "game_type / game_mode 必须为整数。");
                var draft = (original ?? new PresetDefinition(1, Guid.Empty, 1, "")) with
                { Name = (name.Text ?? "").Trim(), Map = (map.Text ?? "").Trim(), MapKind = (ServerMapKind)mapKind.SelectedIndex, GameType = gameType, GameMode = gameMode };
                InstanceRules.Preset(draft);
                var failure = await save(draft);
                if (failure is null) return true;
                error.Text = failure;
            }
            catch (InstanceException e) { error.Text = e.Code + ": " + e.Message; }
        }
        return false;
    }
}
