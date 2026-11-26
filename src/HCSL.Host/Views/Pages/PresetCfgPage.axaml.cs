using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

public partial class PresetCfgPage : UserControl
{
    private sealed record Node(PresetCfgFile File, string Path, Node[]? Children);
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private int generation;
    private HostServerController? subscribed;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private bool executing;
    private Node[] selected = [];

    public PresetCfgPage()
    {
        InitializeComponent();
        TreeTableHeaderAlignment.Attach(CfgTree);
        // Arc 1.1 keeps its row type internal. Override only the CFG table's selected row fill;
        // its checkbox and selection toolbar still show which files are selected.
        if (typeof(TreeTable).Assembly.GetType("TKCode.Arc.TreeTableRow") is { } rowType)
            CfgTree.Styles.Add(new Style(selector => selector.OfType(rowType).Class(":selected"))
            {
                Setters = { new Setter(Panel.BackgroundProperty, Brushes.Transparent) }
            });
        CfgTree.KeySelector = item => ((Node)item).Path;
        CfgTree.RowLabelSelector = item => ((Node)item).File.Name;
        CfgTree.ChildrenSelector = item => ((Node)item).Children;
        CfgTree.Columns.Add(TreeTableColumn.Create<Node>("名称", node => node.File.Name));
        CfgTree.Columns.Add(new TreeTableColumn { Header = "类型", Width = new Avalonia.Controls.GridLength(112),
            ValueSelector = item => ((Node)item).File.Kind == PresetCfgKind.Directory ? "目录" : "CFG" });
        CfgTree.SelectionChanged += (_, e) =>
        {
            selected = e.SelectedItems.OfType<Node>().Where(node => node.File.Kind == PresetCfgKind.File)
                .OrderBy(node => node.Path, StringComparer.Ordinal).ToArray();
            Selection.SelectedCount = selected.Length;
            UpdateActions();
        };
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += async (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) await LoadAsync();
            else generation++;
        };
    }

    private (HostServerController Servers, InstanceDefinition Instance, PresetDefinition Preset)? Scope()
    {
        if (DataContext is ShellViewModel { ManagedPresetId: { } id } shell &&
            shell.Servers.Selected?.Entry is { Definition: { } instance } entry &&
            entry.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            return (shell.Servers, instance, preset);
        return null;
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = (DataContext as ShellViewModel)?.Servers;
        if (subscribed is not null) subscribed.PropertyChanged += ServerChanged;
        UpdateActions();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= ServerChanged;
        subscribed = null;
    }

    private void ServerChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(UpdateActions);

    private void UpdateActions()
    {
        var action = Selection.Actions.FirstOrDefault(item => item.Key == "exec");
        var enabled = selected.Length > 0 && !executing && Scope() is { } scope
            && scope.Servers.CanExecCfg(scope.Instance.Id, scope.Preset.Id);
        if (action is null)
            Selection.Actions.Add(new SelectionToolbarAction { Key = "exec", Label = "执行 exec", IsEnabled = enabled });
        else
            action.IsEnabled = enabled;
    }

    private void ClearSelection(object? sender, RoutedEventArgs e) => CfgTree.SelectedKeys = [];

    private void SelectAll(object? sender, RoutedEventArgs e) =>
        CfgTree.SelectedKeys = Keys(CfgTree.ItemsSource?.Cast<Node>() ?? []).ToArray();

    private static IEnumerable<object> Keys(IEnumerable<Node> nodes) =>
        nodes.SelectMany(node => Keys(node.Children ?? []).Prepend(node.Path));

    private bool syncingPlugins;

    private async void SyncPlugins(object? sender, RoutedEventArgs e)
    {
        if (syncingPlugins || Scope() is not { } scope || Owner is not { } owner) return;
        syncingPlugins = true;
        try
        {
            var (model, instance, preset) = scope;
            var result = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id, PackageMetadataOnly: true));
            var enabled = result.Selected.Where(p => p.Enabled && p.Available).Select(p => new PluginSelection(p.Id, p.Version)).ToArray();
            var preview = await model.PreviewPluginCfgAsync(instance.Id, preset.Id, instance.Revision, enabled, []);
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = $"新增 {preview.Import.Length} 个 · 相同 {preview.Identical.Length} 个 · 冲突 {preview.Conflicts.Length} 个" });
            foreach (var file in preview.Import) body.Children.Add(new TextBlock { Text = file.Path, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var conflicts = preview.Conflicts.Select(file => (file.Path, Box: new TKCode.Arc.Checkbox
                { Content = "覆盖 " + file.Path, IsChecked = false })).ToArray();
            foreach (var conflict in conflicts) body.Children.Add(conflict.Box);
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
            actions.Classes.Add("dialog-actions");
            var cancel = new TKCode.Arc.Button { Content = "关闭", Variant = ButtonVariant.Secondary };
            Dialog.SetResult(cancel, false);
            actions.Children.Add(cancel);
            if (model.CanEditPlugins && (preview.Import.Length > 0 || conflicts.Length > 0))
            {
                var apply = new TKCode.Arc.Button { Content = "同步", Variant = ButtonVariant.Primary };
                apply.Classes.Add("dialog-primary");
                Dialog.SetResult(apply, true);
                actions.Children.Add(apply);
            }
            var dialog = new Dialog { Title = "从插件同步 CFG", Content = new ScrollViewer { Content = body, MaxHeight = 360 }, Actions = actions };
            if (await dialog.ShowAsync(this) is not true || Scope() is not { } active || active != scope) return;
            var latest = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id, PackageMetadataOnly: true));
            if (latest.Hash != result.Hash) throw new IOException("插件启用列表已变化，请重新预览。");
            var applied = await model.ApplyPluginCfgAsync(instance.Id, preset.Id, instance.Revision, enabled,
                conflicts.Where(c => c.Box.IsChecked == true).Select(c => c.Path).ToArray());
            owner.ShowInstanceNotification("CFG 同步完成", $"新增 {applied.Imported} 个，覆盖 {applied.Overwritten} 个。", ToastKind.Success);
            if (Scope() == scope) await LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or InstanceException or InvalidOperationException)
        { owner.ShowInstanceNotification("无法同步插件 CFG", ex.Message); }
        finally { syncingPlugins = false; }
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var ticket = ++generation;
        CfgTree.ItemsSource = null;
        CfgTree.SelectedKeys = Array.Empty<object>();
        CfgTree.ExpandedKeys = Array.Empty<object>();
        selected = [];
        Selection.SelectedCount = 0;
        UpdateActions();
        if (Scope() is not { } scope) return;
        var remaining = 64;
        var truncated = false;
        bool Current() => generation == ticket && Scope()?.Preset.Id == scope.Preset.Id;
        await loadGate.WaitAsync();
        try
        {
            for (var i = 0; i < 40 && Current() && !scope.Servers.Server.Connected; i++)
                await Task.Delay(125);
            if (!Current()) return;
            var roots = await ReadAsync("");
            if (!Current()) return;
            CfgTree.ItemsSource = roots;
            if (truncated) Owner?.ShowInstanceNotification("CFG 列表未完整显示", "其余文件可从基本信息打开 CFG 目录查看。");
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
        { if (Current()) Owner?.ShowInstanceNotification("无法读取 CFG", e.Message); }
        finally { loadGate.Release(); }

        async Task<Node[]> ReadAsync(string path)
        {
            if (!Current()) return [];
            if (remaining-- <= 0) { truncated = true; return []; }
            var result = await scope.Servers.CfgAsync(new(PresetCfgAction.List, scope.Instance.Id,
                scope.Preset.Id, scope.Instance.Revision, Path: path));
            truncated |= result.Truncated;
            var nodes = new List<Node>();
            foreach (var entry in result.Files)
            {
                var relative = entry.RelativePath ?? (path.Length == 0 ? entry.Name : path + "/" + entry.Name);
                nodes.Add(new(entry, relative, entry.Kind == PresetCfgKind.Directory ? await ReadAsync(relative) : null));
            }
            return [.. nodes];
        }
    }

    private static Control CfgDialogActions()
    {
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        actions.Classes.Add("dialog-actions");
        Add("取消", false, ButtonVariant.Secondary);
        Add("执行", true, ButtonVariant.Primary);
        return actions;

        void Add(string label, bool result, ButtonVariant variant)
        {
            var button = new TKCode.Arc.Button { Content = label, Variant = variant };
            if (variant == ButtonVariant.Primary) button.Classes.Add("dialog-primary");
            Dialog.SetResult(button, result);
            actions.Children.Add(button);
        }
    }

    private async void Exec(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (executing || selected.Length == 0 || Scope() is not { } scope || Owner is not { } owner) return;
        var files = selected;
        var written = 0;
        var submitted = false;
        if (files.Length > 32)
        {
            owner.ShowInstanceNotification("一次最多执行 32 个 CFG 文件", kind: ToastKind.Warning);
            return;
        }
        var (servers, instance, preset) = scope;
        if (!servers.CanExecCfg(instance.Id, preset.Id) || servers.Server.RunId is not { } run)
        {
            owner.ShowInstanceNotification("无法执行 CFG", "运行中的实例和预设必须与此页面一致。");
            return;
        }
        executing = true;
        UpdateActions();
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var read = await servers.CfgAsync(new(PresetCfgAction.Read, instance.Id, preset.Id, instance.Revision, file.Path));
                hashes.Add(file.Path, read.Hash ?? throw new IOException($"无法确认文件版本：{file.Path}"));
            }
            var order = new ObservableCollection<ReorderableListEntry>(files.Select(file =>
                new ReorderableListEntry { Id = file.Path, Title = file.Path }));
            var list = new ReorderableList { ItemsSource = order };
            var content = new Border { MaxWidth = 480, Padding = new Avalonia.Thickness(16, 16, 16, 24), Child = list };
            // Short lists do not need a clipping viewport. Long lists retain scrolling with room for lift.
            var dialog = new Dialog
            {
                Title = "按顺序执行 CFG",
                Content = order.Count <= 3 ? content : new ScrollViewer { MaxHeight = 360, Content = content },
                Actions = CfgDialogActions()
            };
            if (await dialog.ShowAsync(this) is not true) return;
            var batch = order.Select(file => new CfgBatchFile(file.Id, hashes[file.Id])).ToArray();
            submitted = true;
            var receipts = await servers.DeliverCfgBatchAsync(new(run, instance.Id, preset.Id, batch));
            written = receipts.TakeWhile(r => r.RunId == run && r.Delivery == ConsoleDelivery.Written).Count();
            if (written != batch.Length)
            {
                owner.ShowInstanceNotification($"CFG 投递已停止（确认写入 {written}/{batch.Length} 条）",
                    receipts.ElementAtOrDefault(written)?.Code ?? "未收到完整回执。");
                return;
            }
            owner.ShowInstanceNotification($"已写入 {written} 条 CFG 命令", "写入不代表执行成功，请以服务器日志为准。", ToastKind.Info);
        }
        catch (Exception ex) when (ex is IOException or InstanceException or InvalidOperationException)
        { owner.ShowInstanceNotification(submitted ? "CFG 批量请求未完成，请查看服务器日志" : "无法准备 CFG 批量请求", ex.Message); }
        finally { executing = false; UpdateActions(); }
    }
}
