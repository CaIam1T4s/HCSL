using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

/// <summary>地图池: the instance-wide pool library. Pools are shared by every preset of this instance.</summary>
public partial class InstanceMapPoolsPage : UserControl
{
    private HostServerController? subscribed;
    private MapPoolLibrary? library;
    private MapPoolRow[] rows = [];
    private MapPoolRow? browsed;
    private bool operating, loaded, loading;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private InstanceDefinition? Instance => Model?.Selected?.Entry.Definition;

    public InstanceMapPoolsPage()
    {
        InitializeComponent();
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
        // The library only depends on the selected instance. Reloading on every notification (Busy in particular) would
        // read it forever.
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
            var result = await m.MapPoolsAsync(new MapPoolLibraryCommand(MapPoolLibraryAction.List, instance.Id));
            library = result.Library;
            var assignments = result.Library.Assignments;
            rows = result.Library.Pools.Select(pool =>
            {
                var used = assignments.Count(pair => pair.Value == pool.Id);
                return new MapPoolRow(pool,
                    pool.Maps.Length == 0 ? "空" : $"{pool.Maps.Length} 张",
                    used == 0 ? "未使用" : $"被 {used} 个预设使用",
                    pool.Maps.Length == 0 ? BadgeTone.Warning : BadgeTone.Neutral);
            }).ToArray();
            var wanted = browsed?.Pool.Id;
            PoolChoices.ItemsSource = rows;
            browsed = rows.FirstOrDefault(row => row.Pool.Id == wanted) ?? rows.FirstOrDefault();
            PoolChoices.SelectedItem = browsed;
        }
        catch (IOException error) { Error("读取图池失败", error.Message); loaded = false; }
        finally { loading = false; Update(); }
    }

    private void PoolSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PoolChoices.SelectedItem is not MapPoolRow row) return;
        browsed = row;
        Update();
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var connected = !operating && m.Server.Connected;
        var writable = connected && m.CanEditMapPool;
        var preset = Instance is { } definition
            ? m.Selected?.Entry.Presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId)
            : null;
        CreateButton.IsEnabled = writable;
        RenameButton.IsEnabled = writable && browsed is not null;
        AssignButton.IsEnabled = writable && browsed is not null && preset is not null && library?.PoolOf(preset.Id)?.Id != browsed.Pool.Id;
        DeleteButton.IsEnabled = writable && browsed is not null;
        RefreshButton.IsEnabled = connected;
        MapsText.Text = browsed is not { Pool.Maps.Length: > 0 } row
            ? "该图池还没有地图"
            : string.Join(" · ", row.Pool.Maps.Select(ServerMapRules.Label));
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    /// <summary>All writes share one shape: send the command, report the failure, then reload the library.</summary>
    private async Task WriteAsync(Func<InstanceDefinition, MapPoolLibraryCommand> command)
    {
        if (operating || Model is not { } m || Instance is not { } instance) return;
        operating = true;
        Update();
        try { await m.MapPoolsAsync(command(instance)); }
        catch (IOException error) { Error("图池未保存", error.Message); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }

    private async void CreatePool(object? sender, RoutedEventArgs e)
    {
        if (operating || Instance is null || Owner is not { } owner) return;
        if (await owner.TextAsync("新建图池", "图池名称（1–40 字符）", null, MapPoolLibraryRules.MaxNameLength) is not { } name) return;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Create,
            instance.Id, instance.Revision, Name: name));
        Owner.ShowInstanceNotification("图池已创建", name, ToastKind.Success);
    }

    private async void RenamePool(object? sender, RoutedEventArgs e)
    {
        if (operating || Instance is null || Owner is not { } owner || browsed is null) return;
        var pool = browsed.Pool;
        if (await owner.TextAsync("重命名图池", "新的名称（1–40 字符）", pool.Name, MapPoolLibraryRules.MaxNameLength) is not { } name) return;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Rename,
            instance.Id, instance.Revision, pool.Id, Name: name));
    }

    private async void DeletePool(object? sender, RoutedEventArgs e)
    {
        if (operating || browsed is null) return;
        var pool = browsed.Pool;
        DeleteButton.IsConfirmed = false;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Delete,
            instance.Id, instance.Revision, pool.Id));
    }

    private async void AssignPool(object? sender, RoutedEventArgs e)
    {
        if (operating || Instance is not { } current || browsed is null) return;
        var preset = Model?.Selected?.Entry.Presets.FirstOrDefault(p => p.Id == current.SelectedPresetId);
        if (preset is null) return;
        var pool = browsed.Pool;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.AssignPreset,
            instance.Id, instance.Revision, pool.Id, preset.Id));
    }

    private async void Refresh(object? sender, RoutedEventArgs e)
    {
        if (operating) return;
        loaded = false;
        await LoadAsync();
    }
}
