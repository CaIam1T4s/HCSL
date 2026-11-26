using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.DesignTokens;

namespace HCSL.Host.Views.Pages;

public partial class PresetPluginsPage : UserControl
{
    private sealed record Row(string Id, string Name, string Version, string FrameworkId, string Framework,
        bool Enabled, bool Available, bool Compatible)
    {
        public string State => !Available ? "包不可用" : !Compatible ? "" : Enabled ? "已启用" : "未启用";
    }

    private int generation;
    private Row[] rows = [];
    private PluginResult? snapshot;
    private bool editing;
    private List<PluginSelection>? draft;
    private (Guid Instance, Guid Preset)? draftScope;
    private event Action? SelectionChanged;

    private bool Enabled(Row row) => draft is null ? row.Enabled :
        draft.Any(p => p.Id == row.Id && p.Version == row.Version && p.Enabled);
    private string? frameworkFilter, stateFilter;
    private GlassMenu? viewMenu;
    private int tipGeneration;
    private Avalonia.Threading.DispatcherTimer? tipTimer;
    private Control? tipRow;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetPluginsPage()
    {
        InitializeComponent();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        ManageButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        ViewButton.Icon = Lucide.Geometry(LucideIconKind.Eye);
        Plugins.RowKeySelector = item => ((Row)item).Id + "/" + ((Row)item).Version;
        Plugins.Columns.Add(new DataTableColumn { Header = "", Width = new GridLength(64), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => EnableCell(row)) });
        Plugins.Columns.Add(new DataTableColumn { Header = "插件", Width = new GridLength(1, GridUnitType.Star), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => NameCell(row)) });
        Plugins.Columns.Add(new DataTableColumn { Header = "", Width = new GridLength(88), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => InfoCell(row)) });
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) _ = LoadAsync(); else { generation++; snapshot = null; PluginProperties.IsOpen = false; CloseTip(); viewMenu?.Close(); }
        };
        DataContextChanged += (_, _) => { if (IsVisible) _ = LoadAsync(); };
    }

    private static Control? RowControl(object? source) => (source as Avalonia.Visual)?.GetSelfAndVisualAncestors()
        .OfType<Control>().FirstOrDefault(control => control.GetType().Name == "TableRow");

    // Arc reuses row containers; read the current item when handling a cell action.
    private static readonly System.Reflection.PropertyInfo? TableRowItem = typeof(Table).Assembly
        .GetType("TKCode.Arc.TableRow")?.GetProperty("Item",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
            binder: null, returnType: typeof(object), types: Type.EmptyTypes, modifiers: null);

    private static Row? RowItem(Control? control) =>
        control is not null && TableRowItem?.DeclaringType?.IsInstanceOfType(control) == true
            ? TableRowItem.GetValue(control) as Row : null;

    private Control? EnableCell(Row? row)
    {
        if (row is null) return null;
        var checkbox = new TKCode.Arc.Checkbox { Padding = new Thickness(0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var projecting = false;
        void Project()
        {
            projecting = true;
            try
            {
                var item = checkbox.DataContext as Row;
                checkbox.IsEnabled = item is not null && (Enabled(item) || item.Available && item.Compatible) &&
                    Scope() is { } scope && (scope.Model.CanEditPlugins || scope.Model.CanWritePluginSelections && draftScope == (scope.Instance.Id, scope.Preset.Id));
                checkbox.IsChecked = item is not null && Enabled(item);
                Avalonia.Automation.AutomationProperties.SetName(checkbox, "启用 " + item?.Name);
            }
            finally { projecting = false; }
        }
        checkbox.DataContextChanged += (_, _) => Project();
        checkbox.AttachedToVisualTree += (_, _) => { SelectionChanged += Project; Project(); };
        checkbox.DetachedFromVisualTree += (_, _) => SelectionChanged -= Project;
        checkbox.CheckedStateChanged += async (_, _) =>
        {
            if (projecting || checkbox.DataContext is not Row item || checkbox.IsChecked == Enabled(item)) return;
            if (ReferenceEquals(RowItem(RowControl(checkbox)), item)) await SaveEnabledAsync(item, checkbox.IsChecked == true);
            if (ReferenceEquals(checkbox.DataContext, item) && rows.Contains(item)) Project();
        };
        Project();
        return checkbox;
    }

    private Control? InfoCell(Row? row)
    {
        if (row is null) return null;
        var button = new TKCode.Arc.Button { Variant = ButtonVariant.Ghost, Size = ControlSize.Small,
            Content = new LucideIcon { Kind = LucideIconKind.Info } };
        button.Classes.Add("plugin-info");
        button.PointerEntered += (_, _) => CloseTip();
        void UpdateName() => Avalonia.Automation.AutomationProperties.SetName(button,
            button.DataContext is Row item ? "插件属性：" + item.Name : "插件属性");
        button.DataContextChanged += (_, _) => UpdateName();
        UpdateName();
        button.Click += (_, _) =>
        {
            if (button.DataContext is not Row item || !ReferenceEquals(RowItem(RowControl(button)), item)) return;
            PropertyName.Text = item.Name;
            PropertyVersion.Text = item.Version;
            PropertyFramework.Text = item.Framework;
            PluginProperties.ShowAt(button);
        };
        var location = new TKCode.Arc.Button { Variant = ButtonVariant.Ghost, Size = ControlSize.Small,
            Content = new LucideIcon { Kind = LucideIconKind.FolderOpen } };
        location.Classes.Add("plugin-info");
        location.PointerEntered += (_, _) => CloseTip();
        Avalonia.Automation.AutomationProperties.SetName(location, "打开插件文件位置");
        location.Click += async (_, _) =>
        {
            if (location.DataContext is Row { Available: true } item && ReferenceEquals(RowItem(RowControl(location)), item))
                await OpenLocationAsync(item);
        };
        location.DataContextChanged += (_, _) => location.IsEnabled = location.DataContext is Row { Available: true };
        return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { location, button } };
    }

    private async Task OpenLocationAsync(Row? row = null)
    {
        if (Scope() is not { } scope) return;
        try { await scope.Model.OpenPluginLocationAsync(row?.Id, row?.Version); }
        catch (Exception ex) when (ex is IOException or InstanceException or UnauthorizedAccessException)
        { Owner?.ShowInstanceNotification("无法打开文件位置", ex.Message); }
    }

    private Control? NameCell(Row? row)
    {
        if (row is null) return null;
        var label = new TextBlock { Text = row.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var details = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        details.Children.Add(label);
        var frameworkBadge = new Badge { Size = BadgeSize.Small, Tone = BadgeTone.Danger };
        var versionBadge = new Badge { Size = BadgeSize.Small, Tone = BadgeTone.Info };
        details.Children.Add(frameworkBadge);
        details.Children.Add(versionBadge);
        Control? band = null;
        ScrollViewer? scroll = null;
        ScrollContentPresenter? viewport = null;
        Avalonia.Media.TranslateTransform? rowShift = null;
        void ShiftChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => SyncTint(sender, EventArgs.Empty);
        var tint = new Border { IsHitTestVisible = false, IsVisible = false, Opacity = 0.16 };
        ArcTokens.Bind(tint, Border.BackgroundProperty, ArcDesignTokenKind.Danger);
        bool Incompatible(Row? item) => item is { Compatible: false, Available: true };
        void SyncTint(object? sender, EventArgs e)
        {
            var item = details.DataContext as Row;
            if (!IsEffectivelyVisible || !Incompatible(item) || band is null || !ReferenceEquals(RowItem(band), item) ||
                !(Plugins.ItemsSource?.OfType<Row>().Any(visible => ReferenceEquals(visible, item)) ?? false) ||
                band.TranslatePoint(default, TintLayer) is not { } origin ||
                viewport?.TranslatePoint(default, TintLayer) is not { } viewportOrigin)
            {
                tint.IsVisible = false;
                return;
            }
            var bounds = new Avalonia.Rect(origin, band.Bounds.Size).Intersect(new Avalonia.Rect(viewportOrigin, viewport.Bounds.Size));
            tint.IsVisible = bounds.Width > 0 && bounds.Height > 0;
            if (!tint.IsVisible) return;
            Canvas.SetLeft(tint, bounds.X);
            Canvas.SetTop(tint, bounds.Y);
            tint.Width = bounds.Width;
            tint.Height = bounds.Height;
        }
        void UpdateCell()
        {
            var item = details.DataContext as Row;
            label.Text = item?.Name;
            frameworkBadge.Content = item?.Framework;
            frameworkBadge.IsVisible = Incompatible(item);
            versionBadge.Content = item?.Version;
            if (band is not null && ReferenceEquals(tipRow, band)) CloseTip();
            SyncTint(null, EventArgs.Empty);
        }
        details.DataContextChanged += (_, _) => UpdateCell();
        details.AttachedToVisualTree += (_, _) =>
        {
            band = details.GetVisualAncestors().OfType<Control>().FirstOrDefault(p => p.GetType().Name == "TableRow");
            if (band is null) return;
            UpdateCell();
            scroll = band.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            viewport = band.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
            TintLayer.Children.Add(tint);
            rowShift = band.RenderTransform as Avalonia.Media.TranslateTransform;
            if (rowShift is not null) rowShift.PropertyChanged += ShiftChanged;
            band.PointerEntered += HoverRow;
            band.PointerMoved += HoverRow;
            band.PointerExited += LeaveRow;
            if (scroll is not null) scroll.ScrollChanged += ScrollRow;
            Plugins.LayoutUpdated += SyncTint;
            SyncTint(null, EventArgs.Empty);
        };
        details.DetachedFromVisualTree += (_, _) =>
        {
            Plugins.LayoutUpdated -= SyncTint;
            if (scroll is not null) scroll.ScrollChanged -= ScrollRow;
            if (band is not null)
            {
                band.PointerEntered -= HoverRow;
                band.PointerMoved -= HoverRow;
                band.PointerExited -= LeaveRow;
                if (ReferenceEquals(tipRow, band)) CloseTip();
            }
            if (rowShift is not null) rowShift.PropertyChanged -= ShiftChanged;
            rowShift = null;
            TintLayer.Children.Remove(tint);
            scroll = null;
            viewport = null;
            band = null;
        };
        return details;

        void HoverRow(object? sender, PointerEventArgs e)
        {
            if (band is null || !Incompatible(details.DataContext as Row) ||
                band.GetVisualDescendants().OfType<Control>().Any(control => control.IsPointerOver && control.Classes.Contains("plugin-info"))) return;
            var position = e.GetPosition(TipLayer);
            var anchorRow = band;
            var item = RowItem(anchorRow);
            CloseTip();
            tipRow = anchorRow;
            var ticket = tipGeneration;
            tipTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            var timer = tipTimer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ticket != tipGeneration || !IsEffectivelyVisible || !anchorRow.IsPointerOver ||
                    !ReferenceEquals(RowItem(anchorRow), item)) return;
                Canvas.SetLeft(TipAnchor, position.X);
                Canvas.SetTop(TipAnchor, position.Y);
                TipLayer.UpdateLayout();
                Tooltip.SetIsOpen(TipAnchor, true);
            };
            timer.Start();
        }

        void LeaveRow(object? sender, PointerEventArgs e) { if (ReferenceEquals(tipRow, band)) CloseTip(); }
        void ScrollRow(object? sender, ScrollChangedEventArgs e) { SyncTint(sender, e); if (ReferenceEquals(tipRow, band)) CloseTip(); }
    }

    private (HostServerController Model, InstanceDefinition Instance, PresetDefinition Preset)? Scope()
    {
        if (DataContext is ShellViewModel { ManagedPresetId: { } id } shell &&
            shell.Servers.Selected?.Entry is { Definition: { } instance } entry &&
            entry.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            return (shell.Servers, instance, preset);
        return null;
    }

    private async void Refresh(object? sender, RoutedEventArgs e) { if (!editing) await LoadAsync(); }

    private async Task LoadAsync(bool keepRows = false)
    {
        var ticket = ++generation;
        ManageButton.IsEnabled = false;
        if (!keepRows)
        {
            snapshot = null;
            rows = [];
            ApplyFilter();
        }
        if (Scope() is not { } scope) return;
        bool Current() => ticket == generation && Scope()?.Preset.Id == scope.Preset.Id;
        if (scope.Model.PendingPluginSelectionSave is { } pending) await pending;
        if (!Current()) return;
        try
        {
            var result = await scope.Model.PluginsAsync(new(PluginAction.List, scope.Instance.Id, scope.Preset.Id, PackageMetadataOnly: true));
            if (!Current()) return;
            Render(result);
            ManageButton.IsEnabled = scope.Model.Server.Connected && !editing;
        }
        catch (IOException ex) { if (Current()) Owner?.ShowInstanceNotification("无法读取插件", ex.Message); }
    }

    private static bool IsPlugin(PluginPackageInfo package) => FrameworkRules.Find(package.Id) is null &&
        !package.FrameworkPayload && package.Category != PluginCategory.Framework;

    private void Render(PluginResult result)
    {
        draft = null;
        snapshot = result;
        var selected = result.Selected.ToDictionary(s => (s.Id, s.Version));
        var available = result.Available.Select(p => (p.Id, p.Version)).ToHashSet();
        rows = result.Available.Where(IsPlugin).Select(p =>
        {
            var compatible = p.Framework is null || result.Selected.Any(s => s.Id == p.Framework && s.Enabled && s.Available);
            return new Row(p.Id, p.Name, p.Version, p.Framework ?? "none", FrameworkRules.Find(p.Framework ?? "")?.DisplayName ?? p.Framework ?? "-",
                selected.TryGetValue((p.Id, p.Version), out var s) && s.Enabled, true, compatible);
        }).Concat(result.Selected.Where(s => FrameworkRules.Find(s.Id) is null && !available.Contains((s.Id, s.Version)))
            .Select(s => new Row(s.Id, s.Name ?? s.Id, s.Version, "none", "-", s.Enabled, false, false)))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ThenByDescending(r => r.Version, StringComparer.OrdinalIgnoreCase).ToArray();
        ApplyFilter();
    }

    private void CloseTip()
    {
        tipGeneration++;
        tipTimer?.Stop();
        tipTimer = null;
        tipRow = null;
        Tooltip.SetIsOpen(TipAnchor, false);
    }

    private void SearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OpenViewMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        CloseTip();
        viewMenu?.Close();
        var menu = viewMenu = new GlassMenu();
        Avalonia.Automation.AutomationProperties.SetName(menu, "插件查看选项");
        Add("全部状态", LucideIconKind.List, stateFilter is null, () => stateFilter = null);
        Add("启用", LucideIconKind.CircleCheckBig, stateFilter == "enabled", () => stateFilter = "enabled");
        Add("禁用", LucideIconKind.CircleMinus, stateFilter == "disabled", () => stateFilter = "disabled");
        Add("不兼容", LucideIconKind.TriangleAlert, stateFilter == "incompatible", () => stateFilter = "incompatible");
        menu.Items.Add(new GlassMenuSeparator());
        Add("全部框架", LucideIconKind.Layers, frameworkFilter is null, () => frameworkFilter = null);
        Add("无框架", LucideIconKind.Layers, frameworkFilter == "none", () => frameworkFilter = "none");
        foreach (var framework in FrameworkRules.Known)
            Add(framework.DisplayName, LucideIconKind.Layers, frameworkFilter == framework.Id, () => frameworkFilter = framework.Id);
        menu.Items.Add(new GlassMenuSeparator());
        var folder = new GlassMenuItem { Content = "打开插件文件夹", Icon = Lucide.Geometry(LucideIconKind.FolderOpen) };
        folder.Click += async (_, _) => { menu.Close(); await OpenLocationAsync(); };
        menu.Items.Add(folder);
        menu.Open(anchor);

        void Add(string title, LucideIconKind icon, bool selected, Action select)
        {
            var item = new GlassMenuItem { Content = title, Icon = Lucide.Geometry(selected ? LucideIconKind.Check : icon) };
            item.Click += (_, _) => { menu.Close(); select(); ApplyFilter(); };
            menu.Items.Add(item);
        }
    }

    private void ApplyFilter()
    {
        if (Plugins is null || Search is null) return;
        var query = (Search.Text ?? "").Trim();
        bool MatchState(Row row) => stateFilter switch
        {
            "enabled" => Enabled(row),
            "disabled" => !Enabled(row),
            "incompatible" => !row.Compatible && row.Available,
            _ => true
        };
        var visible = rows.Where(row => MatchState(row) && (frameworkFilter is null || row.FrameworkId == frameworkFilter) &&
            (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Version.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        CloseTip();
        PluginProperties.IsOpen = false;
        // Clear old geometry before Arc starts animating outgoing virtual rows.
        foreach (var tint in TintLayer.Children) tint.IsVisible = false;
        Plugins.ItemsSource = visible;
        Plugins.InvalidateMeasure();
    }

    private async Task SaveEnabledAsync(Row row, bool enabled)
    {
        if (snapshot is not { } current || Scope() is not { } scope) return;
        var key = (scope.Instance.Id, scope.Preset.Id);
        if ((editing || scope.Model.PendingPluginSelectionSave is not null) && draftScope != key) return;
        if (!scope.Model.CanWritePluginSelections || (!scope.Model.CanEditPlugins && draftScope != key) || enabled && (!row.Available || !row.Compatible))
        {
            Owner?.ShowInstanceNotification("无法保存插件", "请确认预设可修改且插件与框架兼容。");
            return;
        }
        var next = draft ??= current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToList();
        var index = next.FindIndex(p => p.Id == row.Id);
        var choice = new PluginSelection(row.Id, row.Version, enabled);
        if (index < 0) next.Add(choice); else next[index] = choice;
        SelectionChanged?.Invoke();
        if (stateFilter is "enabled" or "disabled") ApplyFilter();
        if (editing) return;

        var ticket = generation;
        editing = true;
        draftScope = key;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Model.PendingPluginSelectionSave = completion.Task;
        var success = false;
        try
        {
            PluginSelection[] written;
            do
            {
                written = [.. next];
                current = await scope.Model.PluginsAsync(new(PluginAction.Save, key.Item1, key.Item2,
                    scope.Instance.Revision, current.Hash, written, PackageMetadataOnly: true));
            } while (!next.SequenceEqual(written));
            success = true;
        }
        catch (IOException ex)
        {
            Owner?.ShowInstanceNotification("无法保存插件", ex.Message);
        }
        finally
        {
            draftScope = null;
            editing = false;
            if (ticket == generation && success) snapshot = current;
            // Keep the confirmed projection until the next explicit reload.
            draft = ticket == generation && success ? next : null;
            scope.Model.PendingPluginSelectionSave = null;
            completion.TrySetResult(success);
            SelectionChanged?.Invoke();
            if (!success && ticket == generation && IsVisible) await LoadAsync();
        }
    }

    private async void Manage(object? sender, RoutedEventArgs e)
    {
        if (editing || Scope() is not { } scope || Owner is not { } owner) return;
        editing = true;
        ManageButton.IsEnabled = false;
        var refresh = false;
        try
        {
            var imported = await owner.ImportPluginToLibraryAsync(scope.Model, scope.Instance, scope.Preset);
            if (imported is not null && Scope()?.Preset.Id == scope.Preset.Id)
            {
                refresh = true;
            }
        }
        finally
        {
            editing = false;
            ManageButton.IsEnabled = Scope()?.Model.Server.Connected == true;
            if (refresh && IsVisible) await LoadAsync(keepRows: true);
        }
    }
}
