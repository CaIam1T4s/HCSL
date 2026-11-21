using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views;

/// <summary>What the maps page needs from the console window: the controllers, the current preset and a toast sink.</summary>
public sealed record MapsPageContext(HostServerController Model, Guid InstanceId, Guid PresetId, Action<ToastOptions> Notify);

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
        ToolTip.SetTip(RulesButton, "仅内置地图和创意工坊合集支持轮换");
        AutomationProperties.SetName(RefreshButton, "刷新地图目录");
        ToolTip.SetTip(RefreshButton, "刷新地图目录");
        // Single selection: the bar only ever reports one row, and "select all" has nothing to select.
        Selection.TotalCount = 0;
        Selection.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectionToolbar.SelectedCountProperty) UpdateActions();
        };
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
        foreach (var entry in catalog.Entries)
        {
            var names = pools.Where(p => p.Maps.Any(m => MapCatalogRules.KeyOf(m) == entry.Key))
                .Select(p => p.Name).ToArray();
            var row = new MapRow(entry, names);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MapRow.IsSelected)) Choose(row); };
            allRows.Add(row);
        }
        FillPoolFilter(pools, library?.Library.PoolOf(ctx.PresetId)?.Id);
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

    private void UpdateActions() =>
        Selection.Actions.Single(a => a.Key == "switch").IsEnabled = selected is { IsSelected: true };

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
    private void ShowRules(object? sender, RoutedEventArgs e)
    {
        context?.Notify(new ToastOptions
        {
            Title = "仅内置地图和创意工坊合集支持轮换",
            Description = "含工坊单图的图池需要手动切换；合集的成员与顺序以日志为准",
            Kind = ToastKind.Info
        });
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await ReloadAsync();
}
