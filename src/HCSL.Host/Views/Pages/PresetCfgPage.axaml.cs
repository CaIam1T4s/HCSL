using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private bool executing;
    private Node[] selected = [];

    public PresetCfgPage()
    {
        InitializeComponent();
        TreeTableHeaderAlignment.Attach(CfgTree);
        CfgTree.KeySelector = item => ((Node)item).Path;
        CfgTree.RowLabelSelector = item => ((Node)item).File.Name;
        CfgTree.ChildrenSelector = item => ((Node)item).Children;
        CfgTree.Columns.Add(TreeTableColumn.Create<Node>("名称", node => node.File.Name));
        CfgTree.Columns.Add(new TreeTableColumn { Header = "类型", Width = new Avalonia.Controls.GridLength(112),
            ValueSelector = item => ((Node)item).File.Kind == PresetCfgKind.Directory ? "目录" : "CFG" });
        Selection.Actions.Add(new SelectionToolbarAction { Key = "exec", Label = "执行 exec", IsEnabled = false });
        CfgTree.SelectionChanged += (_, e) =>
        {
            selected = e.SelectedItems.OfType<Node>().Where(node => node.File.Kind == PresetCfgKind.File)
                .OrderBy(node => node.Path, StringComparer.Ordinal).ToArray();
            Selection.SelectedCount = e.SelectedItems.Count();
            UpdateActions();
        };
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

    private void UpdateActions()
    {
        Selection.Actions[0].IsEnabled = selected.Length > 0 && !executing;
    }

    private void ClearSelection(object? sender, RoutedEventArgs e) => CfgTree.SelectedKeys = [];

    private void SelectAll(object? sender, RoutedEventArgs e) =>
        CfgTree.SelectedKeys = Keys(CfgTree.ItemsSource?.Cast<Node>() ?? []).ToArray();

    private static IEnumerable<object> Keys(IEnumerable<Node> nodes) =>
        nodes.SelectMany(node => Keys(node.Children ?? []).Prepend(node.Path));

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
            for (var i = 0; i < 40 && Current() && (scope.Servers.Busy || !scope.Servers.Server.Connected); i++)
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

    private async void Exec(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (executing || selected.Length == 0 || Scope() is not { } scope || Owner is not { } owner) return;
        var files = selected;
        var written = 0;
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
            var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
            content.Children.Add(new TextBlock
            {
                Text = $"{instance.Name} / {preset.Name}\n拖动调整顺序，命令将从上到下逐条投递。CFG 可能换图或停止服务器。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            content.Children.Add(new ScrollViewer { MaxHeight = 300, Content = new ReorderableList { ItemsSource = order } });
            if (await Dialog.ShowAsync(this, "按顺序执行 CFG", content, null,
                new DialogAction("取消", false), new DialogAction("执行", true, ButtonVariant.Primary)) is not true) return;
            foreach (var file in order.ToArray())
            {
                if (servers.Server.RunId != run) throw new IOException("服务器运行会话已改变。");
                var receipt = await servers.DeliverCfgAsync(instance.Id, preset.Id, run, file.Id, hashes[file.Id]);
                if (!receipt.Written) throw new IOException($"{file.Id}：{receipt.Message}");
                written++;
            }
            owner.ShowInstanceNotification($"已写入 {written} 条 CFG 命令", "写入不代表执行成功，请以服务器日志为准。");
        }
        catch (Exception ex) when (ex is IOException or InstanceException or InvalidOperationException)
        { owner.ShowInstanceNotification($"CFG 投递已停止（已写入 {written}/{files.Length} 条）", ex.Message); }
        finally { executing = false; UpdateActions(); }
    }
}
