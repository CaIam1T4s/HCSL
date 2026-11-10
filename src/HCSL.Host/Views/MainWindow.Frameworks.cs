using Avalonia.Controls;
using Avalonia.Threading;
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
            Text = "这里只管理框架文件（下载/更新到内容库），不会启用或部署。选择某个框架作为当前预设使用，请在「插件」页切换；不兼容按当前预设已启用的脚本框架判断。打开页面时会并行检查官方版本，未完成的卡片显示「更新检测中」。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var currentText = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cards = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        content.Children.Add(currentText); content.Children.Add(cards); content.Children.Add(status);

        PluginResult? plugins = null;
        string? current = null;
        string? busyId = null;
        var generation = 0;

        string? LocalVersion(PluginResult result, string id) =>
            result.Available.Where(p => p.Id == id && p.FrameworkPayload)
                .OrderByDescending(p => p.Version, Comparer<string>.Create((a, b) => FrameworkRules.CompareVersions(id, a, b)))
                .Select(p => p.Version).FirstOrDefault();

        async Task Refresh()
        {
            var gen = ++generation;
            plugins = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
            current = FrameworkSelection.Current(plugins);
            var metamod = plugins.Selected.FirstOrDefault(s => s.Id == "metamod" && s.Enabled);
            currentText.Text = (metamod is null ? "基础框架：Metamod:Source 未启用" : "基础框架：Metamod:Source " + metamod.Version + "（已启用）")
                + " · "
                + (current is null ? "脚本框架：未启用" : "脚本框架：" + FrameworkRules.Find(current)?.DisplayName + "（" + current + "）");
            cards.Children.Clear();
            var checks = new List<(FrameworkDefinition Framework, Action<FrameworkVersionInfo?> Apply)>();

            foreach (var framework in FrameworkRules.Known)
            {
                var script = framework.Kind == FrameworkKind.ScriptFramework;
                var incompatible = script && current is not null && current != framework.Id;
                var localInstalled = LocalVersion(plugins, framework.Id);
                var stateText = new TextBlock { Text = framework.Installable ? "更新检测中…" : "手动导入（不在内置安装目录）", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var note = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var download = new TKCode.Arc.Button { Content = framework.Installable ? "检测中" : "手动", Variant = ButtonVariant.Secondary, IsEnabled = false };
                var choose = new TKCode.Arc.Button
                {
                    Content = framework.Kind == FrameworkKind.Base ? "基础框架（必需）" : "检测中",
                    Variant = ButtonVariant.Secondary,
                    IsEnabled = false
                };
                var card = new Border { Classes = { "panel" }, Margin = new Avalonia.Thickness(0, 0, 10, 10), Padding = new Avalonia.Thickness(14), Width = 250 };
                var body = new StackPanel { Spacing = 6 };
                body.Children.Add(new TextBlock { Text = framework.DisplayName, FontSize = 16 });
                body.Children.Add(stateText);
                if (framework.Requires is not null) body.Children.Add(new TextBlock { Text = "需要 " + framework.Requires, Classes = { "muted" } });
                body.Children.Add(note);
                body.Children.Add(download); body.Children.Add(choose);
                if (incompatible) body.Children.Add(new TextBlock { Text = "切换前先在「插件」页停用当前框架，或直接在此选择目标框架（会自动禁用旧框架插件）。", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                card.Child = body; cards.Children.Add(card);

                async Task Install()
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
                download.Click += async (_, _) => await Install();

                async Task Select()
                {
                    if (busyId is not null || plugins is null) return;
                    try
                    {
                        var currentSelections = plugins.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
                        var choice = FrameworkSelection.Apply(currentSelections, framework.Id, plugins);
                        if (choice.MissingFramework is not null) { status.Text = "框架尚未安装：" + framework.Id; return; }
                        await model.PluginsAsync(new(PluginAction.Save, instance.Id, preset.Id, instance.Revision, plugins.Hash, choice.Selections));
                        status.Text = "已切换当前预设框架；旧的框架插件已禁用但保留。下次部署生效。"
                            + (choice.Warnings.Length > 0 ? " " + string.Join("；", choice.Warnings) : "");
                        await Refresh();
                    }
                    catch (Exception e) { status.Text = e.Message; }
                }
                choose.Click += async (_, _) => await Select();

                void Apply(FrameworkVersionInfo? info)
                {
                    if (gen != generation) return;
                    var installed = info?.InstalledVersion ?? localInstalled;
                    var latest = info?.LatestVersion;
                    var update = latest is not null && installed is not null && FrameworkRules.CompareVersions(framework.Id, latest, installed) > 0;
                    var installedPayload = plugins?.Available.Any(p => p.Id == framework.Id && p.FrameworkPayload) == true;
                    stateText.Text = !framework.Installable ? "手动导入（不在内置安装目录）"
                        : info?.Error is { } code ? code
                        : installed is null ? "未安装"
                        : update ? $"已安装 {installed} · 可更新 {latest}"
                        : $"已安装 {installed}";
                    note.Text = incompatible ? "与 " + FrameworkRules.Find(current)?.DisplayName + " 不兼容" : "";
                    download.Content = !framework.Installable ? "手动"
                        : installed is null ? "下载最新"
                        : update ? "更新到 " + latest
                        : "已安装";
                    download.IsEnabled = framework.Installable && !incompatible && info?.Error is null && (installed is null || update);
                    choose.Content = framework.Kind == FrameworkKind.Base ? "基础框架（必需）"
                        : !installedPayload ? "未安装，先下载"
                        : current == framework.Id ? "当前预设框架" : "设为当前预设框架";
                    choose.IsEnabled = installedPayload && framework.Kind == FrameworkKind.ScriptFramework && !incompatible && current != framework.Id;
                }

                if (!framework.Installable) { Apply(null); continue; }
                checks.Add((framework, Apply));
            }

            foreach (var (framework, apply) in checks)
            {
                _ = CheckOneAsync(framework, apply, gen);
            }

            async Task CheckOneAsync(FrameworkDefinition framework, Action<FrameworkVersionInfo?> apply, int gen)
            {
                try
                {
                    var result = await model.CheckFrameworkAsync(framework.Id);
                    var info = result.Frameworks.FirstOrDefault(f => f.Id == framework.Id);
                    Dispatcher.UIThread.Post(() => { if (gen == generation) apply(info); });
                }
                catch (Exception e)
                {
                    Dispatcher.UIThread.Post(() => { if (gen == generation) apply(new FrameworkVersionInfo(framework.Id, framework.DisplayName,
                        framework.Kind == FrameworkKind.Base, framework.Installable, null, null, null, false, framework.Requires, e.Message)); });
                }
            }
        }

        try
        {
            await Refresh();
            await ShowOverlayAsync(content, ["关闭"]);
            generation++;
        }
        catch (Exception e) { await ChooseAsync("框架页错误", e.Message, "返回"); }
    }

}
