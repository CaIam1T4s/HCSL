using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

public partial class PresetPluginsPage : UserControl
{
    private sealed record Row(string Key, string Name, string Version = "", string State = "", Row[]? Children = null);
    private int generation;
    private Row[] rows = [];
    private string[] selectedPlugins = [];
    private bool editing;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetPluginsPage()
    {
        InitializeComponent();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        ManageButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        TreeTableHeaderAlignment.Attach(Plugins);
        Selection.Labels = new SelectionToolbarLabels { Count = count => $"已选 {count}", Clear = "清除选择", Toolbar = "插件操作" };
        Selection.Actions.Add(new SelectionToolbarAction { Key = "manage", Label = "编辑所选插件", IsEnabled = false });
        Selection.Actions.Add(new SelectionToolbarAction { Key = "enable", Label = "启用所选", IsEnabled = false });
        Selection.Actions.Add(new SelectionToolbarAction { Key = "disable", Label = "停用所选", IsEnabled = false });
        Plugins.SelectionChanged += (_, e) =>
        {
            selectedPlugins = e.SelectedItems.OfType<Row>().Where(row => row.Children is null).Select(row => row.Key).ToArray();
            UpdateSelection();
        };
        Plugins.KeySelector = row => ((Row)row).Key;
        Plugins.ChildrenSelector = row => ((Row)row).Children;
        Plugins.RowLabelSelector = row => ((Row)row).Name;
        Plugins.Columns.Add(TreeTableColumn.Create<Row>("插件", row => row.Name));
        Plugins.Columns.Add(new TreeTableColumn { Header = "版本", Width = new GridLength(120), ValueSelector = row => ((Row)row).Version });
        Plugins.Columns.Add(new TreeTableColumn { Header = "状态", Width = new GridLength(112), ValueSelector = row => ((Row)row).State });
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) _ = LoadAsync(); else generation++;
        };
        DataContextChanged += (_, _) => { if (IsVisible) _ = LoadAsync(); };
    }

    private (HostServerController Model, InstanceDefinition Instance, PresetDefinition Preset)? Scope()
    {
        if (DataContext is ShellViewModel { ManagedPresetId: { } id } shell &&
            shell.Servers.Selected?.Entry is { Definition: { } instance } entry &&
            entry.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            return (shell.Servers, instance, preset);
        return null;
    }

    private async void Refresh(object? sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var ticket = ++generation;
        ManageButton.IsEnabled = false;
        rows = [];
        Plugins.SelectedKeys = [];
        selectedPlugins = [];
        ApplyFilter();
        if (Scope() is not { } scope) return;
        bool Current() => ticket == generation && Scope()?.Preset.Id == scope.Preset.Id;
        try
        {
            var result = await scope.Model.PluginsAsync(new(PluginAction.List, scope.Instance.Id, scope.Preset.Id));
            if (!Current()) return;
            var packages = result.Available.ToLookup(p => (p.Id, p.Version));
            rows = result.Selected.Select(s => (Selection: s, Package: packages[(s.Id, s.Version)].FirstOrDefault()))
                .Where(x => FrameworkRules.Find(x.Selection.Id) is null && x.Package is not { FrameworkPayload: true } and not { Category: PluginCategory.Framework })
                .GroupBy(x => x.Package?.Category ?? PluginCategory.Other)
                .Select(group => new Row("category:" + group.Key, PluginPresentation.CategoryLabel(group.Key), Children:
                    group.Select(x => new Row(x.Selection.Id, x.Selection.Name ?? x.Selection.Id, x.Selection.Version,
                        !x.Selection.Available ? "包不可用" : x.Selection.Enabled ? "已启用" : "已禁用")).ToArray())).ToArray();
            ApplyFilter();
            ManageButton.IsEnabled = scope.Model.Server.Connected && !editing;
        }
        catch (IOException ex) { if (Current()) Owner?.ShowInstanceNotification("无法读取插件", ex.Message); }
    }

    private void FilterPlugins(object? sender, FilterToolbarFiltersChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Plugins is null || Filters is null) return;
        var query = (Filters.SearchText ?? "").Trim();
        bool Match(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
        var visible = rows.Select(group => Match(group.Name) ? group : group with
            { Children = group.Children!.Where(row => Match(row.Name) || Match(row.Key)).ToArray() })
            .Where(group => group.Children!.Length > 0).ToArray();
        var visibleIds = visible.SelectMany(group => group.Children!).Select(row => row.Key).ToHashSet();
        var selected = selectedPlugins.Where(visibleIds.Contains).Cast<object>().ToArray();
        Plugins.ItemsSource = visible;
        Plugins.SelectedKeys = selected;
        Plugins.ExpandedKeys = visible.Select(row => (object)row.Key).ToArray();
        UpdateSelection();
    }

    private void SelectAll(object? sender, RoutedEventArgs e) => Plugins.SelectedKeys =
        (Plugins.ItemsSource?.Cast<Row>() ?? []).SelectMany(group => group.Children!).Select(row => (object)row.Key).ToArray();

    private void ClearSelection(object? sender, RoutedEventArgs e) => Plugins.SelectedKeys = [];

    private void UpdateSelection()
    {
        Selection.SelectedCount = selectedPlugins.Length;
        foreach (var action in Selection.Actions)
            action.IsEnabled = !editing && (action.Key == "manage" ? selectedPlugins.Length == 1 :
                selectedPlugins.Length > 0 && Scope()?.Model.CanEditPlugins == true);
    }

    private async void Manage(object? sender, RoutedEventArgs e) => await OpenEditorAsync();

    private async void EditSelection(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (selectedPlugins.Length == 0) return;
        await OpenEditorAsync(selectedPlugins, e.Action.Key switch { "enable" => true, "disable" => false, _ => null });
    }

    private async Task OpenEditorAsync(string[]? targets = null, bool? enabled = null)
    {
        if (editing || Scope() is not { } scope || Owner is not { } owner) return;
        editing = true;
        ManageButton.IsEnabled = false;
        UpdateSelection();
        try { await owner.EditPluginsAsync(scope.Model, scope.Instance, scope.Preset, targets, enabled); }
        finally { editing = false; if (IsVisible) await LoadAsync(); }
    }
}
