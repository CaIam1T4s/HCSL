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

/// <summary>
/// 地图池 of an instance: the library (create, rename, delete) plus the console map page in pool-editing mode, where the
/// checkboxes add and remove maps. A preset only chooses a pool; it never edits one.
/// </summary>
public partial class InstanceMapPoolsPage : UserControl
{
    private HostServerController? subscribed;
    private MapPoolLibrary? library;
    private MapPoolRow[] rows = [];
    private MapPoolRow? browsed;
    private MapsPage? maps;
    private bool operating, loaded;
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

    private bool loading;

    private async void PoolSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (PoolChoices.SelectedItem is not MapPoolRow row) return;
        browsed = row;
        Update();
        await ShowMapsAsync(row.Pool);
    }

    /// <summary>The map table is the console page in pool-editing mode: one instance of it, re-attached per pool.</summary>
    private async Task ShowMapsAsync(MapPoolRecord pool)
    {
        if (Model is not { } m || Instance is not { } instance) return;
        maps ??= new MapsPage();
        if (!ReferenceEquals(MapsHost.Content, maps)) MapsHost.Content = maps;
        var preset = instance.SelectedPresetId;
        await maps.AttachAsync(new MapsPageContext(m, instance.Id, preset,
            options => Owner?.ShowInstanceNotification(options.Title, options.Description, options.Kind), pool.Id));
        MapsText.Text = pool.Maps.Length == 0 ? "空图池：勾选地图即可加入" : "";
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var connected = !operating && m.Server.Connected;
        var writable = connected && m.CanEditMapPool;
        CreateButton.IsEnabled = writable;
        RenameButton.IsEnabled = writable && browsed is not null;
        DeleteButton.IsEnabled = writable && browsed is not null;
        RefreshButton.IsEnabled = connected;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    /// <summary>All pool writes share one shape: send the command, report the failure, then reload library and table.</summary>
    private async Task WriteAsync(Func<InstanceDefinition, MapPoolLibraryCommand> command)
    {
        if (operating || Model is not { } m || Instance is not { } instance) return;
        operating = true;
        Update();
        try { await m.MapPoolsAsync(command(instance)); }
        catch (IOException error) { Error("图池未保存", error.Message); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); await RefreshMapsAsync(); }
    }

    private async Task RefreshMapsAsync()
    {
        if (browsed is { } row) await ShowMapsAsync(row.Pool);
        else MapsHost.Content = null;
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

    private async void DeletePool(object? sender, RoutedEventArgs e)
    {
        if (operating || browsed is null) return;
        var pool = browsed.Pool;
        DeleteButton.IsConfirmed = false;
        await WriteAsync(instance => new MapPoolLibraryCommand(MapPoolLibraryAction.Delete,
            instance.Id, instance.Revision, pool.Id));
    }

    private async void Refresh(object? sender, RoutedEventArgs e)
    {
        if (operating) return;
        loaded = false;
        await LoadAsync();
        await RefreshMapsAsync();
    }
}
