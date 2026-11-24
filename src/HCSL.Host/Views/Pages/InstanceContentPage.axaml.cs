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

/// <summary>内容与插件: framework install state plus the selected plugin manifest of the current preset.</summary>
public partial class InstanceContentPage : UserControl
{
    private HostServerController? subscribed;
    private bool operating, loaded, loading;
    private FrameworkRow[] frameworkRows = [];
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    /// <summary>One table row; Arc binds by property name, so this stays a plain record.</summary>
    public sealed record FrameworkRow(string DisplayName, string Installed, string Latest, string State, string Enabled = "-", string Id = "");

    public InstanceContentPage()
    {
        InitializeComponent();
        RefreshButton.Icon = Lucide.Geometry(LucideIconKind.RotateCw);
        FrameworkSelectionBar.Actions.Add(new SelectionToolbarAction { Key = "manage", Label = "管理所选框架", IsEnabled = false });
        FrameworkSelectionBar.Labels = new SelectionToolbarLabels { Count = count => $"已选 {count}", Clear = "清除选择", Toolbar = "框架操作" };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) _ = LoadAsync(); };
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) =>
        {
            if (subscribed is not null) subscribed.PropertyChanged -= Changed;
            subscribed = null;
            loaded = false;
        };
    }

    private void Subscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        Update();
        if (IsVisible && !loaded) _ = LoadAsync();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        Update();
        // Framework/plugin reads flip Busy; only a different instance or preset justifies another read.
        if (loading || operating || Model is not { } m || m.Busy) return;
        if (m.Selected?.Entry.Definition is not { } definition) return;
        if ((definition.Id, ScopedPreset(definition)?.Id ?? definition.SelectedPresetId) == loadedKey) return;
        Dispatcher.UIThread.Post(() => { loaded = false; _ = LoadAsync(); });
    }

    private (Guid Instance, Guid Preset) loadedKey;

    /// <summary>The preset scope names its preset; outside it the instance's current preset applies.</summary>
    private PresetDefinition? ScopedPreset(InstanceDefinition definition)
    {
        if (Model is not { } m) return null;
        var id = (DataContext as ShellViewModel)?.ManagedPresetId ?? definition.SelectedPresetId;
        return m.Selected?.Entry.Presets.FirstOrDefault(p => p.Id == id);
    }

    private void Update()
    {
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } definition) return;
        var connected = m.Server.Connected && !m.Busy;
        var preset = ScopedPreset(definition);
        var selected = Frameworks.SelectedItems?.Count ?? 0;
        FrameworkSelectionBar.SelectedCount = selected;
        foreach (var action in FrameworkSelectionBar.Actions)
            if (action.Key == "manage") action.IsEnabled = connected && !operating && preset is not null && selected == 1;
        RefreshButton.IsEnabled = connected && !operating;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    private async Task LoadAsync()
    {
        if (loading) return;
        if (Model is not { } m || !m.Server.Connected) return;
        loading = true;
        loaded = true;
        if (m.Selected?.Entry.Definition is { } target)
            loadedKey = (target.Id, ScopedPreset(target)?.Id ?? target.SelectedPresetId);
        try
        {
            var rows = FrameworkRules.Known.Select(f => new FrameworkRow(f.DisplayName, "-", "-", "检查更新中", Id: f.Id)).ToArray();
            if (m.Selected?.Entry.Definition is { } definition && ScopedPreset(definition) is { } scoped)
            {
                var plugins = await m.PluginsAsync(new PluginCommand(PluginAction.List, definition.Id, scoped.Id));
                rows = FrameworkRules.Known.Select(f => new FrameworkRow(f.DisplayName,
                    FrameworkRules.OrderVersions(f.Id, plugins.Available.Where(p => p.Id == f.Id).Select(p => p.Version)).FirstOrDefault() ?? "未安装",
                    "-", "检查更新中", plugins.Selected.FirstOrDefault(p => p.Id == f.Id && p.Enabled)?.Version ?? "未启用", f.Id)).ToArray();
            }
            frameworkRows = rows;
            ApplyFrameworkFilter();
            await Task.WhenAll(FrameworkRules.Known.Select(async (f, i) =>
            {
                try
                {
                    if ((await m.CheckFrameworkAsync(f.Id)).Frameworks.FirstOrDefault() is { } result)
                        rows[i] = new FrameworkRow(f.DisplayName, result.InstalledVersion ?? "未安装", result.LatestVersion ?? "-",
                            result.Error ?? (result.UpdateAvailable ? "可更新" : result.InstalledVersion is null ? "未安装" : "已是最新"), rows[i].Enabled, f.Id);
                }
                catch (IOException) { rows[i] = rows[i] with { State = "无法检查更新" }; }
                ApplyFrameworkFilter();
            }));
        }
        catch (IOException error) { Error("读取内容失败", error.Message); loaded = false; }
        finally { loading = false; Update(); }
    }

    private async void RefreshFrameworks(object? sender, RoutedEventArgs e)
    {
        if (operating) return;
        operating = true;
        Update();
        try { loaded = false; await LoadAsync(); }
        finally { operating = false; Update(); }
    }

    private void FilterFrameworks(object? sender, FilterToolbarFiltersChangedEventArgs e) => ApplyFrameworkFilter();

    private void ApplyFrameworkFilter()
    {
        if (Frameworks is null || Filters is null) return;
        var query = (Filters.SearchText ?? "").Trim();
        var selected = Frameworks.SelectedItems?.OfType<FrameworkRow>().Select(row => row.Id).ToHashSet() ?? [];
        var visible = frameworkRows.Where(row => row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Frameworks.ItemsSource = visible;
        if (selected.Count > 0) Frameworks.SelectedItems = visible.Where(row => selected.Contains(row.Id)).ToList();
        Update();
    }

    private void FrameworkSelected(object? sender, DataTableSelectionChangedEventArgs e) => Update();

    private void ClearFrameworkSelection(object? sender, RoutedEventArgs e) => Frameworks.ClearSelection();

    private async void EditFrameworks(object? sender, SelectionToolbarActionEventArgs e)
    {
        if (operating || Model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance ||
            ScopedPreset(instance) is not { } preset ||
            Frameworks.SelectedItems?.OfType<FrameworkRow>().ToArray() is not [var row]) return;
        operating = true;
        Update();
        try { await owner.EditFrameworksAsync(m, instance, preset, row.Id); }
        finally { operating = false; loaded = false; Update(); owner.RefreshAdminAvailability(); await LoadAsync(); }
    }

}
