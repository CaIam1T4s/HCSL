using Avalonia.Controls;
using Avalonia.Layout;
using HCSL.Core;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    public async Task<ServerMapTarget?> MapTargetAsync(bool allowCollection = false)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = allowCollection ? "添加地图池条目" : "即时换图", FontSize = 20 });
        var kind = new Select { ItemsSource = allowCollection ? new[] { "普通地图", "工坊地图 ID", "工坊合集 ID" } : new[] { "普通地图", "工坊地图 ID" }, SelectedIndex = 0 };
        var value = new Input { Text = "de_dust2", PlaceholderText = allowCollection ? "普通地图名 / 单张工坊地图 ID / 合集 ID" : "普通地图名 / 单张工坊地图 ID" };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        body.Children.Add(kind); body.Children.Add(value);
        body.Children.Add(new TextBlock { Text = allowCollection ? "工坊合集必须单独占用整个图池，不能与普通/单张工坊地图混排。保存后由合集提供原生轮换。" : "仅改变当前运行会话，不保存启动预设。不自动关闭工坊脚本保护。工坊下载可能耗时/失败，请查看日志。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(error);
        while (await ShowOverlayAsync(body, ["取消", "下一步"]) == 1)
        {
            var target = new ServerMapTarget((ServerMapKind)kind.SelectedIndex, (value.Text ?? "").Trim());
            try { ServerMapRules.Validate(target); return target; }
            catch (InstanceException e) { error.Text = e.Code + ": " + e.Message; }
        }
        return null;
    }
}
