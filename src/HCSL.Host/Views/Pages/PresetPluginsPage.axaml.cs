using Avalonia.Controls;
using Avalonia.Interactivity;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

public partial class PresetPluginsPage : UserControl
{
    private sealed record Row(string Key, string Name, string Version = "", string State = "", Row[]? Children = null);
    private int generation;
    private bool editing;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetPluginsPage()
    {
        InitializeComponent();
        TreeTableHeaderAlignment.Attach(Plugins);
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
        Plugins.ItemsSource = null;
        if (Scope() is not { } scope) return;
        bool Current() => ticket == generation && Scope()?.Preset.Id == scope.Preset.Id;
        try
        {
            var result = await scope.Model.PluginsAsync(new(PluginAction.List, scope.Instance.Id, scope.Preset.Id));
            if (!Current()) return;
            var packages = result.Available.ToLookup(p => (p.Id, p.Version));
            var rows = result.Selected.Select(s => (Selection: s, Package: packages[(s.Id, s.Version)].FirstOrDefault()))
                .Where(x => FrameworkRules.Find(x.Selection.Id) is null && x.Package is not { FrameworkPayload: true } and not { Category: PluginCategory.Framework })
                .GroupBy(x => x.Package?.Category ?? PluginCategory.Other)
                .Select(group => new Row("category:" + group.Key, PluginPresentation.CategoryLabel(group.Key), Children:
                    group.Select(x => new Row(x.Selection.Id, x.Selection.Name ?? x.Selection.Id, x.Selection.Version,
                        !x.Selection.Available ? "包不可用" : x.Selection.Enabled ? "已启用" : "已禁用")).ToArray())).ToArray();
            Plugins.ItemsSource = rows;
            Plugins.ExpandedKeys = rows.Select(row => (object)row.Key).ToArray();
            ManageButton.IsEnabled = scope.Model.Server.Connected && !editing;
        }
        catch (IOException ex) { if (Current()) Owner?.ShowInstanceNotification("无法读取插件", ex.Message); }
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
