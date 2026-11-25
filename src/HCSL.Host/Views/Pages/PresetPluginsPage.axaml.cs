using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
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

    private int generation, filterGeneration;
    private Row[] rows = [];
    private PluginResult? snapshot;
    private bool editing, projecting;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetPluginsPage()
    {
        InitializeComponent();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        ManageButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        var frameworkFilter = Filters.Fields.First(field => field.Id == "framework");
        frameworkFilter.Options.Add(new FilterOption { Value = "none", Label = "无框架" });
        foreach (var framework in FrameworkRules.Known)
            frameworkFilter.Options.Add(new FilterOption { Value = framework.Id, Label = framework.DisplayName });
        Plugins.RowKeySelector = item => ((Row)item).Id + "/" + ((Row)item).Version;
        Plugins.Columns.Add(new DataTableColumn { Header = "插件", Width = new GridLength(1, GridUnitType.Star), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => NameCell(row)) });
        Plugins.Columns.Add(new DataTableColumn { Header = "", Width = new GridLength(56), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => InfoCell(row)) });
        Plugins.AddHandler(InputElement.PointerPressedEvent, BlockUnavailableToggle, RoutingStrategies.Tunnel);
        Plugins.AddHandler(InputElement.KeyDownEvent, BlockUnavailableKey, RoutingStrategies.Tunnel);
        Plugins.SelectionChanged += SelectionChanged;
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) _ = LoadAsync(); else { generation++; snapshot = null; PluginProperties.IsOpen = false; }
        };
        DataContextChanged += (_, _) => { if (IsVisible) _ = LoadAsync(); };
    }

    private static Control? RowControl(object? source) => (source as Avalonia.Visual)?.GetSelfAndVisualAncestors()
        .OfType<Control>().FirstOrDefault(control => control.GetType().Name == "TableRow");

    // Arc 1.1 has no per-row checkbox enable predicate. Gate input before its row handler.
    private static readonly System.Reflection.PropertyInfo? TableRowItem = typeof(Table).Assembly
        .GetType("TKCode.Arc.TableRow")?.GetProperty("Item",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly,
            binder: null, returnType: typeof(object), types: Type.EmptyTypes, modifiers: null);

    private static Row? RowItem(Control? control) =>
        control is not null && TableRowItem?.DeclaringType?.IsInstanceOfType(control) == true
            ? TableRowItem.GetValue(control) as Row : null;
    private static bool CannotEnable(Row? row) => row is { Enabled: false } && (!row.Available || !row.Compatible);

    private void BlockUnavailableToggle(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual visual && visual.GetSelfAndVisualAncestors().OfType<Control>()
            .Any(control => control.Classes.Contains("plugin-info"))) return;
        if (CannotEnable(RowItem(RowControl(e.Source)))) e.Handled = true;
    }

    private void BlockUnavailableKey(object? sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Space or Key.Enter) && e.Source is Control control &&
            !control.Classes.Contains("plugin-info") && CannotEnable(RowItem(RowControl(control)))) e.Handled = true;
    }

    private Control? InfoCell(Row? row)
    {
        if (row is null) return null;
        var button = new TKCode.Arc.Button { Variant = ButtonVariant.Ghost, Size = ControlSize.Small,
            Content = new LucideIcon { Kind = LucideIconKind.Info } };
        button.Classes.Add("plugin-info");
        Avalonia.Automation.AutomationProperties.SetName(button, "插件属性：" + row.Name);
        button.Click += (_, _) =>
        {
            PropertyName.Text = row.Name;
            PropertyVersion.Text = row.Version;
            PropertyFramework.Text = row.Framework;
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
        if (!row.Compatible && row.Available)
            details.Children.Add(new Badge { Content = row.Framework, Size = BadgeSize.Small, Tone = BadgeTone.Danger });
        details.Children.Add(new Badge { Content = row.Version, Size = BadgeSize.Small, Tone = BadgeTone.Info });
        if (row.Compatible || !row.Available) return details;
        Control? band = null;
        AdornerLayer? layer = null;
        var tint = new Border { IsHitTestVisible = false, Opacity = 0.16 };
        ArcTokens.Bind(tint, Border.BackgroundProperty, ArcDesignTokenKind.Danger);
        void SyncTint(object? sender, EventArgs e) => tint.IsVisible = IsEffectivelyVisible && ReferenceEquals(RowItem(band), row)
            && (Plugins.ItemsSource?.OfType<Row>().Contains(row) ?? false);
        details.AttachedToVisualTree += (_, _) =>
        {
            band = details.GetVisualAncestors().OfType<Control>().FirstOrDefault(p => p.GetType().Name == "TableRow");
            if (band is null) return;
            Tooltip.SetTip(band, "与当前预设框架不兼容：需要 " + row.Framework);
            if (AdornerLayer.GetAdornerLayer(band) is not { } adorners) return;
            layer = adorners;
            AdornerLayer.SetAdornedElement(tint, band);
            layer.Children.Add(tint);
            Plugins.LayoutUpdated += SyncTint;
            SyncTint(null, EventArgs.Empty);
        };
        details.DetachedFromVisualTree += (_, _) =>
        {
            Plugins.LayoutUpdated -= SyncTint;
            layer?.Children.Remove(tint);
            AdornerLayer.SetAdornedElement(tint, null);
            layer = null;
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

    private void FilterPlugins(object? sender, FilterToolbarFiltersChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Plugins is null || Filters is null) return;
        var query = (Filters.SearchText ?? "").Trim();
        var frameworks = (Filters.ActiveFilters ?? []).Where(chip => chip.Id == "framework")
            .SelectMany(chip => new[] { chip.Value, chip.Label }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = rows.Where(row => (frameworks.Count == 0 || frameworks.Contains(row.FrameworkId) ||
                frameworks.Contains(row.Framework) || row.FrameworkId == "none" && frameworks.Contains("无框架")) &&
            (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Version.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        PluginProperties.IsOpen = false;
        var ticket = ++filterGeneration;
        projecting = true;
        Plugins.ItemsSource = visible;
        // Arc 1.1 retains the old realized row indices until its next layout pass.
        Dispatcher.UIThread.Post(() =>
        {
            if (ticket != filterGeneration) return;
            try
            {
                if (!IsVisible || !Plugins.IsAttachedToVisualTree()) return;
                Plugins.UpdateLayout();
                Plugins.SelectedItems = visible.Where(r => r.Enabled).ToList();
            }
            finally { projecting = false; }
        }, DispatcherPriority.Loaded);
    }

    private async void SelectionChanged(object? sender, DataTableSelectionChangedEventArgs e)
    {
        if (projecting || editing || snapshot is not { } current || Scope() is not { } scope) return;
        var checkedRows = e.SelectedItems.OfType<Row>().ToHashSet();
        var visible = (Plugins.ItemsSource?.OfType<Row>() ?? []).ToArray();
        var added = visible.Where(r => !r.Enabled && checkedRows.Contains(r)).ToArray();
        var removed = visible.Where(r => r.Enabled && !checkedRows.Contains(r)).ToArray();
        if (added.Length == 0 && removed.Length == 0) return;
        if (!scope.Model.CanEditPlugins || added.Any(r => !r.Available || !r.Compatible) ||
            added.GroupBy(r => r.Id).Any(group => group.Count() > 1))
        {
            ApplyFilter();
            Owner?.ShowInstanceNotification("无法启用所选插件", "请确认预设可修改、框架兼容，且每个插件只选择一个版本。", ToastKind.Warning);
            return;
        }
        var next = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToList();
        foreach (var row in removed)
        {
            var index = next.FindIndex(p => p.Id == row.Id);
            if (index >= 0) next[index] = next[index] with { Enabled = false };
        }
        foreach (var row in added)
        {
            var index = next.FindIndex(p => p.Id == row.Id);
            var choice = new PluginSelection(row.Id, row.Version, true);
            if (index < 0) next.Add(choice); else next[index] = choice;
        }
        var ticket = generation;
        editing = true;
        Plugins.IsEnabled = false;
        try
        {
            var saved = await scope.Model.PluginsAsync(new(PluginAction.Save, scope.Instance.Id, scope.Preset.Id,
                scope.Instance.Revision, current.Hash, [.. next]));
            if (ticket == generation) Render(saved);
            Owner?.ShowInstanceNotification("预设插件已保存", "下次部署生效。", ToastKind.Success);
        }
        catch (IOException ex)
        {
            if (ticket == generation) ApplyFilter();
            Owner?.ShowInstanceNotification("无法保存插件", ex.Message);
        }
        finally { editing = false; Plugins.IsEnabled = true; }
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
