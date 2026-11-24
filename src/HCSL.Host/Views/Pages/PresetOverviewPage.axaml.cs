using Avalonia.Controls;
using Avalonia.Interactivity;
using TKCode.Arc;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class PresetOverviewPage : UserControl
{
    private bool operating, projecting;
    private int pickerGeneration;
    private MapPoolLibraryResult? pools;
    private ServerMapTarget? startMap;
    private sealed record Choice(string Key, string Label, Choice[]? Children = null,
        Guid? Pool = null, ServerMapTarget? Map = null);
    private PresetDefinition? loaded;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetOverviewPage()
    {
        InitializeComponent();
        foreach (var picker in new[] { PoolPicker, MapPicker })
        {
            picker.ChildrenSelector = item => ((Choice)item).Children;
            picker.TextSelector = item => ((Choice)item).Label;
            picker.ValueSelector = item => ((Choice)item).Key;
        }
        PoolPicker.DisabledSelector = item => ((Choice)item).Pool is null;
        MapPicker.DisabledSelector = item => ((Choice)item).Map is null;
        PoolPicker.SelectionChanged += async (_, _) => await AssignPoolAsync();
        MapPicker.SelectionChanged += async (_, _) =>
        {
            if (projecting || operating || MapPicker.SelectedItem is not Choice { Map: { } map }) return;
            await ChangeStartMapAsync(map);
        };
        Tooltip.SetTip(TypeHelp, "游戏类型编号，与 game_mode 一起决定服务器玩法。例如 0 表示经典类型。");
        Tooltip.SetTip(ModeHelp, "该游戏类型下的模式编号。例如 game_type 为 0 时，0 为休闲，1 为竞技。");
        foreach (var field in new Control[] { NameInput, TypeInput, ModeInput })
            field.LostFocus += async (_, _) => await SavePresetAsync();
        DataContextChanged += (_, _) => { if (IsVisible) Open(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) Open(); else pickerGeneration++;
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

    private void LoadFields()
    {
        loaded = Scope()?.Preset;
        NameInput.Text = loaded?.Name;
        startMap = loaded is { } preset ? new(preset.MapKind, preset.Map) : null;
        TypeInput.Text = loaded?.GameType.ToString();
        ModeInput.Text = loaded?.GameMode.ToString();
    }

    private async Task<bool> SavePresetAsync()
    {
        if (operating || Scope() is not { } scope || !scope.Servers.CanMutate || loaded?.Id != scope.Preset.Id) return false;
        var saved = false;
        await RunAsync(async () =>
        {
            if (loaded != scope.Preset)
                throw new IOException("预设已在其他位置修改，请重新打开基本信息后再编辑。");
            if (!int.TryParse(TypeInput.Text, out var type) || !int.TryParse(ModeInput.Text, out var mode))
                throw new IOException("game_type 和 game_mode 必须为整数。");
            var draft = scope.Preset with
            {
                Name = NameInput.Text?.Trim() ?? "", Map = startMap?.Value ?? scope.Preset.Map,
                MapKind = startMap?.Kind ?? scope.Preset.MapKind, GameType = type, GameMode = mode
            };
            if (draft == loaded) { saved = true; return; }
            InstanceRules.Preset(draft);
            if (!await scope.Servers.UpdatePresetAsync(scope.Instance.Id, scope.Instance.Revision, draft))
                throw new IOException(scope.Servers.Error);
            if (Scope() is { } current && current.Preset.Id == draft.Id) loaded = current.Preset;
            saved = true;
            Owner?.ShowInstanceNotification("预设已保存");
        });
        return saved;
    }

    private Choice[] mapChoices = [];

    private async void Open()
    {
        var ticket = ++pickerGeneration;
        pools = null;
        mapChoices = [];
        LoadFields();
        RenderPools();
        RenderMaps(mapChoices);
        if (Scope() is not { } scope) return;
        bool Current() => ticket == pickerGeneration && Scope()?.Preset.Id == scope.Preset.Id;
        await Task.WhenAll(ReadPools(), ReadMaps());

        async Task ReadPools()
        {
            try
            {
                var result = await scope.Servers.MapPoolsAsync(new(MapPoolLibraryAction.List, scope.Instance.Id));
                if (!Current()) return;
                pools = result;
                RenderPools();
            }
            catch (IOException e) { if (Current()) Owner?.ShowInstanceNotification("无法读取图池", e.Message); }
        }

        async Task ReadMaps()
        {
            try
            {
                var catalog = await scope.Servers.MapCatalogAsync(scope.Instance.Id);
                if (!Current()) return;
                mapChoices = catalog.Entries.Where(entry => entry.Source == MapCatalogSource.Standard ||
                        entry.Source == MapCatalogSource.Workshop && !string.IsNullOrEmpty(entry.WorkshopId))
                    .DistinctBy(entry => entry.Key).Select(entry => new Choice(entry.Key, entry.Name,
                        Map: entry.Source == MapCatalogSource.Workshop
                            ? new(ServerMapKind.Workshop, entry.WorkshopId!) : new(ServerMapKind.Standard, entry.Name))).ToArray();
                RenderMaps(mapChoices);
            }
            catch (IOException e) { if (Current()) Owner?.ShowInstanceNotification("无法读取地图目录", e.Message); }
        }
    }

    private void RenderPools()
    {
        var rows = pools?.Library.Pools.Select(pool => new Choice(pool.Id.ToString(), pool.Name,
            pool.Maps.Select((map, index) => new Choice($"{pool.Id}:{index}", ServerMapRules.Label(map))).ToArray(),
            Pool: pool.Id)).ToArray() ?? [];
        var assigned = Scope() is { } scope ? pools?.Library.PoolOf(scope.Preset.Id)?.Id : null;
        Project(() =>
        {
            PoolPicker.ItemsSource = rows;
            PoolPicker.SelectedItem = rows.FirstOrDefault(row => row.Pool == assigned);
        });
    }

    private void RenderMaps(Choice[] choices) => Project(() =>
    {
        var available = choices;
        if (startMap is { } map && !available.Any(row => row.Map == map))
            available = [.. choices, new Choice("current", ServerMapRules.Label(map), Map: map)];
        MapPicker.ItemsSource = new[]
        {
            new Choice("standard", "内置地图", available.Where(row => row.Map?.Kind == ServerMapKind.Standard).ToArray()),
            new Choice("workshop", "工坊地图", available.Where(row => row.Map?.Kind == ServerMapKind.Workshop).ToArray())
        };
        MapPicker.SelectedItem = available.FirstOrDefault(row => row.Map == startMap);
    });

    private void Project(Action update)
    {
        projecting = true;
        try { update(); }
        finally { projecting = false; }
    }

    private async Task AssignPoolAsync()
    {
        if (projecting || operating || pools is not { } snapshot || Scope() is not { } scope ||
            PoolPicker.SelectedItem is not Choice { Pool: { } poolId } || snapshot.Library.PoolOf(scope.Preset.Id)?.Id == poolId) return;
        if (scope.Servers.CanEditMapPool)
        {
            await RunAsync(async () =>
            {
                var result = await scope.Servers.MapPoolsAsync(new(MapPoolLibraryAction.AssignPreset,
                    scope.Instance.Id, scope.Instance.Revision, poolId, scope.Preset.Id,
                    ExpectedRevision: snapshot.Library.Revision, ExpectedHash: snapshot.Hash));
                if (Scope()?.Preset.Id == scope.Preset.Id) pools = result;
            });
        }
        if (Scope()?.Preset.Id == scope.Preset.Id) RenderPools();
    }

    private async void EnterWorkshopId(object? sender, RoutedEventArgs e)
    {
        if (operating || Scope() is not { } scope || !scope.Servers.CanMutate || Owner is not { } owner) return;
        var value = await owner.TextAsync("启动地图", "工坊地图 ID", maxLength: 20);
        if (value is null || Scope()?.Preset.Id != scope.Preset.Id) return;
        await ChangeStartMapAsync(new(ServerMapKind.Workshop, value));
    }

    private async Task ChangeStartMapAsync(ServerMapTarget map)
    {
        var ticket = pickerGeneration;
        var previous = startMap;
        startMap = map;
        var saved = await SavePresetAsync();
        if (ticket != pickerGeneration) return;
        if (!saved) startMap = previous;
        RenderMaps(mapChoices);
    }

    private async void OpenCfg(object? sender, RoutedEventArgs e) => await OpenDirectoryAsync(cfg: true);
    private async void OpenAddons(object? sender, RoutedEventArgs e) => await OpenDirectoryAsync(cfg: false);

    private Task OpenDirectoryAsync(bool cfg) => RunAsync(async () =>
    {
        if (Scope() is not { } scope || !scope.Servers.Server.Connected || scope.Servers.Busy) return;
        var ok = cfg ? await scope.Servers.OpenPresetCfgAsync(scope.Instance.Id, scope.Preset.Id)
            : await scope.Servers.OpenPresetDirectoryAsync(scope.Instance.Id, scope.Preset.Id, "addons");
        if (!ok) throw new IOException(scope.Servers.Error);
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        try { await action(); }
        catch (Exception e) when (e is IOException or InstanceException or InvalidOperationException)
        { Owner?.ShowInstanceNotification("操作未完成", e.Message); }
        finally { operating = false; }
    }
}
