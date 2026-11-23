using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

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
    private List<MapRow> visible = [];
    private MapsPageContext? context;
    private MapPoolLibraryResult? library;
    private MapRow? selected;
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
        Table.ItemsSource = visible;
        Table.SelectionChanged += (_, _) => UpdatePoolSelection();
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
        Selection.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectionToolbar.SelectedCountProperty) UpdateActions();
        };
        // The whole row selects its map: text cells are drawn by the table itself and carry no control to click.
        Table.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, OnRowClicked,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Maps a click anywhere in a row to that row's selection. The row container has no data context and plain text
    /// cells are self-drawn, so the row band is found first and the player is then read from any templated cell in it.
    /// </summary>
    private void OnRowClicked(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Left) return;
        // Pool editing reads the checkbox, not the row: a stray click must not toggle membership.
        if (context?.ManagePools == true) return;
        var point = e.GetPosition(Table);
        double? bandTop = null, bandBottom = null;
        foreach (var container in Table.GetVisualDescendants().OfType<Control>())
        {
            if (container.GetType().Name != "TableRow") continue;
            if (container.TranslatePoint(default, Table) is not { } origin) continue;
            var bottom = origin.Y + container.Bounds.Height;
            if (point.Y < origin.Y || point.Y >= bottom) continue;
            bandTop = origin.Y;
            bandBottom = bottom;
            break;
        }
        if (bandTop is not { } top || bandBottom is not { } limit) return;
        foreach (var cell in Table.GetVisualDescendants().OfType<Control>())
        {
            if (cell.DataContext is not MapRow row) continue;
            if (cell.TranslatePoint(default, Table) is not { } origin) continue;
            var center = origin.Y + cell.Bounds.Height / 2;
            if (center < top || center >= limit) continue;
            row.IsSelected = true;
            e.Handled = true;
            return;
        }
    }

    public async Task AttachAsync(MapsPageContext value)
    {
        if (context is { } old && (old.InstanceId != value.InstanceId || old.PoolId != value.PoolId))
        {
            library = null;
            ClearSelection(this, new RoutedEventArgs());
            Table.ItemsSource = visible = [];
        }
        context = value;
        if (value.ManagePools && !Table.IsSelectable)
        {
            ManagePools = true;
            Table.LayoutUpdated += HidePoolTableFooter;
            Table.Columns.RemoveAt(Table.Columns.Count - 1);
            Table.Columns.RemoveAt(0);
            Table.Columns[^1].Width = new GridLength(1, GridUnitType.Star);
            Table.SelectsOnRowClick = false;
            Table.IsSelectable = true;
            Selection.Actions.Clear();
            Selection.Actions.Add(new SelectionToolbarAction { Key = "add", Label = "添加到图池" });
            Selection.Actions.Add(new SelectionToolbarAction { Key = "remove", Label = "从图池移除" });

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
        if (version != reloadVersion) return;
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
                && !(row.WorkshopId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            next.Add(row);
        }
        Table.ItemsSource = visible = next;
        if (Table.IsSelectable) UpdatePoolSelection();
        else UpdateActions();
    }

    /// <summary>Only one row may be chosen: choosing another clears the previous one, hiding it clears the bar.</summary>
    private void Choose(MapRow row)
    {
        if (row.IsSelected)
        {
            selected = row;
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
        foreach (var action in Selection.Actions)
            action.IsEnabled = action.Key == "switch" ? selected is { IsSelected: true }
                : !savingPool && Selection.SelectedCount > 0 && library?.Library.Pools.Length > 0;
        if (Selection.Actions.FirstOrDefault(a => a.Key == "add") is { } add)
            add.IsEnabled &= context?.PoolId is null;
    }

    private bool savingPool;
    private GlassMenu? poolMenu;

    private void HidePoolTableFooter(object? sender, EventArgs e)
    {
        // Arc binds this generated part at template priority, above an ordinary style setter.
        if (Table.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_Footer") is { IsVisible: true } footer)
            footer.IsVisible = false;
    }

    private void UpdatePoolSelection()
    {
        if (!Table.IsSelectable) return;
        Selection.TotalCount = visible.Count;
        Selection.SelectedCount = Table.SelectedItems?.Count ?? 0;
        UpdateActions();
    }

    private void OpenPoolMenu(MapsPageContext ctx, bool add)
    {
        if (savingPool || library is not { } snapshot || Table.SelectedItems is not { Count: > 0 } items) return;
        var rows = items.OfType<MapRow>().Where(visible.Contains).ToArray();
        poolMenu?.Close();
        var menu = poolMenu = new GlassMenu { Placement = PlacementMode.TopEdgeAlignedLeft };
        if (!add && ctx.PoolId is { } current && snapshot.Library.Pools.FirstOrDefault(p => p.Id == current) is { } active)
        {
            _ = ChangePoolAsync(ctx, snapshot, active, rows, false);
            return;
        }
        foreach (var pool in snapshot.Library.Pools.Where(p => p.Maps.All(m => m.Kind != ServerMapKind.WorkshopCollection)).OrderByDescending(p => add && p.Id == ctx.AddToPoolId)
                     .ThenBy(p => p.Name, StringComparer.CurrentCulture))
        {
            var item = new GlassMenuItem { Content = add && pool.Id == ctx.AddToPoolId ? $"{pool.Name}（目标图池）" : pool.Name };
            item.Click += async (_, _) => await ChangePoolAsync(ctx, snapshot, pool, rows, add);
            menu.Items.Add(item);
        }
        var key = add ? "add" : "remove";
        var anchor = Selection.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .FirstOrDefault(b => b.Tag is SelectionToolbarAction action && action.Key == key);
        if (menu.Items.Count > 0 && anchor is not null) menu.Open(anchor);
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
        if (Table.IsSelectable) Table.SelectAll();
    }

    private void ClearSelection(object? sender, RoutedEventArgs e)
    {
        poolMenu?.Close();
        if (Table.IsSelectable && Table.SelectedItems is { Count: > 0 }) Table.ClearSelection();
        foreach (var row in allRows) row.IsSelected = false;
        selected = null;
        Selection.SelectedCount = 0;
        UpdateActions();
    }

    private async void Invoked(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (context is not { } ctx) return;
        if (e.Action.Key is "add" or "remove") OpenPoolMenu(ctx, e.Action.Key == "add");
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
