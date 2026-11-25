using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views.Pages;

public partial class PresetPluginsPage : UserControl
{
    private sealed record Row(string Key, string Name, string Version = "", string State = "",
        bool Enabled = false, bool CanEnable = false, string[]? Versions = null, Row[]? Children = null);
    private int generation;
    private Row[] rows = [];
    private PluginResult? snapshot;
    private bool editing;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetPluginsPage()
    {
        InitializeComponent();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        ManageButton.Icon = Lucide.Geometry(LucideIconKind.Plus);
        TreeTableHeaderAlignment.Attach(Plugins);
        Plugins.KeySelector = row => ((Row)row).Key;
        Plugins.ChildrenSelector = row => ((Row)row).Children;
        Plugins.RowLabelSelector = row => ((Row)row).Name;
        Plugins.Columns.Add(TreeTableColumn.Create<Row>("插件", row => row.Name));
        Plugins.Columns.Add(new TreeTableColumn { Header = "版本", Width = new GridLength(160), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => VersionCell(row)) });
        Plugins.Columns.Add(new TreeTableColumn { Header = "启用", Width = new GridLength(72), IsSortable = false,
            CellTemplate = new FuncDataTemplate<Row>((row, _) => EnabledCell(row)) });
        Plugins.Columns.Add(new TreeTableColumn { Header = "状态", Width = new GridLength(156), ValueSelector = row => ((Row)row).State });
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            if (IsVisible) _ = LoadAsync(); else { generation++; snapshot = null; }
        };
        DataContextChanged += (_, _) => { if (IsVisible) _ = LoadAsync(); };
    }

    private Control? VersionCell(Row? row)
    {
        if (row is not { Versions: { Length: > 0 } versions })
            return row is { Children: null } ? new TextBlock { Text = row.Version } : null;
        var picker = new Select { ItemsSource = versions, SelectedIndex = Array.IndexOf(versions, row.Version),
            IsEnabled = versions.Length > 1 && Scope()?.Model.CanEditPlugins == true };
        picker.SelectionChanged += async (_, _) =>
        {
            if (picker.SelectedItem is string version && version != row.Version)
                await SaveAsync(row, version, row.Enabled);
        };
        return picker;
    }

    private Control? EnabledCell(Row? row)
    {
        if (row is not { Children: null }) return null;
        var check = new TKCode.Arc.Checkbox { IsChecked = row.Enabled,
            IsEnabled = (row.Enabled || row.CanEnable) && Scope()?.Model.CanEditPlugins == true };
        Avalonia.Automation.AutomationProperties.SetName(check, "在当前预设启用 " + row.Name);
        check.CheckedStateChanged += async (_, _) =>
        {
            if ((check.IsChecked == true) != row.Enabled) await SaveAsync(row, row.Version, check.IsChecked == true);
        };
        return check;
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
            var result = await scope.Model.PluginsAsync(new(PluginAction.List, scope.Instance.Id, scope.Preset.Id));
            if (!Current()) return;
            snapshot = result;
            Render(result);
            ManageButton.IsEnabled = scope.Model.Server.Connected && !editing;
        }
        catch (IOException ex) { if (Current()) Owner?.ShowInstanceNotification("无法读取插件", ex.Message); }
    }

    private static bool IsPlugin(PluginPackageInfo package) => FrameworkRules.Find(package.Id) is null &&
        !package.FrameworkPayload && package.Category != PluginCategory.Framework;

    private static bool HasFramework(PluginResult result, PluginPackageInfo package) => package.Framework is not { } required ||
        result.Selected.Any(s => s.Id == required && s.Enabled && s.Available);

    private void Render(PluginResult result)
    {
        var packages = result.Available.Where(IsPlugin).ToLookup(p => p.Id);
        var referenced = result.Selected.Where(s => FrameworkRules.Find(s.Id) is null &&
            !result.Available.Any(p => p.Id == s.Id && p.Version == s.Version && !IsPlugin(p))).ToDictionary(s => s.Id);
        rows = packages.Select(p => p.Key).Concat(referenced.Keys).Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                referenced.TryGetValue(id, out var selection);
                var variants = packages[id].OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase).ToArray();
                var version = selection?.Version ?? variants.FirstOrDefault()?.Version ?? "-";
                var chosen = variants.FirstOrDefault(p => p.Version == version);
                var fallback = chosen ?? variants.FirstOrDefault();
                var canEnable = chosen is not null && HasFramework(result, chosen);
                var state = chosen is null ? "包不可用" : !canEnable ? "需要 " + (FrameworkRules.Find(chosen.Framework!)?.DisplayName ?? chosen.Framework)
                    : selection?.Enabled == true ? "已启用" : "未启用";
                return (Category: fallback?.Category ?? PluginCategory.Other, Item: new Row(id,
                    chosen?.Name ?? selection?.Name ?? fallback?.Name ?? id, version, state, selection?.Enabled == true,
                    canEnable, variants.Select(p => p.Version).Append(version).Distinct().ToArray()));
            }).OrderBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase).GroupBy(x => x.Category)
            .Select(group => new Row("category:" + group.Key, PluginPresentation.CategoryLabel(group.Key),
                Children: group.Select(x => x.Item).ToArray())).ToArray();
        ApplyFilter();
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
        Plugins.ItemsSource = visible;
        Plugins.ExpandedKeys = visible.Select(row => (object)row.Key).ToArray();
    }

    private async Task SaveAsync(Row row, string version, bool enabled)
    {
        if (editing || snapshot is not { } current || Scope() is not { } scope) return;
        var package = current.Available.FirstOrDefault(p => p.Id == row.Key && p.Version == version && IsPlugin(p));
        if (!scope.Model.CanEditPlugins || (enabled && (package is null || !HasFramework(current, package))) ||
            (version != row.Version && package is null))
        {
            Owner?.ShowInstanceNotification("无法修改插件", "请确认预设可修改，且选定版本及所需框架可用。");
            Render(current);
            return;
        }
        var next = current.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToList();
        var index = next.FindIndex(p => p.Id == row.Key);
        var choice = new PluginSelection(row.Key, version, enabled);
        if (index < 0) next.Add(choice); else next[index] = choice;
        editing = true;
        Plugins.IsEnabled = ManageButton.IsEnabled = false;
        try
        {
            await scope.Model.PluginsAsync(new(PluginAction.Save, scope.Instance.Id, scope.Preset.Id,
                scope.Instance.Revision, current.Hash, [.. next]));
            Owner?.ShowInstanceNotification("预设插件已保存", "下次部署生效。", ToastKind.Success);
        }
        catch (IOException ex) { Owner?.ShowInstanceNotification("无法保存插件", ex.Message); }
        finally
        {
            editing = false;
            Plugins.IsEnabled = true;
            if (IsVisible) await LoadAsync();
        }
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
