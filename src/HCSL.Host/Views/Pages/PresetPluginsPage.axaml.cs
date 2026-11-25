using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
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
    private bool editing, descending;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetPluginsPage()
    {
        InitializeComponent();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        ManageButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        SortButton.Icon = Lucide.Geometry(LucideIconKind.ArrowUp);
        // Arc 1.1 does not expose a header visibility switch. Zero both layout modes for this list only.
        foreach (var name in new[] { "HeaderHeightProperty", "CompactHeaderHeightProperty" })
            if (typeof(Table).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(null) is StyledProperty<double> height)
                Plugins.SetValue(height, 0);
        var frameworkFilter = Filters.Fields.First(field => field.Id == "framework");
        frameworkFilter.Options.Add(new FilterOption { Value = "none", Label = "无框架" });
        foreach (var framework in FrameworkRules.Known)
            frameworkFilter.Options.Add(new FilterOption { Value = framework.Id, Label = framework.DisplayName });
        Plugins.RowKeySelector = item => ((Row)item).Id + "/" + ((Row)item).Version;
        Plugins.Columns.Add(new DataTableColumn { Header = "", Width = new GridLength(64), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => EnableCell(row)) });
        Plugins.Columns.Add(new DataTableColumn { Header = "插件", Width = new GridLength(1, GridUnitType.Star), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => NameCell(row)) });
        Plugins.Columns.Add(new DataTableColumn { Header = "", Width = new GridLength(56), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => InfoCell(row)) });
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) _ = LoadAsync(); else { generation++; snapshot = null; PluginProperties.IsOpen = false; }
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
    private static bool CannotEnable(Row? row) => row is { Enabled: false } && (!row.Available || !row.Compatible);

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
                checkbox.IsEnabled = item is not null && !CannotEnable(item) && Scope()?.Model.CanEditPlugins == true;
                checkbox.IsChecked = item?.Enabled == true;
                Avalonia.Automation.AutomationProperties.SetName(checkbox, "启用 " + item?.Name);
            }
            finally { projecting = false; }
        }
        checkbox.DataContextChanged += (_, _) => Project();
        checkbox.CheckedStateChanged += async (_, _) =>
        {
            if (projecting || checkbox.DataContext is not Row item || checkbox.IsChecked == item.Enabled) return;
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
        return button;
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
            if (band is not null) Tooltip.SetTip(band, Incompatible(item) ? "不兼容" : null);
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
            if (scroll is not null) scroll.ScrollChanged += SyncTint;
            Plugins.LayoutUpdated += SyncTint;
            SyncTint(null, EventArgs.Empty);
        };
        details.DetachedFromVisualTree += (_, _) =>
        {
            Plugins.LayoutUpdated -= SyncTint;
            if (scroll is not null) scroll.ScrollChanged -= SyncTint;
            TintLayer.Children.Remove(tint);
            scroll = null;
            viewport = null;
            if (band is not null) Tooltip.SetTip(band, null);
            band = null;
        };
        return details;
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

    private async Task LoadAsync()
    {
        var ticket = ++generation;
        ManageButton.IsEnabled = false;
        snapshot = null;
        rows = [];
        ApplyFilter();
        if (Scope() is not { } scope) return;
        bool Current() => ticket == generation && Scope()?.Preset.Id == scope.Preset.Id;
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

    private void ToggleNameSort(object? sender, RoutedEventArgs e)
    {
        descending = !descending;
        SortButton.Label = descending ? "名称降序" : "名称升序";
        SortButton.Icon = Lucide.Geometry(descending ? LucideIconKind.ArrowDown : LucideIconKind.ArrowUp);
        ApplyFilter();
    }

    private void FilterPlugins(object? sender, FilterToolbarFiltersChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Plugins is null || Filters is null) return;
        var query = (Filters.SearchText ?? "").Trim();
        var chips = Filters.ActiveFilters ?? [];
        var states = chips.Where(chip => chip.Id == "state").SelectMany(chip => new[] { chip.Value, chip.Label }).ToHashSet();
        bool MatchState(Row row) => states.Count == 0 ||
            (!row.Compatible && row.Available && (states.Contains("incompatible") || states.Contains("不兼容"))) ||
            (row.Enabled ? states.Contains("enabled") || states.Contains("启用") : states.Contains("disabled") || states.Contains("禁用"));
        var frameworks = chips.Where(chip => chip.Id == "framework")
            .SelectMany(chip => new[] { chip.Value, chip.Label }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = rows.Where(row => MatchState(row) && (frameworks.Count == 0 || frameworks.Contains(row.FrameworkId) ||
                frameworks.Contains(row.Framework) || row.FrameworkId == "none" && frameworks.Contains("无框架")) &&
            (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Version.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        PluginProperties.IsOpen = false;
        Plugins.ItemsSource = descending
            ? visible.OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
            : visible;
    }

    private async Task SaveEnabledAsync(Row row, bool enabled)
    {
        if (editing || snapshot is not { } current || Scope() is not { } scope) return;
        if (!scope.Model.CanEditPlugins || enabled && (!row.Available || !row.Compatible))
        {
            Owner?.ShowInstanceNotification("无法保存插件", "请确认预设可修改且插件与框架兼容。");
            return;
        }
        var next = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToList();
        var index = next.FindIndex(p => p.Id == row.Id);
        var choice = new PluginSelection(row.Id, row.Version, enabled);
        if (index < 0) next.Add(choice); else next[index] = choice;
        var ticket = generation;
        editing = true;
        try
        {
            var saved = await scope.Model.PluginsAsync(new(PluginAction.Save, scope.Instance.Id, scope.Preset.Id,
                scope.Instance.Revision, current.Hash, [.. next], PackageMetadataOnly: true));
            if (ticket == generation) Render(saved);
        }
        catch (IOException ex)
        {
            Owner?.ShowInstanceNotification("无法保存插件", ex.Message);
        }
        finally { editing = false; }
    }

    private async void Manage(object? sender, RoutedEventArgs e)
    {
        if (editing || Scope() is not { } scope || Owner is not { } owner) return;
        editing = true;
        ManageButton.IsEnabled = false;
        try { await owner.EditPluginsAsync(scope.Model, scope.Instance, scope.Preset); }
        finally { editing = false; if (IsVisible) await LoadAsync(); }
    }
}
