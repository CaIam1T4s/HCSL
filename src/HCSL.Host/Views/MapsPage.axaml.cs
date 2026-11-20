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
/// The console's map page: the app-level map catalog filtered and selected through Arc's toolbar blocks, with the
/// current instance's pool deciding membership. Editable pools stay in the instance editor.
/// </summary>
public sealed partial class MapsPage : UserControl
{
    private readonly ObservableCollection<MapRow> allRows = [], visible = [];
    private MapsPageContext? context;
    private MapPoolResult? pool;

    public MapsPage()
    {
        InitializeComponent();
        Table.ItemsSource = visible;
        AutomationProperties.SetName(RulesButton, "地图轮换规则");
        ToolTip.SetTip(RulesButton, "仅内置地图和创意工坊合集支持轮换");
        AutomationProperties.SetName(RefreshButton, "刷新地图目录");
        ToolTip.SetTip(RefreshButton, "刷新地图目录");
        // The bar follows the table's own checkbox column; its count is the only gate for the bulk actions.
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
        try
        {
            var catalog = await ctx.Model.MapCatalogAsync(ctx.InstanceId);
            pool = await ctx.Model.MapPoolAsync(new(MapPoolAction.Read, ctx.InstanceId, ctx.PresetId));
            var poolName = "当前图池";
            allRows.Clear();
            foreach (var entry in catalog.Entries)
            {
                var inPool = pool.Pool.Maps.Any(m => MapCatalogRules.KeyOf(m) == entry.Key);
                allRows.Add(new MapRow(entry, inPool, inPool ? poolName : null));
            }
            FillPoolFilter(poolName);
            Apply();
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException or UnauthorizedAccessException)
        {
            ctx.Notify(new ToastOptions { Title = "无法读取地图", Description = e.Message, Kind = ToastKind.Error });
        }
    }

    private void FillPoolFilter(string poolName)
    {
        var field = Filters.Fields.FirstOrDefault(f => f.Id == "pool");
        if (field is null) return;
        field.Options.Clear();
        field.Options.Add(new FilterOption { Value = poolName, Label = poolName });
    }

    /// <summary>Source chips, pool chips and the search text all narrow the same catalog.</summary>
    private void Apply()
    {
        var query = Filters.SearchText ?? "";
        var chips = Filters.ActiveFilters ?? [];
        var sources = chips.Where(c => c.Id == "source").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pools = chips.Where(c => c.Id == "pool").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        visible.Clear();
        foreach (var row in allRows)
        {
            if (sources.Count > 0 && !sources.Contains(row.FilterSource)) continue;
            if (pools.Count > 0 && !row.InPool) continue;
            if (query.Length > 0 && !row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !(row.WorkshopId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            visible.Add(row);
        }
        Selection.TotalCount = visible.Count;
        Selection.SelectedCount = Table.SelectedItems?.Count ?? 0;
        UpdateActions();
    }

    private void UpdateActions()
    {
        // The bar's own count is the single source: the table's selection feeds it, and a test (or a future bulk
        // selection) can drive it directly.
        var selected = Selection.SelectedCount;
        foreach (var action in Selection.Actions)
            action.IsEnabled = action.Key switch
            {
                "switch" => selected == 1,
                "add" => selected >= 1,
                "remove" => selected >= 1,
                _ => true
            };
    }

    private void FiltersChanged(object? sender, FilterToolbarFiltersChangedEventArgs e) => Apply();

    private void SelectionChanged(object? sender, DataTableSelectionChangedEventArgs e)
    {
        Selection.SelectedCount = Table.SelectedItems?.Count ?? 0;
        UpdateActions();
    }

    private void SelectAll(object? sender, RoutedEventArgs e) => Table.SelectAll();

    private void ClearSelection(object? sender, RoutedEventArgs e) => Table.ClearSelection();

    private async void Invoked(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (context is not { } ctx) return;
        var chosen = Table.SelectedItems?.OfType<MapRow>().ToArray() ?? [];
        try
        {
            switch (e.Action.Key)
            {
                case "switch" when chosen.Length == 1:
                    await SwitchAsync(ctx, chosen[0]);
                    break;
                case "add":
                    await ChangePoolAsync(ctx, chosen, add: true);
                    break;
                case "remove":
                    await ChangePoolAsync(ctx, chosen, add: false);
                    break;
            }
        }
        finally
        {
            Table.ClearSelection();
            Selection.SelectedCount = 0;
            UpdateActions();
        }
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
            var receipt = await new ServerMapClient(ctx.Model.Server)
                .ChangeAsync(ctx.InstanceId, run, row.Target);
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

    /// <summary>Adds or removes the selection to the preset's pool; entries already in that state are skipped.</summary>
    private async Task ChangePoolAsync(MapsPageContext ctx, MapRow[] rows, bool add)
    {
        if (pool is not { } loaded) return;
        var maps = loaded.Pool.Maps.ToList();
        var changed = 0; var skipped = 0;
        foreach (var row in rows)
        {
            var present = maps.Any(m => MapCatalogRules.KeyOf(m) == row.Key);
            if (add == present) { skipped++; continue; }
            if (add) maps.Add(row.Target); else maps.RemoveAll(m => MapCatalogRules.KeyOf(m) == row.Key);
            changed++;
        }
        if (changed == 0)
        {
            ctx.Notify(new ToastOptions { Title = add ? "已在图池中" : "不在图池中", Description = "没有需要修改的条目", Kind = ToastKind.Info });
            return;
        }
        try
        {
            var command = new MapPoolCommand(MapPoolAction.Save, ctx.InstanceId, ctx.PresetId, Maps: maps.ToArray(),
                ExpectedRevision: loaded.Pool.Revision, ExpectedHash: loaded.Hash);
            pool = await ctx.Model.MapPoolAsync(command);
            foreach (var row in rows)
            {
                var present = pool.Pool.Maps.Any(m => MapCatalogRules.KeyOf(m) == row.Key);
                row.SetPool(present ? "当前图池" : null);
            }
            ctx.Notify(new ToastOptions
            {
                Title = add ? "已加入图池" : "已从图池移除",
                Description = "修改 " + changed + " 张 · 跳过 " + skipped + " 张",
                Kind = ToastKind.Success
            });
        }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
        {
            ctx.Notify(new ToastOptions { Title = "修改图池失败", Description = e.Message, Kind = ToastKind.Error });
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
