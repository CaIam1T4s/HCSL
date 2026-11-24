using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views;

/// <summary>What the maps page needs from the console window: the controllers, the current preset and a toast sink.</summary>
public sealed record MapsPageContext(HostServerController Model, Guid InstanceId, Guid PresetId, Action<ToastOptions> Notify,
    Guid? PoolId = null, bool ManagePools = false, Guid? AddToPoolId = null,
    Action<MapPoolLibraryResult>? PoolsChanged = null);

/// <summary>
/// The console's map page: the app-level map catalog filtered through Arc's toolbar blocks. Exactly one map can be
/// chosen, so the bar carries one action; pool membership is shown, never edited here (that lives in the instance editor).
/// </summary>
public sealed partial class MapsPage : UserControl
{
    private readonly ObservableCollection<MapRow> allRows = [];
    private MapsPageContext? context;
    private ServerController? observedServer;
    private MapPoolLibraryResult? library;
    private MapRow? selected;
    private WorkshopItem? runningCollection;
    private int reloadVersion;
    public static readonly StyledProperty<bool> ManagePoolsProperty =
        AvaloniaProperty.Register<MapsPage, bool>(nameof(ManagePools));
    public bool ManagePools
    {
        get => GetValue(ManagePoolsProperty);
        private set => SetValue(ManagePoolsProperty, value);
    }

    public MapsPage()
    {
        InitializeComponent();
        TreeTableHeaderAlignment.Attach(CollectionTree);
        CollectionTree.ChildrenSelector = node => (node as CollectionBranch)?.Children;
        CollectionTree.KeySelector = node => node is CollectionBranch branch ? branch.Key : node;
        CollectionTree.Columns.Add(new TreeTableColumn
        {
            Header = "地图名", ValueSelector = node => node is CollectionBranch branch ? branch.Name : ((MapRow)node).Name,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((node, _) =>
            {
                if (node is CollectionBranch branch) return new TextBlock { Text = branch.Name };
                if (node is not MapRow row) return null;
                var label = new TextBlock { Text = row.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                if (ManagePools) return label;
                var mark = new Badge { Content = "已选", IsVisible = row.IsSelected,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                mark.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(MapRow.IsSelected)) { Source = row });
                var cell = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(mark, Dock.Right);
                cell.Children.Add(mark);
                cell.Children.Add(label);
                return cell;
            })
        });
        CollectionTree.Columns.Add(new TreeTableColumn { Header = "来源", Width = new GridLength(120), IsSortable = false,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((node, _) => node is null ? null : new Badge
            { Content = node is MapRow row ? row.SourceText : "工坊合集", Tone = node is MapRow map ? map.SourceTone : BadgeTone.Info }) });
        CollectionTree.Columns.Add(new TreeTableColumn { Header = "所属图池", Width = new GridLength(160), IsSortable = false,
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((node, _) => node is null ? null : new PresenceStack
            {
                Label = "所属图池", IsCountVisible = false,
                IsVisible = node is not MapRow map || map.InPool,
                ItemsSource = node is MapRow row ? row.Pools : [new PresenceStackViewer(((CollectionBranch)node).Name)]
            }) });
        CollectionTree.Styles.Add(new Style(s => s.Is<Grid>().Class(":selected"))
        {
            Setters = { new Setter(Panel.BackgroundProperty, Avalonia.Media.Brushes.Transparent) }
        });
        CollectionTree.SelectionChanged += (_, _) => UpdatePoolSelection();
        CollectionTree.RowActivated += (_, e) =>
        {
            if (!ManagePools && e.Item is MapRow row) row.IsSelected = true;
        };
        AutomationProperties.SetName(RulesButton, "地图轮换规则");
        // Arc's tooltip, so it keeps the shared floating-surface material and typography.
        Tooltip.SetTip(RulesButton, "仅内置地图和创意工坊合集支持轮换");
        // The refresh icon is self-evident; an extra tooltip would only add noise.
        AutomationProperties.SetName(RefreshButton, "刷新地图目录");
        // Single selection: the bar only ever reports one row, and "select all" has nothing to select.
        Selection.TotalCount = 0;
        Selection.Labels = new SelectionToolbarLabels
        {
            Culture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
            Count = count => "已选 " + count,
            SelectAll = total => "选择所有 " + total,
            Clear = "清除选择",
            Toolbar = "批量操作"
        };
        AttachedToVisualTree += (_, _) => ObserveServer(context?.Model.Server);
        DetachedFromVisualTree += (_, _) => ObserveServer(null);
        Selection.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectionToolbar.SelectedCountProperty) UpdateActions();
        };

    }

    private void ObserveServer(ServerController? server)
    {
        if (ReferenceEquals(observedServer, server)) return;
        if (observedServer is not null) observedServer.PropertyChanged -= ServerChanged;
        observedServer = server;
        if (observedServer is not null) observedServer.PropertyChanged += ServerChanged;
        UpdateActions();
    }

    private void ServerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateActions);

    public async Task AttachAsync(MapsPageContext value)
    {
        if (context is { } old && (old.InstanceId != value.InstanceId || old.PoolId != value.PoolId))
        {
            library = null;
            ClearSelection(this, new RoutedEventArgs());
            CollectionTree.ItemsSource = null;
        }
        context = value;
        if (this.IsAttachedToVisualTree()) ObserveServer(value.Model.Server);
        UpdateActions();
        CollectionTree.IsSelectable = value.ManagePools;
        if (value.ManagePools && !ManagePools)
        {
            ManagePools = true;
            Selection.Actions.Clear();
            Selection.Actions.Add(new SelectionToolbarAction { Key = "add", Label = "添加到图池" });
            Selection.Actions.Add(new SelectionToolbarAction { Key = "remove", Label = "从图池移除" });
            Selection.Actions.Add(new SelectionToolbarAction { Key = "delete-workshop", Label = "移除地图" });

        }
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (context is not { } ctx) return;
        var version = ++reloadVersion;
        MapCatalogResult catalog;
        try
        {
            catalog = await ctx.Model.MapCatalogAsync(ctx.InstanceId);
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException or UnauthorizedAccessException)
        {
            ctx.Notify(new ToastOptions { Title = "无法读取地图", Description = e.Message, Kind = ToastKind.Error });
            return;
        }
        // The catalog is the page's reason to exist, so rows appear even when the pool library cannot be read.
        MapPoolLibraryResult? snapshot = null;
        try
        {
            snapshot = await ctx.Model.MapPoolsAsync(new(MapPoolLibraryAction.List, ctx.InstanceId));
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException or UnauthorizedAccessException)
        {
            ctx.Notify(new ToastOptions { Title = "无法读取图池", Description = e.Message + "；地图列表仍可使用", Kind = ToastKind.Warning });
        }
        WorkshopItem? activeCollection = null;
        var running = ctx.Model.Server.Snapshot;
        if (!ctx.ManagePools && running is { Active: true, InstanceId: var instanceId, Preset: { } preset } && instanceId == ctx.InstanceId)
        {
            var pool = snapshot?.Library.PoolOf(preset.Id);
            var id = preset.MapKind == ServerMapKind.WorkshopCollection ? preset.Map
                : pool?.Maps is [{ Kind: ServerMapKind.WorkshopCollection } target] ? target.Value : null;
            if (ServerMapRules.TryValidateWorkshopId(id))
            {
                try { activeCollection = (await ctx.Model.WorkshopCatalogAsync(new(ctx.InstanceId, WorkshopId: id))).Workshop; }
                catch (IOException) { /* The catalog and saved member snapshot remain usable offline. */ }
            }
        }
        if (version != reloadVersion) return;
        runningCollection = ctx.Model.Server.Snapshot?.RunId == running?.RunId ? activeCollection : null;
        library = snapshot;
        var pools = snapshot?.Library.Pools ?? [];
        ClearSelection(this, new RoutedEventArgs());
        allRows.Clear();
        foreach (var entry in catalog.Entries)
        {
            var names = pools.Where(p => p.Maps.Any(m => MapCatalogRules.KeyOf(m) == entry.Key))
                .Select(p => p.Name).ToArray();
            var row = new MapRow(entry, names);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MapRow.IsSelected)) Choose(row); };
            allRows.Add(row);
        }
        FillPoolFilter(pools, library?.Library.PoolOf(ctx.PresetId)?.Id);
        // A pool can hold a map the catalog does not offer yet (a removed vanity pack, a Workshop id that never
        // downloaded). Show it anyway, so filtering a pool never yields "no maps" for maps that are in it.
        var known = allRows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphaned = pools.SelectMany(p => p.Maps).Where(m => m.Kind != ServerMapKind.WorkshopCollection && !known.Contains(MapCatalogRules.KeyOf(m))).ToArray();
        foreach (var target in orphaned)
        {
            if (!known.Add(MapCatalogRules.KeyOf(target))) continue;
            var entry = target.Kind == ServerMapKind.Standard
                ? new MapCatalogEntry(target.Value, MapCatalogSource.Standard, null)
                : new MapCatalogEntry("Workshop #" + target.Value, MapCatalogSource.Workshop, target.Value, Incomplete: true);
            var names = pools.Where(p => p.Maps.Any(m => MapCatalogRules.KeyOf(m) == entry.Key)).Select(p => p.Name).ToArray();
            allRows.Add(new MapRow(entry, names));
        }
        Apply();
    }

    /// <summary>
    /// Pool chips: the preset's own pool first and marked as current, the rest by name with their map count, so the
    /// list answers "which one am I on" and "which are worth filtering" before it is opened.
    /// </summary>
    private void FillPoolFilter(IReadOnlyList<MapPoolRecord> pools, Guid? currentPoolId)
    {
        var field = Filters.Fields.FirstOrDefault(f => f.Id == "pool");
        if (field is null) return;
        field.Options.Clear();
        foreach (var pool in pools.OrderByDescending(p => p.Id == currentPoolId).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var count = pool.Maps.Length + " 张";
            field.Options.Add(new FilterOption
            {
                Value = pool.Name,
                Label = pool.Name,
                Hint = pool.Id == currentPoolId ? "当前 · " + count : count
            });
        }
    }

    /// <summary>Source chips, pool chips and the search text all narrow the same catalog.</summary>
    private void Apply()
    {
        var query = Filters.SearchText ?? "";
        var chips = Filters.ActiveFilters ?? [];
        // A chip may carry the option value ("standard") or its label ("内置"); accept either.
        var sources = chips.Where(c => c.Id == "source")
            .SelectMany(c => new[] { c.Value, c.Label })
            .Where(v => v is { Length: > 0 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pools = chips.Where(c => c.Id == "pool").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collections = library?.Library.Pools ?? [];
        if (runningCollection is { Children: { } live })
        {
            var match = collections.FirstOrDefault(p => p.Maps is [{ Kind: ServerMapKind.WorkshopCollection } map] && map.Value == runningCollection.Id);
            collections = match is null
                ? [.. collections, new MapPoolRecord(Guid.Empty, runningCollection.Title, [new(ServerMapKind.WorkshopCollection, runningCollection.Id)], live)]
                : collections.Select(p => p.Id == match.Id ? p with { CollectionMembers = live } : p).ToArray();
        }
        var collectionBranches = collections.Where(p => p.Maps is [{ Kind: ServerMapKind.WorkshopCollection }] &&
                (pools.Count == 0 || pools.Contains(p.Name)) && (query.Length == 0 ||
                    p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Maps[0].Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.CollectionMembers?.Any(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) == true))
                .Select(p => new CollectionBranch("collection:" + p.Id, p.Name,
                    (p.CollectionMembers ?? []).Select(m =>
                    {
                        var original = allRows.FirstOrDefault(r => r.WorkshopId == m.Id);
                        var row = new MapRow(original?.Entry ?? new(m.Title, MapCatalogSource.Workshop, m.Id, Incomplete: true),
                            (original?.PoolNames ?? []).Append(p.Name).Distinct().ToArray());
                        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MapRow.IsSelected)) Choose(row); };
                        return row;
                    }).Where(r => sources.Count == 0 || r.SourceFilterValues.Any(sources.Contains)).ToArray())).ToArray();
        var scope = context?.PoolId;
        var members = library?.Library.Pools.FirstOrDefault(p => p.Id == scope)?.Maps
            .Select(MapCatalogRules.KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = new List<MapRow>();
        foreach (var row in allRows)
        {
            if (scope is not null && members?.Contains(row.Key) != true) continue;
            if (sources.Count > 0 && !row.SourceFilterValues.Any(sources.Contains)) continue;
            if (pools.Count > 0 && !row.PoolNames.Any(pools.Contains)) continue;
            if (query.Length > 0 && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !(row.Entry.InternalName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                && !(row.WorkshopId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            next.Add(row);
        }
        var branches = collectionBranches.Where(branch => sources.Count == 0 || branch.Children.Count > 0).ToArray();
        var memberKeys = branches.SelectMany(b => b.Children).Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = next.Where(r => !memberKeys.Contains(r.Key)).ToArray();
        if (!ManagePools && selected is { } target && !roots.Contains(target) && !branches.Any(b => b.Children.Contains(target)))
            target.IsSelected = false;
        treeMapCount = roots.Concat(branches.SelectMany(b => b.Children)).Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        CollectionTree.ItemsSource = roots.Cast<object>().Concat(branches).ToArray();
        if (ManagePools) UpdatePoolSelection();
        else UpdateActions();
    }

    /// <summary>Only one row may be chosen: choosing another clears the previous one, hiding it clears the bar.</summary>
    private void Choose(MapRow row)
    {
        if (row.IsSelected)
        {
            var previous = selected;
            selected = row;
            if (previous is not null && !ReferenceEquals(previous, row)) previous.IsSelected = false;
            foreach (var other in allRows)
                if (!ReferenceEquals(other, row) && other.IsSelected) other.IsSelected = false;
            Selection.SelectedCount = 1;
        }
        else if (ReferenceEquals(selected, row))
        {
            selected = null;
            Selection.SelectedCount = 0;
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        var canEditMembership = !savingPool && context?.Model.CanEditMapPool == true && library is not null;
        var rows = canEditMembership ? SelectedPoolMaps() : [];
        foreach (var action in Selection.Actions)
            action.IsEnabled = action.Key switch
            {
                "switch" => selected is { IsSelected: true } && context is { } ctx &&
                    ctx.Model.Server is { CanSendConsole: true, Busy: false,
                        Snapshot: { Active: true, State: ServerState.Running, RunId: not null } run } &&
                    run.InstanceId == ctx.InstanceId,
                "add" => canEditMembership && context?.PoolId is null && EditablePools(library!, rows, true).Any(),
                "remove" => canEditMembership && EditablePools(library!, rows, false).Any(),
                "delete-workshop" => !savingPool && context?.Model.CanEditMapPool == true &&
                    (WorkshopSelection().Length > 0 || SelectedCollections().Length > 0),
                _ => false
            };
    }

    private sealed record CollectionBranch(string Key, string Name, IReadOnlyList<MapRow> Children);
    private int treeMapCount;
    private bool savingPool;
    private GlassMenu? poolMenu;

    private void UpdatePoolSelection()
    {
        if (!ManagePools) return;
        Selection.TotalCount = treeMapCount;
        Selection.SelectedCount = SelectedPoolMaps().Length;
        UpdateActions();
    }

    private MapRow[] SelectedPoolMaps()
    {
        var keys = CollectionTree.SelectedKeys?.ToHashSet() ?? [];
        return (CollectionTree.ItemsSource?.Cast<object>() ?? []).SelectMany(node => node is CollectionBranch branch
            ? branch.Children : [(MapRow)node]).Where(row => keys.Contains(row))
            .DistinctBy(row => row.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IEnumerable<MapPoolRecord> EditablePools(MapPoolLibraryResult snapshot, MapRow[] rows, bool add)
    {
        if (rows.Length == 0) yield break;
        var selectedKeys = rows.Select(row => row.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in snapshot.Library.Pools)
        {
            if (pool.Maps.Any(m => m.Kind == ServerMapKind.WorkshopCollection) ||
                (!add && context?.PoolId is { } scope && scope != pool.Id)) continue;
            var keys = pool.Maps.Select(MapCatalogRules.KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (add ? !selectedKeys.IsSubsetOf(keys) : selectedKeys.Overlaps(keys)) yield return pool;
        }
    }

    private void OpenPoolMenu(MapsPageContext ctx, bool add)
    {
        if (savingPool || !ctx.Model.CanEditMapPool || library is not { } snapshot) return;
        var rows = SelectedPoolMaps();
        var targets = EditablePools(snapshot, rows, add).ToArray();
        if (targets.Length == 0) return;
        poolMenu?.Close();
        var menu = poolMenu = new GlassMenu { Placement = PlacementMode.TopEdgeAlignedLeft };
        if (!add && ctx.PoolId is { } current && targets.FirstOrDefault(p => p.Id == current) is { } active)
        {
            _ = ChangePoolAsync(ctx, snapshot, active, rows, false);
            return;
        }
        foreach (var pool in targets.OrderByDescending(p => add && p.Id == ctx.AddToPoolId)
                     .ThenBy(p => p.Name, StringComparer.CurrentCulture))
        {
            var item = new GlassMenuItem
            {
                Content = add && pool.Id == ctx.AddToPoolId ? $"{pool.Name}（目标图池）" : pool.Name,
                Icon = Lucide.Geometry(LucideIconKind.Map)
            };
            item.Click += async (_, _) => await ChangePoolAsync(ctx, snapshot, pool, rows, add);
            menu.Items.Add(item);
        }
        var key = add ? "add" : "remove";
        var anchor = Selection.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Tag is SelectionToolbarAction action && action.Key == key);
        if (anchor is not null) menu.Open(anchor);
    }

    private async Task ChangePoolAsync(MapsPageContext ctx, MapPoolLibraryResult snapshot, MapPoolRecord pool,
        MapRow[] rows, bool add)
    {
        if (savingPool || !ReferenceEquals(ctx, context) || !ReferenceEquals(snapshot, library)) return;
        var keys = pool.Maps.Select(MapCatalogRules.KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedKeys = rows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maps = add
            ? pool.Maps.Concat(rows.Where(r => keys.Add(MapCatalogRules.KeyOf(r.Target)))
                .Select(r => r.Target)).ToArray()
            : pool.Maps.Where(m => !selectedKeys.Contains(MapCatalogRules.KeyOf(m))).ToArray();
        if (maps.Length == pool.Maps.Length) return;
        var instance = ctx.Model.Instances.FirstOrDefault(i => i.Entry.Id == ctx.InstanceId)?.Entry.Definition;
        if (instance is null) return;
        savingPool = true;
        UpdateActions();
        try
        {
            var result = await ctx.Model.MapPoolsAsync(new(MapPoolLibraryAction.SaveMaps, ctx.InstanceId, instance.Revision,
                pool.Id, Maps: maps, ExpectedRevision: snapshot.Library.Revision, ExpectedHash: snapshot.Hash));
            if (ReferenceEquals(ctx, context))
            {
                ctx.PoolsChanged?.Invoke(result);
                await ReloadAsync();
            }
        }
        catch (Exception error) when (error is IOException or InstanceException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(ctx, context))
                ctx.Notify(new ToastOptions { Title = "图池未保存", Description = error.Message, Kind = ToastKind.Error });
        }
        finally { savingPool = false; UpdateActions(); }
    }

    private void FiltersChanged(object? sender, FilterToolbarFiltersChangedEventArgs e) => Apply();

    private void SelectAll(object? sender, RoutedEventArgs e)
    {
        if (ManagePools)
            CollectionTree.SelectedKeys = (CollectionTree.ItemsSource?.Cast<object>() ?? []).SelectMany(node => node is CollectionBranch branch
                ? branch.Children.Cast<object>().Prepend(branch.Key) : [node]).Distinct().ToArray();
    }

    private void ClearSelection(object? sender, RoutedEventArgs e)
    {
        poolMenu?.Close();
        CollectionTree.SelectedKeys = [];
        if (selected is { } target) target.IsSelected = false;
        foreach (var row in allRows) row.IsSelected = false;
        selected = null;
        Selection.SelectedCount = 0;
        UpdateActions();
    }

    private string[] WorkshopSelection()
    {
        if (CollectionTree.IsVisible && CollectionTree.SelectedKeys?.Any(key => key is string) == true) return [];
        var rows = SelectedPoolMaps();
        var pools = library?.Library.Pools;
        return pools is not null && rows.All(r => r.Source == MapCatalogSource.Workshop && r.WorkshopId is { } id &&
            !pools.Any(p => p.Maps.Any(m => m.Kind == ServerMapKind.Workshop && m.Value == id) ||
                p.CollectionMembers?.Any(member => member.Id == id) == true))
            ? rows.Select(r => r.WorkshopId!).Distinct().ToArray() : [];
    }

    private MapPoolRecord[] SelectedCollections()
    {
        if (!CollectionTree.IsVisible || library is not { } snapshot) return [];
        var keys = CollectionTree.SelectedKeys?.ToHashSet() ?? [];
        var nodes = CollectionTree.ItemsSource?.Cast<object>().ToArray() ?? [];
        if (nodes.OfType<MapRow>().Any(keys.Contains)) return [];
        var branches = nodes.OfType<CollectionBranch>().ToArray();
        if (branches.Any(b => !keys.Contains(b.Key) && b.Children.Any(keys.Contains))) return [];
        return snapshot.Library.Pools.Where(p => p.Maps is [{ Kind: ServerMapKind.WorkshopCollection }] &&
            keys.Contains("collection:" + p.Id)).ToArray();
    }

    private (MapsPageContext Context, string[] Ids, Guid[] Pools)? workshopRemoval;

    private async void ConfirmRemoveWorkshop(object? sender, RoutedEventArgs e)
    {
        var removal = workshopRemoval;
        workshopRemoval = null;
        RemoveWorkshopPopover.IsOpen = false;
        RemoveWorkshopConfirm.IsConfirmed = false;
        if (removal is not { } request || !ReferenceEquals(context, request.Context) || savingPool ||
            !request.Ids.ToHashSet().SetEquals(WorkshopSelection()) ||
            !request.Pools.ToHashSet().SetEquals(SelectedCollections().Select(p => p.Id)) || !request.Context.Model.CanEditMapPool) return;
        var selectedMapKeys = SelectedPoolMaps().Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        savingPool = true;
        ClearSelection(this, new RoutedEventArgs());
        request.Context.Notify(new ToastOptions { Title = "正在移除…", Kind = ToastKind.Info });
        try
        {
            if (request.Pools.Length > 0)
            {
                var ctx = request.Context;
                var snapshot = library ?? throw new IOException("请刷新图池列表。");
                var instance = ctx.Model.Instances.FirstOrDefault(i => i.Entry.Id == ctx.InstanceId)?.Entry.Definition
                    ?? throw new IOException("实例不存在。");
                foreach (var poolId in request.Pools)
                {
                    snapshot = await ctx.Model.MapPoolsAsync(new(MapPoolLibraryAction.Delete, ctx.InstanceId, instance.Revision,
                        poolId, ExpectedRevision: snapshot.Library.Revision, ExpectedHash: snapshot.Hash));
                    if (ReferenceEquals(context, ctx)) ctx.PoolsChanged?.Invoke(snapshot);
                }
            }
            else
            {
                var result = await request.Context.Model.WorkshopCatalogAsync(new(request.Context.InstanceId, Remove: request.Ids));
                if (result.Detail != "workshop-removed") throw new IOException("请更新并重启守护进程。");
            }
            request.Context.Notify(new ToastOptions { Title = request.Pools.Length > 0 ? "已移除工坊合集" : "已移除工坊地图", Kind = ToastKind.Success });
            if (ReferenceEquals(context, request.Context)) await ReloadAsync();
        }
        catch (IOException error)
        {
            request.Context.Notify(new ToastOptions { Title = "未能完成移除", Description = error.Message, Kind = ToastKind.Error });
            if (ReferenceEquals(context, request.Context))
            {
                await ReloadAsync();
                if (ReferenceEquals(context, request.Context))
                {
                    CollectionTree.SelectedKeys = (CollectionTree.ItemsSource?.Cast<object>() ?? []).SelectMany(node => node is CollectionBranch branch
                        ? branch.Children.Where(row => selectedMapKeys.Contains(row.Key)).Cast<object>()
                            .Concat(request.Pools.Any(id => branch.Key == "collection:" + id) ? [branch.Key] : [])
                        : node is MapRow row && selectedMapKeys.Contains(row.Key) ? [node] : []).ToArray();
                    UpdatePoolSelection();
                }
            }
        }
        finally { savingPool = false; UpdateActions(); }
    }

    private async void Invoked(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (context is not { } ctx) return;
        if (e.Action.Key is "add" or "remove") OpenPoolMenu(ctx, e.Action.Key == "add");
        else if (e.Action.Key == "delete-workshop" && e.Action.IsEnabled &&
            Selection.GetVisualDescendants().OfType<Avalonia.Controls.Button>().FirstOrDefault(b => ReferenceEquals(b.Tag, e.Action)) is { } anchor)
        {
            var ids = WorkshopSelection();
            var pools = SelectedCollections().Select(p => p.Id).ToArray();
            if (ids.Length == 0 && pools.Length == 0) return;
            workshopRemoval = (ctx, ids, pools);
            RemoveWorkshopConfirm.Label = pools.Length > 0 ? "长按移除合集及图池" : "长按移除地图";
            RemoveWorkshopConfirm.IsConfirmed = false;
            RemoveWorkshopPopover.ShowAt(anchor);
        }
        else if (e.Action.Key == "switch" && selected is { } row) await SwitchAsync(ctx, row);
    }

    /// <summary>The switch only writes the command; loading and downloading are reported by the server log.</summary>
    private async Task SwitchAsync(MapsPageContext ctx, MapRow row)
    {
        if (ctx.Model.Server.Snapshot is not { RunId: { } run })
        {
            ctx.Notify(new ToastOptions { Title = "无法切换地图", Description = "当前没有运行中的服务器", Kind = ToastKind.Warning });
            return;
        }
        try
        {
            var receipt = await new ServerMapClient(ctx.Model.Server).ChangeAsync(ctx.InstanceId, run, row.Target);
            if (receipt is null)
            {
                ctx.Notify(new ToastOptions { Title = "换图投递结果未知", Description = "下一次状态轮询前无法确认", Kind = ToastKind.Warning });
                return;
            }
            ctx.Notify(receipt.Delivery switch
            {
                HCSL.Core.ConsoleDelivery.Rejected => new ToastOptions { Title = "换图命令被拒绝", Description = receipt.Code, Kind = ToastKind.Error },
                HCSL.Core.ConsoleDelivery.Written => new ToastOptions { Title = "已请求切换到 " + row.Name, Description = "是否加载以日志为准", Kind = ToastKind.Info },
                _ => new ToastOptions { Title = "换图投递结果未知", Description = receipt.Code, Kind = ToastKind.Warning }
            });
            if (row.Source == MapCatalogSource.Workshop && !row.Downloaded)
                ctx.Notify(new ToastOptions { Title = "该地图未下载", Description = "切换后由 CS2 下载，可能耗时数分钟", Kind = ToastKind.Info });
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
        {
            ctx.Notify(new ToastOptions { Title = "换图失败", Description = e.Message, Kind = ToastKind.Error });
        }
    }

    /// <summary>Rotation rules, kept short and honest: only built-in pools and a single collection rotate on their own.</summary>
    private void ShowRules(object? sender, RoutedEventArgs e) =>
        context?.Notify(new ToastOptions { Title = "仅内置地图和创意工坊合集支持轮换", Kind = ToastKind.Info });

    private async void Refresh(object? sender, RoutedEventArgs e) => await ReloadAsync();
}
