using Avalonia.Controls;
using Avalonia.Layout;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace HCSL.Host.Views;

public partial class MainWindow
{
    private sealed record VersionChoice(string Version, FrameworkReleaseEntry? Release);

    internal async Task ShowFrameworkVersionsAsync(HostServerController model, InstanceDefinition instance,
        PresetDefinition preset, FrameworkDefinition framework)
    {
        try
        {
            var plugins = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
            var current = plugins.Selected.FirstOrDefault(item => item.Id == framework.Id && item.Enabled)?.Version;
            var local = plugins.Available.Where(p => p.Id == framework.Id && p.FrameworkPayload).Select(p => p.Version).ToHashSet();
            var versions = new Dictionary<string, FrameworkReleaseEntry>(StringComparer.Ordinal);
            FrameworkCatalogResult? catalog = null;
            var list = new StackPanel { Spacing = 4 };
            var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            ArcTokens.Bind(status, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            var filters = new FilterToolbar { IsSearchVisible = true, SearchPlaceholder = "搜索版本", Label = "版本筛选" };
            var refresh = new ArcButton { Content = "刷新", Variant = ButtonVariant.Secondary, IsVisible = framework.Installable };
            filters.Actions = refresh;
            var more = new ArcButton { Content = "加载更多", Variant = ButtonVariant.Secondary, HorizontalAlignment = HorizontalAlignment.Center };
            var editor = new ArcButton { Content = "管理已安装框架", Variant = ButtonVariant.Secondary };
            Dialog.SetResult(editor, "manage");
            var body = new StackPanel { Width = 440, Spacing = 8 };
            body.Children.Add(filters);
            body.Children.Add(status);
            body.Children.Add(new ScrollViewer { MaxHeight = 360, Content = list });
            body.Children.Add(more);
            if (current is not null || !framework.Installable) body.Children.Add(editor);
            var dialog = new Dialog { Title = "选择 " + framework.DisplayName + " 版本", Content = body, Size = ControlSize.Large };
            var loading = false;
            var closed = false;

            void Render()
            {
                var query = (filters.SearchText ?? "").Trim();
                list.Children.Clear();
                foreach (var version in FrameworkRules.OrderVersions(framework.Id, versions.Keys.Concat(local).Distinct())
                    .Where(v => v.Contains(query, StringComparison.OrdinalIgnoreCase)))
                {
                    versions.TryGetValue(version, out var release);
                    var row = new ArcButton { Variant = ButtonVariant.Ghost, HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        IsEnabled = model.CanEditPlugins && (local.Contains(version) || release?.Downloadable == true) };
                    var icon = new LucideIcon { Kind = LucideIconKind.ArrowRight, VerticalAlignment = VerticalAlignment.Center };
                    var details = new StackPanel { Spacing = 2 };
                    details.Children.Add(new TextBlock { Text = version });
                    var description = version == current ? "当前预设版本" : local.Contains(version) ? "已安装"
                        : release?.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
                    var note = new TextBlock { Text = description, IsVisible = description.Length > 0 };
                    ArcTokens.Bind(note, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
                    details.Children.Add(note);
                    var line = new DockPanel();
                    DockPanel.SetDock(icon, Dock.Right);
                    line.Children.Add(icon);
                    line.Children.Add(details);
                    row.Content = line;
                    Dialog.SetResult(row, new VersionChoice(version, release));
                    list.Children.Add(row);
                }
                more.IsVisible = catalog is { } page && (versions.Count < page.TotalCached || page.NextPage is not null);
                more.IsEnabled = refresh.IsEnabled = !loading;
            }

            async Task LoadAsync(FrameworkCatalogCommand command, bool reset = false)
            {
                if (loading || closed) return;
                loading = true;
                status.Text = "正在读取版本…";
                Render();
                try
                {
                    var page = await model.FrameworkCatalogAsync(command);
                    if (closed) return;
                    if (reset) versions.Clear();
                    catalog = page;
                    local.UnionWith(page.InstalledVersions);
                    foreach (var release in page.Versions) versions[release.Version] = release;
                    status.Text = page.Error ?? (versions.Count == 0 && local.Count == 0 ? "暂无可用版本" : "");
                }
                catch (IOException e)
                {
                    if (!closed)
                    {
                        status.Text = "版本读取失败，可刷新重试。";
                        ShowInstanceNotification("无法读取框架版本", e.Message);
                    }
                }
                finally { loading = false; if (!closed) Render(); }
            }

            filters.FiltersChanged += (_, _) => Render();
            refresh.Click += async (_, _) => await LoadAsync(new(framework.Id, Refresh: true), reset: true);
            more.Click += async (_, _) =>
            {
                if (catalog is not { } page) return;
                await LoadAsync(versions.Count < page.TotalCached
                    ? new(framework.Id, CacheOnly: true, Offset: versions.Count)
                    : new(framework.Id, Page: page.NextPage, Offset: versions.Count));
            };
            Render();
            async Task InitialAsync()
            {
                if (!framework.Installable) return;
                await LoadAsync(new(framework.Id, CacheOnly: true));
                if (catalog?.NeedsRefresh == true) await LoadAsync(new(framework.Id, Refresh: true), reset: true);
            }
            var showing = dialog.ShowAsync(this);
            _ = InitialAsync();
            var chosen = await showing;
            closed = true;
            if (chosen is "manage") { await EditFrameworksAsync(model, instance, preset, framework.Id); return; }
            if (chosen is not VersionChoice choice || choice.Version == current || !model.CanEditPlugins) return;
            if (!await ConfirmFrameworkVersionAsync(framework, preset, current, choice.Version)) return;
            if (!local.Contains(choice.Version))
            {
                if (choice.Release?.Downloadable != true) return;
                await model.InstallFrameworkAsync(framework.Id, choice.Version);
            }
            var saved = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
            if (saved.Hash != plugins.Hash || saved.Revision != plugins.Revision)
                throw new IOException("预设插件清单已变更，请重新打开版本列表确认。");
            if (model.Selected?.Entry.Definition is not { } target || target.Id != instance.Id ||
                target.Revision != instance.Revision || !model.CanEditPlugins) return;
            var selections = saved.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray();
            PluginSelection[] next;
            if (framework.Kind == FrameworkKind.Base)
            {
                if (!saved.Available.Any(p => p.Id == framework.Id && p.Version == choice.Version && p.FrameworkPayload))
                    throw new IOException("所选版本未安装。");
                next = [new(framework.Id, choice.Version, true), .. selections.Where(s => s.Id != framework.Id)];
            }
            else
            {
                var change = FrameworkSelection.Apply(selections, framework.Id, saved, choice.Version);
                if (change.MissingFramework is not null || change.Warnings.Length > 0)
                    throw new IOException(string.Join("；", change.Warnings.DefaultIfEmpty("所选版本未安装。")));
                next = change.Selections;
            }
            await model.PluginsAsync(new(PluginAction.Save, instance.Id, preset.Id, instance.Revision, saved.Hash, next));
            ShowInstanceNotification("已更新预设框架", choice.Version + " · 下次部署生效", ToastKind.Success);
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
        { ShowInstanceNotification("无法更换框架版本", e.Message); }
    }

    private async Task<bool> ConfirmFrameworkVersionAsync(FrameworkDefinition framework, PresetDefinition preset, string? current, string target)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Classes.Add("dialog-actions");
        var cancel = new ArcButton { Content = "取消", Variant = ButtonVariant.Secondary };
        var confirm = new ArcButton { Content = "确认", Variant = ButtonVariant.Primary };
        confirm.Classes.Add("dialog-primary");
        Dialog.SetResult(cancel, false);
        Dialog.SetResult(confirm, true);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        var dialog = new Dialog { Title = current is null ? "启用框架" : "更换框架版本", Actions = actions,
            Description = $"预设「{preset.Name}」：{framework.DisplayName} {current ?? "未启用"} → {target}。切换脚本框架时会停用旧框架及其插件。" };
        return await dialog.ShowAsync(this) is true;
    }
}
