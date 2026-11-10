using Avalonia.Controls;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    /// <summary>Library-level framework install/update page; it never changes the preset selection.</summary>
    public async Task EditFrameworksAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "框架 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "这里只管理框架文件（下载/更新到内容库），不会启用或部署。选择某个框架作为当前预设使用，请在「插件」页切换；不兼容按当前预设已启用的脚本框架判断。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var currentText = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cards = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        content.Children.Add(currentText); content.Children.Add(cards); content.Children.Add(status);

        PluginResult? plugins = null;
        string? current = null;
        string? busyId = null;

        async Task Refresh()
        {
            plugins = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
            var check = await model.CheckFrameworksAsync();
            current = FrameworkSelection.Current(plugins);
            currentText.Text = current is null
                ? "当前预设未启用脚本框架。"
                : "当前预设脚本框架：" + FrameworkRules.Find(current)?.DisplayName + "（" + current + "）";
            cards.Children.Clear();
            foreach (var framework in FrameworkRules.Known)
            {
                var info = check.Frameworks.FirstOrDefault(f => f.Id == framework.Id);
                var installed = info?.InstalledVersion;
                var latest = info?.LatestVersion;
                var script = framework.Kind == FrameworkKind.ScriptFramework;
                var incompatible = script && current is not null && current != framework.Id;
                var installedPayload = plugins.Available.Any(p => p.Id == framework.Id && p.FrameworkPayload);
                var latestAvailable = plugins.Available.Where(p => p.Id == framework.Id && p.FrameworkPayload)
                    .OrderByDescending(p => p.Version, Comparer<string>.Create((a, b) => FrameworkRules.CompareVersions(framework.Id, a, b))).FirstOrDefault();
                var card = new Border { Classes = { "panel" }, Margin = new Avalonia.Thickness(0, 0, 10, 10), Padding = new Avalonia.Thickness(14), Width = 250 };
                var body = new StackPanel { Spacing = 6 };
                body.Children.Add(new TextBlock { Text = framework.DisplayName, FontSize = 16 });
                var stateText = !framework.Installable ? "手动导入（不在内置安装目录）"
                    : info?.Error is { } code ? code
                    : installed is null ? "未安装"
                    : latest is not null && FrameworkRules.CompareVersions(framework.Id, latest, installed) > 0 ? $"已安装 {installed} · 可更新 {latest}"
                    : $"已安装 {installed}";
                body.Children.Add(new TextBlock { Text = stateText, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                if (incompatible) body.Children.Add(new TextBlock { Text = "与 " + FrameworkRules.Find(current)?.DisplayName + " 不兼容", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                if (framework.Requires is not null) body.Children.Add(new TextBlock { Text = "需要 " + framework.Requires, Classes = { "muted" } });

                async Task InstallAsync()
                {
                    if (busyId is not null) return;
                    busyId = framework.Id; status.Text = "安装中… " + framework.DisplayName; cards.IsEnabled = false;
                    try
                    {
                        var result = await model.InstallFrameworkAsync(framework.Id);
                        status.Text = $"已安装 {result.Id} {result.Version}（{result.Files} 文件）；不会自动启用或部署。";
                        await Refresh();
                    }
                    catch (Exception e) { status.Text = e.Message; }
                    finally { busyId = null; cards.IsEnabled = true; }
                }
                async Task SelectAsync()
                {
                    if (busyId is not null || plugins is null) return;
                    try
                    {
                        var currentSelections = plugins.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
                        var choice = FrameworkSelection.Apply(currentSelections, framework.Id, plugins);
                        if (choice.MissingFramework is not null) { status.Text = "框架尚未安装：" + framework.Id; return; }
                        var saved = await model.PluginsAsync(new(PluginAction.Save, instance.Id, preset.Id, instance.Revision, plugins.Hash, choice.Selections));
                        status.Text = "已切换当前预设框架；旧的框架插件已禁用但保留。下次部署生效。"
                            + (choice.Warnings.Length > 0 ? " " + string.Join("；", choice.Warnings) : "");
                        await Refresh();
                    }
                    catch (Exception e) { status.Text = e.Message; }
                }

                var download = new TKCode.Arc.Button
                {
                    Content = !framework.Installable ? "手动" : installed is null ? "下载最新" : latest is not null && FrameworkRules.CompareVersions(framework.Id, latest, installed) > 0 ? "更新到 " + latest : "已安装",
                    Variant = ButtonVariant.Secondary,
                    IsEnabled = framework.Installable && !incompatible && info?.Error is null
                };
                download.Click += async (_, _) => await InstallAsync();
                body.Children.Add(download);
                var choose = new TKCode.Arc.Button
                {
                    Content = installedPayload ? "设为当前预设框架" : "未安装，先下载",
                    Variant = ButtonVariant.Secondary,
                    IsEnabled = installedPayload && !incompatible && current != framework.Id
                };
                choose.Click += async (_, _) =>
                {
                    if (!installedPayload) { await InstallAsync(); return; }
                    await SelectAsync();
                };
                body.Children.Add(choose);
                if (incompatible) body.Children.Add(new TextBlock { Text = "切换前先在「插件」页停用当前框架，或直接在此选择目标框架（会自动禁用旧框架插件）。", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                card.Child = body; cards.Children.Add(card);
            }
        }

        try
        {
            await Refresh();
            await ShowOverlayAsync(content, ["关闭"]);
        }
        catch (Exception e) { await ChooseAsync("框架页错误", e.Message, "返回"); }
    }
}
