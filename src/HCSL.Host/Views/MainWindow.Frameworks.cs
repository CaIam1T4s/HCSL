using Avalonia.Controls;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    /// <summary>Library-level framework install/update page; it never changes the preset selection.</summary>
    public async Task EditFrameworksAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset, string? frameworkId = null)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "框架 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "下载和更新写入内容库；启用或停用只修改当前预设，下次部署生效。",
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
            FrameworkRules.OrderVersions(id, result.Available.Where(p => p.Id == id && p.FrameworkPayload).Select(p => p.Version)).FirstOrDefault();

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

            foreach (var framework in FrameworkRules.Known.Where(f => frameworkId is null || f.Id == frameworkId))
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
                if (incompatible) body.Children.Add(new TextBlock { Text = "切换到此框架会停用原脚本框架及其插件。", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                card.Child = body; cards.Children.Add(card);

                async Task Install()
                {
                    if (busyId is not null) return;
                    busyId = framework.Id; status.Text = "安装中… " + framework.DisplayName; cards.IsEnabled = false;
                    try
                    {
                        var wasCurrent = current == framework.Id;
                        var result = await model.InstallFrameworkAsync(framework.Id);
                        var text = $"已安装 {result.Id} {result.Version}（{result.Files} 文件）；不会自动启用或部署。";
                        if (wasCurrent)
                        {
                            var latest = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
                            var selections = latest.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
                            var choice = FrameworkSelection.Apply(selections, framework.Id, latest);
                            if (choice.MissingFramework is null)
                            {
                                await model.PluginsAsync(new(PluginAction.Save, instance.Id, preset.Id, instance.Revision, latest.Hash, choice.Selections));
                                text += " 当前预设已切换到 " + result.Version + "；需重新部署。旧版本仍保留在内容库。";
                            }
                        }
                        status.Text = text;
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
                        var selected = currentSelections.FirstOrDefault(s => s.Id == framework.Id && s.Enabled);
                        FrameworkChoice choice;
                        if (framework.Kind == FrameworkKind.Base)
                        {
                            if (selected is not null && currentSelections.Any(s => s.Enabled && FrameworkRules.Find(s.Id)?.Requires == framework.Id))
                            { status.Text = "请先停用依赖此基础框架的脚本框架。"; return; }
                            var version = selected?.Version ?? LocalVersion(plugins, framework.Id);
                            if (version is null) return;
                            choice = new([.. currentSelections.Where(s => s.Id != framework.Id), new(framework.Id, version, selected is null)], null, []);
                        }
                        else
                        {
                            var disable = selected is not null && selected.Version == LocalVersion(plugins, framework.Id);
                            choice = FrameworkSelection.Apply(currentSelections, disable ? null : framework.Id, plugins);
                        }
                        if (choice.MissingFramework is not null) { status.Text = "框架尚未安装：" + framework.Id; return; }
                        await model.PluginsAsync(new(PluginAction.Save, instance.Id, preset.Id, instance.Revision, plugins.Hash, choice.Selections));
                        status.Text = "已更新当前预设框架，下次部署生效。"
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
                    var selectedVersion = plugins?.Selected.FirstOrDefault(s => s.Id == framework.Id && s.Enabled)?.Version;
                    var currentVersion = string.Equals(selectedVersion, installed, StringComparison.Ordinal);
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
                    choose.Content = framework.Kind == FrameworkKind.Base && selectedVersion is not null || currentVersion && installedPayload
                        ? "停用" : !installedPayload ? "未安装，先下载" : incompatible ? "切换到此框架" : "启用此框架";
                    choose.IsEnabled = model.CanEditPlugins && (installedPayload || selectedVersion is not null && framework.Kind == FrameworkKind.Base)
                        && framework.Kind is FrameworkKind.Base or FrameworkKind.ScriptFramework;
                    note.Text = incompatible ? "" : OldVersionsText(framework, installed);
                }

                if (!framework.Installable) { Apply(null); continue; }
                checks.Add((framework, Apply));
            }

            foreach (var (framework, apply) in checks)
            {
                _ = CheckOneAsync(framework, apply, gen);
            }

            string OldVersionsText(FrameworkDefinition framework, string? installed)
            {
                if (plugins is null) return "";
                var selected = plugins.Selected.Where(s => s.Id == framework.Id).Select(s => s.Version).ToHashSet(StringComparer.Ordinal);
                var highest = FrameworkRules.OrderVersions(framework.Id, plugins.Available.Where(p => p.Id == framework.Id).Select(p => p.Version)).FirstOrDefault();
                var old = plugins.Available.Where(p => p.Id == framework.Id && !selected.Contains(p.Version) && p.Version != highest)
                    .Select(p => p.Version).Distinct().Order(StringComparer.Ordinal).ToArray();
                return old.Length == 0 ? "" : "旧版本：" + string.Join(", ", old) + "（可在「清理旧版本」中删除）";
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

        async Task CleanupOldVersions()
        {
            if (plugins is null) return;
            var selected = plugins.Selected.Select(s => s.Id + "\0" + s.Version).ToHashSet(StringComparer.Ordinal);
            var targets = new List<(string Id, string Version, long Bytes)>();
            foreach (var framework in FrameworkRules.Known.Where(f => frameworkId is null || f.Id == frameworkId))
            {
                var highest = FrameworkRules.OrderVersions(framework.Id, plugins.Available.Where(p => p.Id == framework.Id).Select(p => p.Version)).FirstOrDefault();
                foreach (var package in plugins.Available.Where(p => p.Id == framework.Id))
                    if (!selected.Contains(package.Id + "\0" + package.Version) && package.Version != highest)
                        targets.Add((package.Id, package.Version, package.Bytes));
            }
            if (targets.Count == 0) { status.Text = "没有可清理的旧框架版本。"; return; }
            var description = string.Join("\n", targets.Select(t => t.Id + " " + t.Version + " · " + (t.Bytes / 1024 / 1024) + " MiB"));
            if (await ChooseAsync("清理旧框架版本", "只删除未被当前预设选中、且不是最高版本的框架包。\n被其他预设引用时会拒绝。\n\n" + description, "取消", "删除") != 1) return;
            var removed = 0; var errors = new List<string>();
            foreach (var target in targets)
            {
                try { await model.RemovePluginPackageAsync(target.Id, target.Version); removed++; }
                catch (Exception e) { errors.Add(target.Id + " " + target.Version + ": " + e.Message); }
            }
            status.Text = "已删除 " + removed + " 个旧版本。" + (errors.Count == 0 ? "" : " 未删除：" + string.Join("；", errors.Take(3)));
            await Refresh();
        }

        try
        {
            await Refresh();
            while (!windowClosed)
            {
                var choice = await ShowOverlayAsync(content, ["关闭", "清理旧版本"]);
                if (choice <= 0) break;
                if (choice == 1) await CleanupOldVersions();
            }
            generation++;
        }
        catch (Exception e) { await ChooseAsync("框架页错误", e.Message, "返回"); }
    }

}
