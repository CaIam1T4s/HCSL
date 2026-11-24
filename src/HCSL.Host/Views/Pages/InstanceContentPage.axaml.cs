using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

/// <summary>内容与插件: framework install state plus the selected plugin manifest of the current preset.</summary>
public partial class InstanceContentPage : UserControl
{
    private HostServerController? subscribed;
    private bool operating, loaded, loading;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    /// <summary>One table row; Arc binds by property name, so this stays a plain record.</summary>
    public sealed record FrameworkRow(string DisplayName, string Installed, string Latest, string State);

    public InstanceContentPage()
    {
        InitializeComponent();
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
        FrameworkButton.IsEnabled = connected && !operating && preset is not null;
        PluginButton.IsEnabled = connected && !operating && preset is not null;
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
            var rows = FrameworkRules.Known.Select(f => new FrameworkRow(f.DisplayName, "-", "-", "检查更新中")).ToArray();
            if (m.Selected?.Entry.Definition is { } definition && ScopedPreset(definition) is { } scoped)
            {
                var plugins = await m.PluginsAsync(new PluginCommand(PluginAction.List, definition.Id, scoped.Id));
                rows = FrameworkRules.Known.Select(f => new FrameworkRow(f.DisplayName,
                    FrameworkRules.OrderVersions(f.Id, plugins.Available.Where(p => p.Id == f.Id).Select(p => p.Version)).FirstOrDefault() ?? "未安装",
                    "-", "检查更新中")).ToArray();
                PluginSummary.Text = plugins.Selected.Length == 0
                    ? "插件：未启用"
                    : "插件：" + string.Join("、", plugins.Selected.Select(p => p.Id + " " + p.Version));
            }
            Frameworks.ItemsSource = rows.ToArray();
            await Task.WhenAll(FrameworkRules.Known.Select(async (f, i) =>
            {
                try
                {
                    if ((await m.CheckFrameworkAsync(f.Id)).Frameworks.FirstOrDefault() is { } result)
                        rows[i] = new FrameworkRow(f.DisplayName, result.InstalledVersion ?? "未安装", result.LatestVersion ?? "-",
                            result.Error ?? (result.UpdateAvailable ? "可更新" : result.InstalledVersion is null ? "未安装" : "已是最新"));
                }
                catch (IOException) { rows[i] = rows[i] with { State = "无法检查更新" }; }
                Frameworks.ItemsSource = rows.ToArray();
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

    private async void EditFrameworks(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance ||
            ScopedPreset(instance) is not { } preset) return;
        operating = true;
        Update();
        try { await owner.EditFrameworksAsync(m, instance, preset); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }

    private async void EditPlugins(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance ||
            ScopedPreset(instance) is not { } preset) return;
        operating = true;
        Update();
        try { await owner.EditPluginsAsync(m, instance, preset); }
        finally
        {
            operating = false;
            loaded = false;
            Update();
            await LoadAsync();
            // A newly enabled framework can make the admin list readable, so the sidebar re-checks it.
            owner.RefreshAdminAvailability();
        }
    }
}
