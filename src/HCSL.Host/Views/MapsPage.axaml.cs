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
    Guid? PoolId = null);

/// <summary>
/// The console's map page: the app-level map catalog filtered through Arc's toolbar blocks. Exactly one map can be
/// chosen, so the bar carries one action; pool membership is shown, never edited here (that lives in the instance editor).
/// </summary>
public sealed partial class MapsPage : UserControl
{
    private readonly ObservableCollection<MapRow> allRows = [], visible = [];
    private MapsPageContext? context;
    private MapPoolLibraryResult? library;
    private MapRow? selected;

    public MapsPage()
    {
        InitializeComponent();
        Table.ItemsSource = visible;
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
            SelectAll = total => "全选 " + total,
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
        if (context?.PoolId is not null) return;
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
        context = value;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (context is not { } ctx) return;
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
        var pools = Array.Empty<MapPoolRecord>();
        try
        {
            library = await ctx.Model.MapPoolsAsync(new(MapPoolLibraryAction.List, ctx.InstanceId));
            pools = library.Library.Pools;
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException or UnauthorizedAccessException)
        {
            library = null;
            ctx.Notify(new ToastOptions { Title = "无法读取图池", Description = e.Message + "；地图列表仍可使用", Kind = ToastKind.Warning });
        }
        allRows.Clear();
        var edited = ctx.PoolId is { } poolId ? pools.FirstOrDefault(p => p.Id == poolId) : null;
        var editedKeys = edited?.Maps.Select(MapCatalogRules.KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries)
        {
            var names = pools.Where(p => p.Maps.Any(m => MapCatalogRules.KeyOf(m) == entry.Key))
                .Select(p => p.Name).ToArray();
            var row = new MapRow(entry, names) { CanJoinPool = CanJoin(entry, editedKeys.Contains(entry.Key)) };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MapRow.IsSelected)) Choose(row); };
            allRows.Add(row);
        }
        FillPoolFilter(pools, library?.Library.PoolOf(ctx.PresetId)?.Id);
        // A pool can hold a map the catalog does not offer yet (a removed vanity pack, a Workshop id that never
        // downloaded). Show it anyway, so filtering a pool never yields "no maps" for maps that are in it.
        var known = allRows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphaned = pools.SelectMany(p => p.Maps).Where(m => !known.Contains(MapCatalogRules.KeyOf(m))).ToArray();
        foreach (var target in orphaned)
        {
            known.Add(MapCatalogRules.KeyOf(target));
            var entry = target.Kind == ServerMapKind.Standard
                ? new MapCatalogEntry(target.Value, MapCatalogSource.Standard, null)
                : new MapCatalogEntry("Workshop #" + target.Value, MapCatalogSource.Workshop, target.Value, Incomplete: true);
            var names = pools.Where(p => p.Maps.Any(m => MapCatalogRules.KeyOf(m) == entry.Key)).Select(p => p.Name).ToArray();
            allRows.Add(new MapRow(entry, names) { CanJoinPool = CanJoin(entry, editedKeys.Contains(entry.Key)) });
        }
        ApplyPoolMode(ctx, edited, editedKeys);
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
        visible.Clear();
        foreach (var row in allRows)
        {
            if (sources.Count > 0 && !row.SourceFilterValues.Any(sources.Contains)) continue;
            if (pools.Count > 0 && !row.Pools.Any(v => pools.Contains(v.Name))) continue;
            if (query.Length > 0 && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !(row.WorkshopId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            visible.Add(row);
        }
        Selection.SelectedCount = selected is { IsSelected: true } ? 1 : 0;
        UpdateActions();
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
        if (Selection.Actions.FirstOrDefault(a => a.Key == "switch") is { } action)
            action.IsEnabled = selected is { IsSelected: true };
    }

    /// <summary>A preview or an unfinished Workshop download cannot be added; one already in the pool can be removed.</summary>
    private static bool CanJoin(MapCatalogEntry entry, bool alreadyInPool) =>
        entry.Source != MapCatalogSource.Preview && (!entry.Incomplete || alreadyInPool);

    /// <summary>
    /// Pool editing uses Arc's own checkbox column: rows of the pool start checked, and a change saves the whole pool.
    /// Rows that cannot join are silently dropped from the selection instead of showing an error.
    /// </summary>
    private void ApplyPoolMode(MapsPageContext ctx, MapPoolRecord? edited, HashSet<string> keys)
    {
        if (ctx.PoolId is null)
        {
            Table.IsSelectable = false;
            return;
        }
        editedPool = edited;
        editedKeys = keys;
        Table.IsSelectable = true;
        Table.SelectsOnRowClick = false;
        if (!poolSelectionBound)
        {
            Table.SelectionChanged += OnPoolSelectionChanged;
            poolSelectionBound = true;
        }
        applyingPoolSelection = true;
        try
        {
            Table.SelectedItems = allRows.Where(row => keys.Contains(row.Key)).Cast<object>().ToList();
        }
        finally { applyingPoolSelection = false; }
    }

    private MapPoolRecord? editedPool;
    private HashSet<string> editedKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool poolSelectionBound, applyingPoolSelection;

    private async void OnPoolSelectionChanged(object? sender, DataTableSelectionChangedEventArgs e)
    {
        if (applyingPoolSelection || context is not { PoolId: { } poolId } ctx || editedPool is null) return;
        var selected = Table.SelectedItems.OfType<MapRow>().ToArray();
        var rejected = selected.Where(row => !row.CanJoinPool).ToArray();
        if (rejected.Length > 0)
        {
            // Silent by design: the checkbox simply does not take.
            applyingPoolSelection = true;
            try
            {
                foreach (var row in rejected) Table.SelectedItems.Remove(row);
            }
            finally { applyingPoolSelection = false; }
            selected = Table.SelectedItems.OfType<MapRow>().ToArray();
        }
        var maps = selected.Select(row => row.Target).ToArray();
        var nextKeys = maps.Select(MapCatalogRules.KeyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nextKeys.SetEquals(editedKeys)) return;
        applyingPoolSelection = true;
        try
        {
            var result = await ctx.Model.MapPoolsAsync(new MapPoolLibraryCommand(MapPoolLibraryAction.SaveMaps,
                ctx.InstanceId, Maps: maps, PoolId: poolId));
            library = result;
            editedPool = result.Library.Pools.FirstOrDefault(pool => pool.Id == poolId);
            editedKeys = nextKeys;
        }
        catch (Exception error) when (error is IOException or InstanceException or InvalidOperationException)
        {
            ctx.Notify(new ToastOptions { Title = "图池未保存", Description = error.Message, Kind = ToastKind.Error });
            Table.SelectedItems = allRows.Where(row => editedKeys.Contains(row.Key)).Cast<object>().ToList();
        }
        finally { applyingPoolSelection = false; }
    }

    private void FiltersChanged(object? sender, FilterToolbarFiltersChangedEventArgs e) => Apply();

    private void SelectAll(object? sender, RoutedEventArgs e) { }

    private void ClearSelection(object? sender, RoutedEventArgs e)
    {
        foreach (var row in allRows) row.IsSelected = false;
        selected = null;
        Selection.SelectedCount = 0;
        UpdateActions();
    }

    private async void Invoked(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (context is not { } ctx || e.Action.Key != "switch" || selected is not { } row) return;
        await SwitchAsync(ctx, row);
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
