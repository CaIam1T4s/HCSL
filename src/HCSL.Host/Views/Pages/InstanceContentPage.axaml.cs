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
    private bool operating, loaded;
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
        if (IsVisible) Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var connected = m.Server.Connected && !m.Busy;
        var preset = m.Selected?.Entry.Presets.FirstOrDefault(p => p.Id == m.Selected.Entry.Definition?.SelectedPresetId);
        FrameworkButton.IsEnabled = connected && !operating && preset is not null;
        PluginButton.IsEnabled = connected && !operating && preset is not null;
        RefreshButton.IsEnabled = connected && !operating;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    private async Task LoadAsync()
    {
        if (Model is not { } m || !m.Server.Connected) return;
        loaded = true;
        try
        {
            var check = await m.CheckFrameworksAsync();
            Frameworks.ItemsSource = check.Frameworks.Select(f => new FrameworkRow(
                f.DisplayName,
                f.InstalledVersion ?? "未安装",
                f.LatestVersion ?? "—",
                f.Error is { Length: > 0 } error ? error : f.UpdateAvailable ? "可更新" : f.InstalledVersion is null ? "未安装" : "已是最新")).ToArray();
            if (m.Selected?.Entry.Definition is { } definition)
            {
                var plugins = await m.PluginsAsync(new PluginCommand(PluginAction.List, definition.Id, definition.SelectedPresetId));
                PluginSummary.Text = plugins.Selected.Length == 0
                    ? "插件：未启用"
                    : "插件：" + string.Join("、", plugins.Selected.Select(p => p.Id + " " + p.Version));
            }
        }
        catch (IOException error) { Error("读取内容失败", error.Message); loaded = false; }
        finally { Update(); }
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
            m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == instance.SelectedPresetId) is not { } preset) return;
        operating = true;
        Update();
        try { await owner.EditFrameworksAsync(m, instance, preset); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }

    private async void EditPlugins(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance ||
            m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == instance.SelectedPresetId) is not { } preset) return;
        operating = true;
        Update();
        try { await owner.EditPluginsAsync(m, instance, preset); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }
}
