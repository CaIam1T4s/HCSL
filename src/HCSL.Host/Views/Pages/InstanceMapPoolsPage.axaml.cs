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
/// checkboxes select maps for bulk pool operations. A preset only chooses a pool; it never edits one.
/// </summary>
public partial class InstanceMapPoolsPage : UserControl
{
    private HostServerController? subscribed;
    private Guid? addTarget;
    private bool updatingChoices;
    private MapPoolRow? browsed;
    private MapsPage? maps;
    private bool operating, loaded;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private InstanceDefinition? Instance => Model?.Selected?.Entry.Definition;

    public InstanceMapPoolsPage()
    {
        InitializeComponent();
        CreateButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        AddMapsButton.Icon = Lucide.Geometry(LucideIconKind.Map);
        RenameButton.Icon = Lucide.Geometry(LucideIconKind.Pencil);
        DeleteAction.Icon = DeleteButton.Icon = Lucide.Geometry(LucideIconKind.Trash);
        PoolActions.DefaultExpandedItem = CreateButton;
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
            SetChoices(result);
            await RefreshMapsAsync();
        }
        catch (IOException error) { Error("读取图池失败", error.Message); loaded = false; }
        finally { loading = false; Update(); }
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
            var wanted = browsed?.Pool.Id;
            var choices = rows;
            EmptyPools.IsVisible = choices.Length == 0;
            updatingChoices = true;
            try
            {
                PoolChoices.ItemsSource = choices;
                var choice = choices.FirstOrDefault(c => c.Pool.Id == wanted) ?? choices.FirstOrDefault();
                browsed = choice;
                PoolChoices.SelectedItem = choice;
            }
            finally { updatingChoices = false; Update(); }
    }

    private bool loading;

    private void PoolSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (updatingChoices || PoolChoices.SelectedItem is not MapPoolRow choice) return;
        DeletePopover.IsOpen = false;
        browsed = choice;
        Update();
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
        AddMapsButton.IsEnabled = connected && browsed is not null;
        RenameButton.IsEnabled = writable && browsed is not null;
        DeleteAction.IsEnabled = DeleteButton.IsEnabled = writable && browsed is not null;
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

    private void OpenDelete(object? sender, RoutedEventArgs e)
    {
        if (!DeleteAction.IsEnabled || browsed is null) return;
        DeleteButton.IsConfirmed = false;
        DeletePopover.ShowAt(DeleteAction);
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
