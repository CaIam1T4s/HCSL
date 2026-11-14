using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

/// <summary>Home projections over existing instance/preset/map-pool use cases.</summary>
public partial class HomePage : UserControl
{
    private HostServerController? model;
    private bool updating, editing, reading, hasCollection;
    private (Guid Instance, Guid Preset, long Revision)? loaded;
    private ServerMapTarget[] maps = [];
    private string? failure, configurationFailure;
    private int generation;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private PresetDefinition? CurrentPreset => model?.Selected?.Entry.Presets
        .FirstOrDefault(p => p.Id == model.Selected.Entry.Definition?.SelectedPresetId);

    public HomePage()
    {
        InitializeComponent();
        // Arc 1.1.0 measures the action using PART_Row.Margin, not Button.Padding.
        // Forward the dynamic Arc padding preset without imposing a minimum width.
        LaunchButton.Loaded += (_, _) =>
        {
            var row = LaunchButton.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "PART_Row");
            row.Bind(MarginProperty, LaunchButton.GetObservable(PaddingProperty));
            row.HorizontalAlignment = LaunchButton.IsExpanded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        };
        LaunchButton.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "IsExpanded" &&
                LaunchButton.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "PART_Row") is { } row)
                row.HorizontalAlignment = LaunchButton.IsExpanded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        };
        ConfigurationPopover.Closed += (_, _) =>
            InstancePicker.IsOpen = PresetPicker.IsOpen = MapPicker.IsOpen = false;
        InstancePicker.TextSelector = value => (value as HostInstanceItem)?.Entry.Definition?.Name ?? value?.ToString();
        PresetPicker.TextSelector = value => (value as PresetDefinition)?.Name;
        MapPicker.TextSelector = value => value is ServerMapTarget map ? ServerMapRules.Label(map) : null;
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) =>
        {
            if (model is not null) model.PropertyChanged -= Changed;
            ConfigurationPopover.IsOpen = false;
            model = null; generation++; loaded = null;
        };
        InstancePicker.SelectionChanged += (_, _) =>
        {
            if (!updating && !editing && model is { } m)
            {
                m.Selected = InstancePicker.SelectedItem as HostInstanceItem;
                Update();
            }
        };
        PresetPicker.SelectionChanged += async (_, _) =>
        {
            if (!updating && PresetPicker.SelectedItem is PresetDefinition preset)
                await SelectPresetAsync(preset);
        };
        MapPicker.SelectionChanged += async (_, _) =>
        {
            if (!updating && MapPicker.SelectedItem is ServerMapTarget map)
                await SelectMapAsync(map);
        };
    }

    private void Subscribe()
    {
        if (model is not null) model.PropertyChanged -= Changed;
        model = (DataContext as ShellViewModel)?.Servers;
        if (model is not null) model.PropertyChanged += Changed;
        loaded = null; generation++;
        Update();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Update);
    private void Update()
    {
        if (model is not { } m) return;
        var instance = m.Selected?.Entry.Definition;
        var preset = CurrentPreset;
        var hasInstance = instance is not null;
        var selectable = !editing && !reading && !m.Busy && !m.Server.Busy;
        var writable = m.CanMutate && selectable;
        updating = true;
        try
        {
            InstancePicker.ItemsSource = m.Instances;
            InstancePicker.SelectedItem = m.Selected;
            PresetPicker.ItemsSource = m.Selected?.Entry.Presets;
            PresetPicker.SelectedItem = preset;
            MapPicker.ItemsSource = maps;
            MapPicker.SelectedItem = maps.FirstOrDefault(x => x.Kind == preset?.MapKind && x.Value == preset.Map);
            InstancePicker.IsVisible = PresetPicker.IsVisible = VacSwitch.IsVisible = LaunchActions.IsVisible = LaunchButton.IsVisible = hasInstance;
            ConfigurationButton.IsEnabled = hasInstance;
            if (!hasInstance) ConfigurationPopover.IsOpen = false;
            CreateButton.IsVisible = !hasInstance;
            InstanceRow.IsVisible = PresetRow.IsVisible = hasInstance;
            InstancePicker.IsEnabled = PresetPicker.IsEnabled = selectable;
            MapRow.IsVisible = MapPicker.IsVisible = hasInstance && maps.Length > 0;
            MapPicker.IsEnabled = selectable && failure is null;
            PoolButton.IsVisible = hasInstance && preset is not null && instance is not null && !reading
                && loaded == (instance.Id, preset.Id, preset.Revision)
                && failure is null && maps.Length == 0 && !hasCollection;
            PoolButton.IsEnabled = selectable && PoolButton.IsVisible;
            RetryButton.IsVisible = hasInstance && failure is not null;
            PoolNotice.IsVisible = hasInstance && hasCollection;
            RetryButton.IsEnabled = !m.Busy && !reading;
            LaunchButton.IsDisabled = !(writable && preset is not null && maps.Any(x => x.Kind == preset.MapKind && x.Value == preset.Map)
                && m.Server.CanStart);
            LaunchButton.Label = m.Server.Active ? "服务器运行中" : reading ? "读取地图池…" : "启动服务器";
            Feedback.Content = failure ?? configurationFailure ?? (m.Error.Length > 0 ? m.Error : null);
            Feedback.IsVisible = Feedback.Content is not null;
        }
        finally { updating = false; }
        if (instance is not null && preset is not null && m.Server.Connected && !m.Busy && !reading && !editing)
        {
            var key = (instance.Id, preset.Id, preset.Revision);
            if (loaded != key) _ = ReadPoolAsync();
        }
    }

    private async Task ReadPoolAsync()
    {
        if (reading || model is not { } m || m.Selected?.Entry.Definition is not { } instance || CurrentPreset is not { } preset) return;
        var ticket = ++generation;
        loaded = (instance.Id, preset.Id, preset.Revision);
        reading = true; maps = []; failure = null; hasCollection = false; Update();
        try
        {
            var result = await m.MapPoolAsync(new(MapPoolAction.Read, instance.Id, preset.Id));
            if (ticket == generation && m.Selected?.Entry.Id == instance.Id && CurrentPreset?.Id == preset.Id)
            {
                hasCollection = result.Pool.Maps.Any(x => x.Kind == ServerMapKind.WorkshopCollection);
                maps = result.Pool.Maps.Where(x => x.Kind != ServerMapKind.WorkshopCollection).ToArray();
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        { if (ticket == generation) failure = e.Message; }
        finally { reading = false; if (ticket == generation) Update(); }
    }

    private async Task SelectPresetAsync(PresetDefinition preset)
    {
        if (editing || model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance || preset.Id == instance.SelectedPresetId) return;
        if (!CanChangeConfiguration(m)) return;
        ConfigurationPopover.IsOpen = false;
        editing = true; Update();
        try
        {
            if (await owner.ChooseAsync("选择配置预设", $"选择“{preset.Name}”？下次启动生效，不会立即换图。", "取消", "选择") == 1
                && m.Selected?.Entry.Id == instance.Id)
            {
                if (!await m.SelectPresetAsync(instance.Id, instance.Revision, preset.Id)) failure = m.Error;
                else { loaded = null; maps = []; }
            }
        }
        finally { editing = false; Update(); }
    }
    private async Task SelectMapAsync(ServerMapTarget map)
    {
        if (editing || model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance || CurrentPreset is not { } preset ||
            (preset.MapKind == map.Kind && preset.Map == map.Value)) return;
        if (!CanChangeConfiguration(m)) return;
        ConfigurationPopover.IsOpen = false;
        editing = true; Update();
        try
        {
            if (await owner.ChooseAsync("选择启动地图", $"保存启动地图为“{ServerMapRules.Label(map)}”？下次启动生效。", "取消", "保存") == 1
                && m.Selected?.Entry.Id == instance.Id && CurrentPreset?.Id == preset.Id)
            {
                if (!await m.UpdatePresetAsync(instance.Id, instance.Revision, preset with { MapKind = map.Kind, Map = map.Value })) failure = m.Error;
                else loaded = null;
            }
        }
        finally { editing = false; Update(); }
    }
    private bool CanChangeConfiguration(HostServerController controller)
    {
        configurationFailure = null;
        if (controller.CanMutate) return true;
        configurationFailure = "当前无法保存启动配置；请连接宿主、停止服务器并完成正在进行的操作或恢复。";
        Update();
        return false;
    }
    private void ToggleConfiguration(object? sender, RoutedEventArgs e)
    {
        if (ConfigurationPopover.IsOpen) ConfigurationPopover.IsOpen = false;
        else ConfigurationPopover.ShowAt(LaunchAnchor);
    }
    private async void Launch(object? sender, RoutedEventArgs e)
    {
        if (!LaunchButton.IsDisabled && Owner is { } owner)
        {
            ConfigurationPopover.IsOpen = false;
            await owner.StartHomeServerAsync();
        }
    }
    private void ManagePool(object? sender, RoutedEventArgs e)
    {
        if (!PoolButton.IsEnabled) return;
        ConfigurationPopover.IsOpen = false;
        (DataContext as ShellViewModel)?.NavigateCommand.Execute("instance");
    }
    private void CreateInstance(object? sender, RoutedEventArgs e) => (DataContext as ShellViewModel)?.NavigateCommand.Execute("instances");
    private async void RetryPool(object? sender, RoutedEventArgs e) => await ReadPoolAsync();
}
