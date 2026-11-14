using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using HCSL.UI.Shared.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;

namespace HCSL.Host.Views.Pages;

public partial class InstancesPage : UserControl
{
    private static readonly StyledProperty<BoxShadows> SubtleShadowProperty =
        AvaloniaProperty.Register<InstancesPage, BoxShadows>("SubtleShadow");
    private ShellViewModel? shell;
    private bool operating, refreshing, updating;
    private readonly Dictionary<Guid, InstanceListRow> rows = [];
    private readonly ObservableCollection<InstanceListRow> visibleRows = [];
    private readonly AnimatedItemReorder reorder;
    private InstanceDefinition? pendingDelete;
    private Guid? propertiesId;
    private GlassMenu? rowMenu, sortMenu;
    private int sortMode; // Name descending, name ascending, status ascending, status descending.
    private HostServerController? Model => shell?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public InstancesPage()
    {
        InitializeComponent();
        reorder = new AnimatedItemReorder(InstanceChoices);
        InstanceChoices.ItemsSource = visibleRows;
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        CreateButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        SortButton.Icon = Lucide.Geometry(LucideIconKind.ArrowDown);
        ToolbarActions.DefaultExpandedItem = SortButton;
        ToolbarActions.SizeChanged += (_, _) =>
        {
            // Arc recommends a stable start edge. Reserve the widest measured expansion so a shorter label
            // cannot shift a right-aligned group out from under the pointer and bounce its hover intent.
            if (ToolbarActions.IsVisible && ToolbarActions.Bounds.Width > ToolbarSlot.MinWidth)
                ToolbarSlot.MinWidth = ToolbarActions.Bounds.Width;
        };
        DeleteConfirm.Icon = Lucide.Geometry(LucideIconKind.Trash);
        DeletePopover.Closed += (_, _) => { if (!operating) { pendingDelete = null; DeleteConfirm.IsConfirmed = false; } };
        PropertiesPopover.Closed += (_, _) => propertiesId = null;
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => { Unsubscribe(); CloseOverlays(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible) CloseOverlays();
            if (e.Property == SubtleShadowProperty)
            {
                // Use Arc's quietest elevation throughout this page, including its native glass overlays.
                Resources[ArcDesignTokens.Key(ArcDesignTokenKind.ShadowRaised)] = GetValue(SubtleShadowProperty);
                Resources[ArcDesignTokens.Key(ArcDesignTokenKind.ShadowFloating)] = GetValue(SubtleShadowProperty);
            }
        };
        ArcTokens.Bind(this, SubtleShadowProperty, ArcDesignTokenKind.ShadowResting);
    }

    private void CloseOverlays()
    {
        rowMenu?.Close(); sortMenu?.Close();
        reorder.Reset();
        DeletePopover.IsOpen = PropertiesPopover.IsOpen = false;
    }
    private void Unsubscribe()
    {
        if (shell is null) return;
        shell.Servers.PropertyChanged -= Changed;
        shell.PropertyChanged -= Changed;
        shell = null;
    }
    private void Subscribe()
    {
        Unsubscribe();
        shell = DataContext as ShellViewModel;
        if (shell is not null)
        {
            shell.Servers.PropertyChanged += Changed;
            shell.PropertyChanged += Changed;
        }
        Update();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(Update);
    private void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (InstanceChoices is null) return;
        CloseOverlays();
        Update();
    }
    private void Update()
    {
        if (Model is not { } model || shell is null) return;
        updating = true;
        try
        {
            // Keep the toolbar's containers, focus and current expansion while its own list request is pending.
            if (!refreshing)
            {
                CreateButton.IsVisible = !operating && model.CanCreate;
                RefreshButton.IsVisible = !operating && !model.Busy;
                SortButton.IsVisible = !operating && !model.Busy && model.Instances.Count > 0;
                ToolbarActions.IsVisible = RefreshButton.IsVisible || CreateButton.IsVisible || SortButton.IsVisible;
                ToolbarActions.DefaultExpandedItem = SortButton.IsVisible ? SortButton : CreateButton.IsVisible ? CreateButton : RefreshButton;
            }
            // IsDisabled blocks duplicate activation but, unlike IsEnabled/IsVisible, does not discard focus.
            RefreshButton.IsDisabled = CreateButton.IsDisabled = SortButton.IsDisabled = refreshing;
            LoadingRegion.IsLoading = refreshing;
            foreach (var item in model.Instances)
            {
                if (!rows.TryGetValue(item.Entry.Id, out var row)) rows[item.Entry.Id] = row = new(item);
                row.Update(item, model, shell.StartupInstanceId, operating);
            }
            foreach (var id in rows.Keys.Where(id => model.Instances.All(i => i.Entry.Id != id) && visibleRows.All(r => r.Id != id)).ToArray()) rows.Remove(id);
            var query = (Search.Text ?? "").Trim();
            var filtered = model.Instances.Select(i => rows[i.Entry.Id])
                .Where(r => (r.Name + " " + r.Subtitle).Contains(query, StringComparison.CurrentCultureIgnoreCase));
            var ordered = sortMode switch
            {
                1 => filtered.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
                2 => filtered.OrderBy(r => r.StatusOrder == int.MaxValue).ThenBy(r => r.StatusOrder).ThenByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
                3 => filtered.OrderBy(r => r.StatusOrder == int.MaxValue).ThenByDescending(r => r.StatusOrder).ThenByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => filtered.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            };
            var next = ordered.ThenBy(r => r.Id).ToArray();
            // Cancelled exits (filter cleared / refresh returned the row) reuse the same row and container.
            foreach (var row in next) row.SetPresent(true);
            var desired = next.ToList();
            foreach (var row in visibleRows.Where(r => !next.Contains(r)).ToArray())
            {
                var container = InstanceChoices.ContainerFromItem(row) as Control;
                var presence = container?.GetVisualDescendants()
                    .OfType<PresenceHost>().FirstOrDefault(p => p.Name == "RowPresence");
                if (!refreshing && IsEffectivelyVisible && presence is { IsLoaded: true } &&
                    ArcMotion.GetEffectiveLevel(presence) == ArcMotionLevel.Full)
                {
                    // PresenceHost's default Collapse=None fades in place, so remaining rows keep their geometry
                    // until the exit completes; the container is never given a sticky local height.
                    row.SetPresent(false);
                    if (presence.Progress > 0)
                        desired.Insert(Math.Min(visibleRows.IndexOf(row), desired.Count), row);
                }
                else row.SetPresent(false);
            }
            if (!desired.SequenceEqual(visibleRows))
            {
                reorder.Capture();
                foreach (var row in visibleRows.Where(r => !desired.Contains(r)).ToArray()) visibleRows.Remove(row);
                for (var i = 0; i < desired.Count; i++)
                {
                    var previous = visibleRows.IndexOf(desired[i]);
                    if (previous < 0) visibleRows.Insert(i, desired[i]);
                    else if (previous != i) visibleRows.Move(previous, i);
                }
            }
            InstanceChoices.SelectedItem = visibleRows.FirstOrDefault(r => r.IsPresent && r.Id == shell.StartupInstanceId);
            InstanceChoices.IsVisible = visibleRows.Count > 0;
            Empty.IsVisible = visibleRows.Count == 0;
            Empty.Title = model.Instances.Count == 0 ? "暂无实例" : "没有匹配的实例";
            Empty.Description = model.Instances.Count == 0 ? "创建一个实例以开始使用。" : "尝试其他名称、预设或端口，或清除搜索。";
            if (pendingDelete is { } target && Current(target.Id)?.Revision != target.Revision)
            {
                DeletePopover.IsOpen = false;
                pendingDelete = null;
            }
            if (propertiesId is { } propertyTarget && model.Instances.All(i => i.Entry.Id != propertyTarget)) PropertiesPopover.IsOpen = false;
        }
        finally { updating = false; }
    }

    private void RowExited(object? sender, EventArgs e)
    {
        if (sender is not PresenceHost { DataContext: InstanceListRow row }) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (row.IsPresent || !visibleRows.Contains(row)) return;
            reorder.Capture();
            visibleRows.Remove(row);
            Update();
        });
    }

    private void StartupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (updating || shell is null || InstanceChoices.SelectedItem is not InstanceListRow row) return;
        if (!row.CanSelect || !shell.SelectStartupInstance(row.Item))
            Error("无法选择启动实例", "请等待当前操作完成后重试。");
        Update();
    }
    private void OpenSortMenu(object? sender, RoutedEventArgs e)
    {
        if (!SortButton.IsVisible) return;
        rowMenu?.Close(); sortMenu?.Close();
        sortMenu = new GlassMenu { Name = "InstanceSortMenu" };
        AutomationProperties.SetName(sortMenu, "实例排序");
        var labels = new[] { "名称降序", "名称升序", "运行状态升序", "运行状态降序" };
        for (var i = 0; i < labels.Length; i++)
        {
            var mode = i;
            var item = new GlassMenuItem
            {
                Content = labels[i] + (sortMode == i ? "（当前）" : ""),
                Icon = Lucide.Geometry(i is 1 or 2 ? LucideIconKind.ArrowUp : LucideIconKind.ArrowDown),
                // Rows still fading out are no longer part of the list and must not enable a sort choice.
                IsEnabled = visibleRows.Any(r => r.IsPresent) && (i < 2 || visibleRows.Any(r => r.IsPresent && r.StatusOrder != int.MaxValue))
            };
            item.Click += (_, _) =>
            {
                if (!item.IsEnabled) return;
                sortMode = mode;
                SortButton.Label = labels[mode];
                SortButton.Icon = Lucide.Geometry(mode is 1 or 2 ? LucideIconKind.ArrowUp : LucideIconKind.ArrowDown);
                sortMenu?.Close();
                Update();
            };
            sortMenu.Items.Add(item);
        }
        sortMenu.Open(SortButton);
    }
    private void RowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var container = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<RadioGroupItem>().FirstOrDefault();
        if (container is null && InstanceChoices.SelectedItem is { } selected)
            container = InstanceChoices.ContainerFromItem(selected) as RadioGroupItem;
        if (container?.DataContext is not InstanceListRow row) return;
        e.Handled = true;
        sortMenu?.Close(); rowMenu?.Close();
        var menu = rowMenu = new GlassMenu
        {
            Name = "InstanceRowMenu",
            Placement = e.TryGetPosition(container, out _) ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight
        };
        AutomationProperties.SetName(menu, row.OperationsLabel);
        Point? menuOrigin = null;
        void CaptureMenuOrigin()
        {
            if (menu.IsAttachedToVisualTree())
                menuOrigin = PropertyAnchorLayer.PointToClient(menu.PointToScreen(default));
        }
        menu.LayoutUpdated += (_, _) => { if (menu.IsOpen) CaptureMenuOrigin(); };
        void Entry(string title, LucideIconKind icon, string? enabled, Action action, bool separator = false, bool danger = false)
        {
            if (separator) menu.Items.Add(new GlassMenuSeparator());
            var entry = new GlassMenuItem { Content = title, Icon = Lucide.Geometry(icon), IsDestructive = danger };
            if (enabled is not null) entry.Bind(IsEnabledProperty, new Binding(enabled) { Source = row, Mode = BindingMode.OneWay });
            entry.Click += (_, _) =>
            {
                if (!entry.IsEnabled) return;
                CaptureMenuOrigin();
                menu.Close();
                action();
            };
            menu.Items.Add(entry);
        }
        Entry("启动实例", LucideIconKind.Play, nameof(row.CanStart), async () => await StartAsync(row));
        Entry("查看日志", LucideIconKind.ScrollText, nameof(row.CanViewLogs), () =>
        {
            if (row.CanViewLogs && Model?.Server.Snapshot?.InstanceId == row.Id && Browse(row)) Owner?.OpenSelectedInstanceLogs();
        });
        Entry("实例管理", LucideIconKind.Settings, nameof(row.CanSelect), () => { if (Browse(row)) shell!.NavigateCommand.Execute("instance"); });
        Entry("重命名实例", LucideIconKind.PencilLine, nameof(row.CanEdit), async () => await EditAsync(row));
        if (row.Item.Entry.Definition?.LayoutVersion < InstanceRules.CurrentLayoutVersion)
            Entry("迁移布局", LucideIconKind.FolderSync, nameof(row.CanMigrate), async () => await MigrateAsync(row));
        Entry("属性", LucideIconKind.Info, null, () => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !rows.TryGetValue(row.Id, out var current) || !container.IsAttachedToVisualTree()) return;
            propertiesId = row.Id;
            PropertiesCard.DataContext = current;
            var origin = menuOrigin ?? container.TranslatePoint(default, PropertyAnchorLayer) ?? default;
            Canvas.SetLeft(PropertiesAnchor, origin.X);
            Canvas.SetTop(PropertiesAnchor, origin.Y - PropertiesAnchor.Height);
            PropertyAnchorLayer.UpdateLayout();
            PropertiesPopover.ShowAt(PropertiesAnchor);
        }), true);
        Entry("删除实例", LucideIconKind.Trash, nameof(row.CanEdit), () => Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !container.IsAttachedToVisualTree() || operating || Model?.CanCreate != true || Current(row.Id) is not { } target) return;
            if (row.Item.Entry.Definition?.Revision != target.Revision)
            {
                Error("实例信息已变化", "请刷新列表后重试。");
                return;
            }
            pendingDelete = target;
            DeleteConfirm.IsConfirmed = false;
            AutomationProperties.SetName(DeleteConfirm, "长按删除实例：" + target.Name);
            DeletePopover.ShowAt(container);
        }), true, true);
        menu.Open(container);
    }

    private InstanceDefinition? Current(Guid id) => Model?.Instances.FirstOrDefault(i => i.Entry.Id == id)?.Entry.Definition;
    private void Error(string title, string? detail) => Owner?.ShowInstanceNotification(title, detail);
    private bool Browse(InstanceListRow row)
    {
        if (operating || shell is null || !row.CanSelect || shell.Servers.Instances.FirstOrDefault(i => i.Entry.Id == row.Id) is not { } item) return false;
        shell.RememberStartupInstance();
        shell.Servers.Selected = item;
        if (shell.Servers.Selected?.Entry.Id == row.Id) return true;
        Error("实例暂不可切换", "请等待当前操作完成后重试。");
        return false;
    }
    private void GoBack(object? sender, RoutedEventArgs e) => shell?.NavigateCommand.Execute("home");
    private async void CreateInstance(object? sender, RoutedEventArgs e) => await EditAsync(null);
    private async Task EditAsync(InstanceListRow? row)
    {
        if (operating || Model is not { CanCreate: true } model || Owner is not { } owner) return;
        var original = row is null ? null : Current(row.Id);
        if (row is not null && original is null) return;
        operating = true; Update();
        try
        {
            await owner.EditInstanceAsync(original, async draft =>
            {
                var success = original is null ? await model.CreateInstanceAsync(draft)
                    : await model.UpdateInstanceAsync(original.Id, original.Revision, draft);
                return success ? null : model.Error;
            }, nameOnly: original is not null);
        }
        finally { operating = false; Update(); }
    }
    private async void RefreshInstances(object? sender, RoutedEventArgs e)
    {
        if (operating || refreshing || Model is not { } model || model.Busy) return;
        refreshing = true; Update();
        try
        {
            if (!model.Server.Connected) await model.InitializeAsync(); else await model.RefreshInstancesAsync();
            if (model.Error.Length > 0) Error("读取实例失败", model.Error);
        }
        finally
        {
            // Mount/register all per-field MorphBlocks before starting Arc's ordered resolve sequence.
            Update();
            InstanceChoices.UpdateLayout();
            refreshing = false;
            Update();
        }
    }
    private async Task StartAsync(InstanceListRow row)
    {
        if (!row.CanStart || !Browse(row) || Owner is not { } owner) return;
        operating = true; Update();
        try { await owner.StartHomeServerAsync(); }
        finally { operating = false; Update(); }
    }
    private async Task MigrateAsync(InstanceListRow row)
    {
        if (!row.CanMigrate || !Browse(row) || Model is not { } model || Current(row.Id) is not { } original || Owner is not { } owner) return;
        operating = true; Update();
        try
        {
            if (await owner.ChooseAsync("迁移实例布局", $"迁移“{original.Name}”的旧 home/userdata 布局；旧目录归档，共用安装不变。", "取消", "迁移") != 1) return;
            if (model.Selected?.Entry.Id != original.Id || Current(original.Id)?.Revision != original.Revision) return;
            if (!await model.MigrateLayoutAsync()) Error("迁移失败", model.Error);
        }
        finally { operating = false; Update(); }
    }
    private async void ConfirmDelete(object? sender, RoutedEventArgs e)
    {
        if (operating || pendingDelete is not { } target || Model is not { } model) return;
        if (!model.CanCreate || Current(target.Id)?.Revision != target.Revision)
        {
            DeleteConfirm.IsConfirmed = false;
            Error("删除未提交", "实例或宿主状态已变化，请重新打开删除菜单。");
            return;
        }
        operating = true; DeleteConfirm.IsEnabled = false; Update();
        try
        {
            if (!await model.DeleteInstanceAsync(target.Id, target.Revision))
            {
                Error("删除失败", model.Error);
                DeleteConfirm.IsConfirmed = false;
                return;
            }
            DeletePopover.IsOpen = false;
            pendingDelete = null;
            Owner?.ShowInstanceNotification("实例已删除", target.Name, ToastKind.Success);
        }
        finally { operating = false; DeleteConfirm.IsEnabled = true; DeleteConfirm.IsConfirmed = false; Update(); }
    }
}
