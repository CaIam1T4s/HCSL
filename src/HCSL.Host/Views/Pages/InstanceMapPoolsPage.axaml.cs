using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

/// <summary>
/// 地图池 of an instance: the library (create, rename, delete) plus the console map page in pool-editing mode, where the
/// checkboxes select maps for bulk pool operations. A preset only chooses a pool; it never edits one.
/// </summary>
public partial class InstanceMapPoolsPage : UserControl
{
    private HostServerController? subscribed;
    private Guid? addTarget;
    private MapPoolRow? browsed;
    private MapsPage? maps;
    private bool operating, loaded;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private InstanceDefinition? Instance => Model?.Selected?.Entry.Definition;

    public InstanceMapPoolsPage()
    {
        InitializeComponent();
        PoolChoices.AddHandler(InputElement.ContextRequestedEvent, OnPoolContextRequested,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DeleteButton.Icon = Lucide.Geometry(LucideIconKind.Trash);
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible && !loaded) _ = LoadAsync(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        if (IsVisible) _ = LoadAsync();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = null;
        loaded = false;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        Update();
        if (loading || operating || Model is not { } m || m.Busy || Instance is not { } instance) return;
        if (instance.Id == loadedInstance) return;
        Dispatcher.UIThread.Post(() => { loaded = false; _ = LoadAsync(); });
    }

    private Guid loadedInstance;

    private async Task LoadAsync()
    {
        if (loading) return;
        if (Model is not { } m || Instance is not { } instance || !m.Server.Connected || operating) return;
        loading = true;
        loaded = true;
        loadedInstance = instance.Id;
        try
        {
            PoolChoices.IsLoading = true;
            var result = await m.MapPoolsAsync(new MapPoolLibraryCommand(MapPoolLibraryAction.List, instance.Id));
            SetChoices(result);
            await RefreshMapsAsync();
        }
        catch (IOException error) { Error("读取图池失败", error.Message); loaded = false; }
        finally { PoolChoices.IsLoading = false; loading = false; Update(); }
    }

    private void SetChoices(MapPoolLibraryResult result)
    {
            var assignments = result.Library.Assignments;
            var rows = result.Library.Pools.Select(pool =>
            {
                var used = assignments.Count(pair => pair.Value == pool.Id);
                return new MapPoolRow(pool,
                    pool.Maps.Length == 0 ? "空" : $"{pool.Maps.Length} 张",
                    used == 0 ? "未被预设引用" : $"被 {used} 个预设使用",
                    pool.Maps.Length == 0 ? BadgeTone.Warning : BadgeTone.Neutral);
            }).ToArray();
        PoolChoices.ItemsSource = rows;
        Update();
    }

    private bool loading;

    private GlassMenu? rowMenu;

    private void OpenPoolActions(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MapPoolRow row } anchor) ShowPoolActions(row, anchor);
    }

    private void OnPoolContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(PoolChoices, out var point))
        {
            if (e.Source is Control { DataContext: MapPoolRow row } source && source.Classes.Contains("pool-row-action"))
            {
                ShowPoolActions(row, source);
                e.Handled = true;
            }
            return;
        }

        var body = PoolChoices.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(v => v.Name == "PART_ScrollViewer");
        if (body?.TranslatePoint(default, PoolChoices) is not { } bodyOrigin ||
            !new Rect(bodyOrigin, body.Bounds.Size).Contains(point)) return;
        var band = body.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
            c.GetType().Name == "TableRow" && c.TranslatePoint(default, PoolChoices) is { } origin &&
            new Rect(origin, c.Bounds.Size).Contains(point));
        var action = band?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("pool-row-action"));
        if (action?.DataContext is not MapPoolRow target || PoolChoices.TranslatePoint(point, PoolMenuLayer) is not { } position) return;
        Canvas.SetLeft(PoolMenuAnchor, position.X);
        Canvas.SetTop(PoolMenuAnchor, position.Y);
        ShowPoolActions(target, PoolMenuAnchor);
        e.Handled = true;
    }

    private void ShowPoolActions(MapPoolRow row, Control anchor)
    {
        if (Instance?.Id != loadedInstance) return;
        DeletePopover.IsOpen = false;
        rowMenu?.Close();
        browsed = row;
        var menu = rowMenu = new GlassMenu();
        var writable = !operating && Model?.CanEditMapPool == true;
        var collection = row.Pool.Maps.Any(m => m.Kind == ServerMapKind.WorkshopCollection);
        Add("添加地图", AddMaps, !collection && !operating);
        Add("重命名图池", RenamePool, writable);
        Add("删除图池", (_, _) => OpenDelete(anchor), writable);
        menu.Open(anchor);

        void Add(string label, EventHandler<RoutedEventArgs> click, bool enabled)
        {
            var item = new GlassMenuItem { Content = label, IsEnabled = enabled };
            item.Click += click;
            menu.Items.Add(item);
        }
    }

    private bool workshopBusy;

    private async void AddWorkshop(object? sender, RoutedEventArgs e)
    {
        if (workshopBusy || Model is not { } model || Instance is not { } instance || Owner is not { } owner) return;
        workshopBusy = true;
        try
        {
            if (await owner.TextAsync("添加工坊地图 / 合集", "Steam 工坊链接或 ID", maxLength: 512, hideConsole: false, confirmLabel: "识别") is not { } input) return;
            if (WorkshopCatalog.ParseId(input) is not { } id) { Error("工坊链接或 ID 无效"); return; }
            var query = await model.WorkshopCatalogAsync(new(instance.Id, WorkshopId: id));
            if (Instance?.Id != instance.Id) return;
            var item = query.Workshop ?? throw new IOException("请更新并重启守护进程。");
            var form = new StackPanel { Spacing = 12 };
            form.Children.Add(new TextBlock { Text = item.Title, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            WorkshopItem[] register;
            if (item.Children is { } children)
            {
                if (children.Length == 0)
                {
                    owner.ShowInstanceNotification("合集没有可识别的 CS2 地图", "指南等非地图条目无法导入。", ToastKind.Warning);
                    return;
                }
                form.Classes.Add("instance-editor");
                form.Classes.Add("workshop-collection-preview");
                form.Children.RemoveAt(0); // The expanded tree root already shows the collection name.
                var hint = new TextBlock { Text = "全选才能导入为合集；否则只登记所选地图，不创建图池。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                ArcTokens.Bind(hint, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
                form.Children.Add(hint);
                var tree = new TreeTable { Caption = "合集地图", IsSelectable = true,
                    ItemsSource = new[] { item }, ChildrenSelector = x => ((WorkshopItem)x).Children,
                    KeySelector = x => ((WorkshopItem)x).Id, ExpandedKeys = [item.Id] };
                tree.Columns.Add(TreeTableColumn.Create<WorkshopItem>("地图名称", x => x.Title));
                tree.SelectedKeys = [item.Id, .. children.Select(map => (object)map.Id)];
                tree.Styles.Add(new Avalonia.Styling.Style(s => s.Is<Grid>().Class(":selected"))
                {
                    Setters = { new Avalonia.Styling.Setter(Panel.BackgroundProperty, Avalonia.Media.Brushes.Transparent) }
                });
                form.Children.Add(new ScrollViewer { Height = 300, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = tree });
                var asCollection = new TKCode.Arc.Checkbox { Content = "导入为合集", IsChecked = true };
                tree.SelectionChanged += (_, e) =>
                {
                    var keys = e.SelectedKeys.ToHashSet();
                    asCollection.IsEnabled = children.All(map => keys.Contains(map.Id));
                    if (!asCollection.IsEnabled) asCollection.IsChecked = false;
                };
                form.Children.Add(asCollection);
                if (await owner.ShowWorkshopPreviewAsync(form, ["取消", "导入"]) != 1 || Instance?.Id != instance.Id) return;
                var selectedKeys = tree.SelectedKeys?.ToHashSet() ?? [];
                var selectedMembers = children.Where(map => selectedKeys.Contains(map.Id)).ToArray();
                if (selectedMembers.Length == 0) { Error("请至少勾选一张地图"); return; }
                if (asCollection.IsChecked == true && selectedMembers.Length == children.Length)
                {
                    var name = item.Title[..Math.Min(item.Title.Length, MapPoolLibraryRules.MaxNameLength)].Trim();
                    await WriteAsync(i => new(MapPoolLibraryAction.Create, i.Id, i.Revision, Name: name,
                        Maps: [new(ServerMapKind.WorkshopCollection, item.Id)], CollectionMembers: selectedMembers));
                    return;
                }
                register = selectedMembers;
            }
            else
            {
                form.Classes.Add("instance-editor");
                var heading = new TextBlock { Text = "添加工坊地图" };
                ArcTokens.Bind(heading, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeLG);
                form.Children.Insert(0, heading);
                var catalog = await model.MapCatalogAsync(instance.Id);
                if (Instance?.Id != instance.Id) return;
                var entry = catalog.Entries.FirstOrDefault(x => x.WorkshopId == item.Id);
                var metadata = new TextBlock { Text = $"工坊 ID：{item.Id}" + (entry?.Incomplete == true ? " · 未下载" : "") };
                ArcTokens.Bind(metadata, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
                form.Children.Add(metadata);
                if (await owner.ShowWorkshopPreviewAsync(form, ["取消", "添加地图"]) != 1 || Instance?.Id != instance.Id) return;
                register = [item];
            }
            var saved = await model.WorkshopCatalogAsync(new(instance.Id, Register: register));
            if (saved.Detail != "workshop-registered") throw new IOException("请更新并重启守护进程。");
            if (Instance?.Id == instance.Id) await RefreshMapsAsync();
        }
        catch (IOException error) { Error("工坊地图未添加", error.Message); }
        finally { workshopBusy = false; }
    }

    private async void AddMaps(object? sender, RoutedEventArgs e)
    {
        if (browsed is not { } row) return;
        addTarget = row.Pool.Id;
        ManagementTabs.SelectedIndex = 1;
        await RefreshMapsAsync();
    }

    private async Task RefreshMapsAsync()
    {
        if (Model is not { } m || Instance is not { } instance) return;
        maps ??= new MapsPage();
        if (!ReferenceEquals(MapsHost.Content, maps)) MapsHost.Content = maps;
        var preset = instance.SelectedPresetId;
        await maps.AttachAsync(new MapsPageContext(m, instance.Id, preset,
            options => Owner?.ShowInstanceNotification(options.Title, options.Description, options.Kind),
            ManagePools: true, AddToPoolId: addTarget, PoolsChanged: SetChoices));

    }

    private void Update()
    {
        if (Model is not { } m) return;
        var connected = !operating && m.Server.Connected;
        var writable = connected && m.CanEditMapPool;
        CreateButton.IsEnabled = writable;
        DeleteButton.IsEnabled = writable && browsed is not null;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    /// <summary>All pool writes share one shape: send the command, report the failure, then reload library and table.</summary>
    private async Task WriteAsync(Func<InstanceDefinition, MapPoolLibraryCommand> command)
    {
        if (operating || Model is not { } m || Instance is not { } instance) return;
        operating = true;
        Update();
        try
        {
            var snapshot = await m.MapPoolsAsync(new(MapPoolLibraryAction.List, instance.Id));
            await m.MapPoolsAsync(command(instance) with
            {
                ExpectedRevision = snapshot.Library.Revision,
                ExpectedHash = snapshot.Hash
            });
        }
        catch (Exception error) when (error is IOException or InstanceException) { Error("图池未保存", error.Message); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }

    private async void CreatePool(object? sender, RoutedEventArgs e)
    {
        if (operating || Instance is null || Owner is not { } owner) return;
        if (await owner.TextAsync("新建图池", "图池名称（1–40 字符）", null, MapPoolLibraryRules.MaxNameLength) is not { } name) return;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Create,
            instance.Id, instance.Revision, Name: name));
    }

    private async void RenamePool(object? sender, RoutedEventArgs e)
    {
        if (operating || Instance is null || Owner is not { } owner || browsed is null) return;
        var pool = browsed.Pool;
        if (await owner.TextAsync("重命名图池", "新的名称（1–40 字符）", pool.Name, MapPoolLibraryRules.MaxNameLength) is not { } name) return;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Rename,
            instance.Id, instance.Revision, pool.Id, Name: name));
    }

    private void OpenDelete(Control anchor)
    {
        Update();
        if (!DeleteButton.IsEnabled || browsed is null) return;
        DeleteButton.IsConfirmed = false;
        DeletePopover.ShowAt(anchor);
    }

    private async void DeletePool(object? sender, RoutedEventArgs e)
    {
        if (operating || browsed is null) return;
        var pool = browsed.Pool;
        DeletePopover.IsOpen = false;
        DeleteButton.IsConfirmed = false;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Delete,
            instance.Id, instance.Revision, pool.Id));
    }

}
