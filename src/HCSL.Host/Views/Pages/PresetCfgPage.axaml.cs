using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

/// <summary>CFG: everything that edits the managed preset's configuration files.</summary>
public partial class PresetCfgPage : UserControl
{
    private HostServerController? subscribed;
    private bool operating;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public PresetCfgPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) Update(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        Update();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = null;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible) Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var ready = !operating && m.Server.Connected && !m.Busy;
        EditCfgButton.IsEnabled = ready;
        OpenCfgButton.IsEnabled = OpenAddonsButton.IsEnabled = ready;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    /// <summary>The managed preset is the one the preset scope was opened for, not the instance's current one.</summary>
    private bool Scope(out HostServerController model, out InstanceDefinition instance, out PresetDefinition preset)
    {
        model = Model!;
        instance = null!;
        preset = null!;
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } d) return false;
        if ((DataContext as ShellViewModel)?.ManagedPresetId is not { } id) return false;
        if (m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == id) is not { } p) return false;
        model = m;
        instance = d;
        preset = p;
        return true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (operating) return;
        operating = true;
        Update();
        try { await action(); }
        catch (Exception) { Error("操作未完成", "请检查宿主状态后重试。"); }
        finally { operating = false; Update(); }
    }

    private async void EditCfg(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!Scope(out var model, out var instance, out var preset) || Owner is not { } owner) return;
        await owner.EditCfgAsync(model, instance, preset);
    });

    private async void OpenCfg(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!Scope(out var model, out var instance, out var preset)) return;
        if (!await model.OpenPresetCfgAsync(instance.Id, preset.Id)) Error("无法打开 CFG 目录", model.Error);
    });

    private async void OpenAddons(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!Scope(out var model, out var instance, out var preset)) return;
        if (!await model.OpenPresetDirectoryAsync(instance.Id, preset.Id, "addons")) Error("无法打开预设 addons", model.Error);
    });
}
