using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;

namespace HCSL.Host.Views.Pages;

public partial class InstanceContentPage : UserControl
{
    private sealed record Row(FrameworkDefinition Framework, string Name, string State, string? PresetVersion)
    {
        public bool CanDisable => PresetVersion is not null;
    }
    private HostServerController? observed;
    private bool loading, opening;
    private (Guid Instance, Guid Preset)? loaded;

    public InstanceContentPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => Subscribe();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            loaded = null;
            _ = LoadAsync();
        };
    }

    private void Subscribe()
    {
        Unsubscribe();
        observed = (DataContext as ShellViewModel)?.Servers;
        if (observed is not null) observed.PropertyChanged += Changed;
        _ = LoadAsync();
    }

    private void Unsubscribe()
    {
        if (observed is not null) observed.PropertyChanged -= Changed;
        observed = null;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() => _ = LoadAsync());

    private (HostServerController Model, InstanceDefinition Instance, PresetDefinition Preset)? Scope()
    {
        if (DataContext is ShellViewModel { ManagedPresetId: { } id } shell &&
            shell.Servers.Selected?.Entry is { Definition: { } instance } entry &&
            entry.Presets.FirstOrDefault(p => p.Id == id) is { } preset)
            return (shell.Servers, instance, preset);
        return null;
    }

    private async Task LoadAsync()
    {
        if (!IsVisible || loading || opening || Scope() is not { } scope || !scope.Model.Server.Connected) return;
        var key = (scope.Instance.Id, scope.Preset.Id);
        if (loaded == key) return;
        loading = true;
        loaded = key;
        Frameworks.ItemsSource = null;
        try
        {
            var plugins = await scope.Model.PluginsAsync(new(PluginAction.List, key.Item1, key.Item2, FrameworkOverviewOnly: true));
            if (Scope()?.Preset.Id != key.Item2) return;
            var current = FrameworkSelection.Current(plugins);
            Frameworks.ItemsSource = FrameworkRules.Known.Select(framework =>
            {
                var version = plugins.Selected.FirstOrDefault(s => s.Id == framework.Id && s.Enabled)?.Version;
                var state = framework.Kind == FrameworkKind.ScriptFramework && current is not null && current != framework.Id
                    ? "与 " + (FrameworkRules.Find(current)?.DisplayName ?? current) + " 不兼容"
                    : version ?? "未安装";
                return new Row(framework, framework.DisplayName, state, version);
            }).ToArray();
        }
        catch (IOException e)
        {
            if (IsVisible && Scope()?.Preset.Id == key.Item2 && TopLevel.GetTopLevel(this) is MainWindow owner)
                owner.ShowInstanceNotification("无法读取框架", e.Message);
        }
        finally
        {
            loading = false;
            if (Scope()?.Preset.Id != key.Item2) { loaded = null; _ = LoadAsync(); }
        }
    }

    private async void DisableFramework(object? sender, RoutedEventArgs e)
    {
        if (opening || sender is not Control { DataContext: Row { CanDisable: true } row } ||
            Scope() is not { } scope || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        opening = true;
        try
        {
            if (!scope.Model.CanEditPlugins) throw new IOException("请停止服务器并等待当前操作完成后再停用框架。");
            var current = await scope.Model.PluginsAsync(new(PluginAction.List, scope.Instance.Id, scope.Preset.Id, PackageMetadataOnly: true));
            var selected = current.Selected.FirstOrDefault(p => p.Id == row.Framework.Id && p.Enabled);
            if (selected?.Version != row.PresetVersion || Scope() != scope) return;
            var next = current.Selected.Select(p => new PluginSelection(p.Id, p.Version, p.Enabled && p.Id != row.Framework.Id)).ToArray();
            // Do not silently disable dependent plugins or frameworks.
            FrameworkRules.ValidateSelections(next.Where(p => p.Enabled).Select(p =>
            {
                var package = current.Available.FirstOrDefault(a => a.Id == p.Id && a.Version == p.Version);
                return new FrameworkRules.SelectedFramework(p.Id, package?.Framework, package?.FrameworkPayload ?? false);
            }));
            await scope.Model.PluginsAsync(new(PluginAction.Save, scope.Instance.Id, scope.Preset.Id,
                scope.Instance.Revision, current.Hash, next, PackageMetadataOnly: true));
        }
        catch (Exception ex) when (ex is IOException or InstanceException)
        { owner.ShowInstanceNotification("无法停用框架", ex.Message); }
        finally { opening = false; loaded = null; await LoadAsync(); }
    }

    private async void OpenFramework(object? sender, RoutedEventArgs e)
    {
        if (opening || sender is not Control { DataContext: Row row } || Scope() is not { } scope ||
            TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        opening = true;
        try { await owner.OpenFrameworkDownloadsAsync(row.Framework.Id, scope.Instance.Id, scope.Preset.Id, row.PresetVersion); }
        finally
        {
            opening = false;
            loaded = null;
            await LoadAsync();
        }
    }
}
